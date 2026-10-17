-- M4: AI spend control, emergency stop, decision traces and qualification records (ADR-0010, ADR-0017), and the
-- hypothesis records of the research pipeline (m4-protocol.md §3). All tables are append-only.

CREATE SCHEMA IF NOT EXISTS ai;

-- AiKillSwitch: the latest row is the state. Only a user may clear it (actor 'user:<name>'); every row has a reason.
CREATE TABLE ai.kill_switch_events (
    id           bigserial   PRIMARY KEY,
    recorded_at  timestamptz NOT NULL DEFAULT now(),
    at           timestamptz NOT NULL,
    tripped      boolean     NOT NULL,
    actor        text        NOT NULL,
    trigger_kind text        NOT NULL CHECK (trigger_kind IN ('MonthlyCap', 'SpendVelocity', 'CostDeviation', 'CircuitBreaker', 'Manual', 'ManualClear')),
    reason       text        NOT NULL CHECK (length(btrim(reason)) > 0),
    CHECK (tripped OR (trigger_kind = 'ManualClear' AND actor LIKE 'user:_%')),
    CHECK (NOT tripped OR trigger_kind <> 'ManualClear')
);

-- Cost ledger at list prices (USD): one reservation per call before sending, one settlement after.
CREATE TABLE ai.cost_ledger (
    id             bigserial      PRIMARY KEY,
    recorded_at    timestamptz    NOT NULL DEFAULT now(),
    at             timestamptz    NOT NULL,
    call_id        uuid           NOT NULL,
    job_id         text           NOT NULL,
    job_kind       text           NOT NULL CHECK (job_kind IN ('Qualification', 'ResearchCycle', 'Spike')),
    task_class     text,
    entry_kind     text           NOT NULL CHECK (entry_kind IN ('Reservation', 'Settlement')),
    amount_usd     numeric(18, 10) NOT NULL CHECK (amount_usd >= 0),
    provider       text           NOT NULL,
    model          text           NOT NULL,
    input_tokens   integer        NOT NULL DEFAULT 0 CHECK (input_tokens >= 0),
    output_tokens  integer        NOT NULL DEFAULT 0 CHECK (output_tokens >= 0),
    price_version  text           NOT NULL,
    UNIQUE (call_id, entry_kind)
);

CREATE INDEX cost_ledger_at ON ai.cost_ledger (at);
CREATE INDEX cost_ledger_job ON ai.cost_ledger (job_id);

CREATE TABLE ai.decision_traces (
    task_id                      uuid        PRIMARY KEY,
    recorded_at                  timestamptz NOT NULL DEFAULT now(),
    at                           timestamptz NOT NULL,
    job_id                       text        NOT NULL,
    task_class                   text        NOT NULL,
    mode                         text        NOT NULL CHECK (mode IN ('Production', 'Qualification')),
    input_hash                   text        NOT NULL CHECK (input_hash ~ '^[0-9a-f]{64}$'),
    evidence_ids                 jsonb       NOT NULL,
    provider                     text,
    requested_model              text,
    actual_model                 text,
    prompt_version               text,
    qualification_id             bigint,
    qualification_protocol_hash  text,
    calls                        integer     NOT NULL,
    input_tokens                 integer     NOT NULL,
    output_tokens                integer     NOT NULL,
    latency_ms                   bigint      NOT NULL,
    estimated_usd                numeric(18, 10) NOT NULL,
    actual_usd                   numeric(18, 10) NOT NULL,
    validator_results            jsonb       NOT NULL,
    safety_violations            jsonb       NOT NULL,
    outcome                      text        NOT NULL CHECK (outcome IN ('Completed', 'Abstained', 'ManualReview', 'DeterministicFallback', 'Stopped')),
    degraded                     boolean     NOT NULL,
    reason                       text
);

CREATE TABLE ai.qualification_protocols (
    id                      bigserial   PRIMARY KEY,
    registered_at           timestamptz NOT NULL DEFAULT now(),
    protocol_hash           text        NOT NULL UNIQUE CHECK (protocol_hash ~ '^[0-9a-f]{64}$'),
    document_commit         text        NOT NULL,
    task_class              text        NOT NULL,
    provider                text        NOT NULL,
    model                   text        NOT NULL,
    prompt_version          text        NOT NULL,
    schema_hash             text        NOT NULL CHECK (schema_hash ~ '^[0-9a-f]{64}$'),
    pilot_set_hash          text        NOT NULL CHECK (pilot_set_hash ~ '^[0-9a-f]{64}$'),
    qualification_set_hash  text        NOT NULL CHECK (qualification_set_hash ~ '^[0-9a-f]{64}$'),
    alpha                   double precision NOT NULL CHECK (alpha > 0 AND alpha < 1),
    minimum_effect          double precision NOT NULL,
    sample_per_arm          integer     NOT NULL CHECK (sample_per_arm > 0),
    budget_usd              numeric(18, 10) NOT NULL CHECK (budget_usd > 0),
    CHECK (pilot_set_hash <> qualification_set_hash)
);

CREATE TABLE ai.task_qualifications (
    id                  bigserial   PRIMARY KEY,
    recorded_at         timestamptz NOT NULL DEFAULT now(),
    at                  timestamptz NOT NULL,
    protocol_hash       text        NOT NULL REFERENCES ai.qualification_protocols (protocol_hash),
    verdict             text        NOT NULL CHECK (verdict IN ('Passed', 'Failed', 'Inconclusive')),
    trials_llm          integer     NOT NULL,
    successes_llm       integer     NOT NULL,
    trials_baseline     integer     NOT NULL,
    successes_baseline  integer     NOT NULL,
    p_value             double precision NOT NULL,
    safety_violations   integer     NOT NULL CHECK (safety_violations >= 0),
    cost_usd            numeric(18, 10) NOT NULL,
    notes               text        NOT NULL,
    CHECK (safety_violations = 0 OR verdict = 'Failed')
);

-- Hypotheses of the research pipeline: origin, author, knowledge cutoff and the contamination flag (m4-protocol §3).
CREATE TABLE research.hypotheses (
    id                             bigserial   PRIMARY KEY,
    recorded_at                    timestamptz NOT NULL DEFAULT now(),
    hypothesis_key                 text        NOT NULL,
    protocol_version               text        NOT NULL,
    title                          text        NOT NULL,
    origin                         text        NOT NULL CHECK (origin IN ('llm', 'grid', 'human', 'dev-assisted')),
    author                         text        NOT NULL,
    knowledge_cutoff               date,
    knowledge_overlaps_diagnostic  boolean     NOT NULL,
    approved_by                    text,
    structure_hash                 text        NOT NULL CHECK (structure_hash ~ '^[0-9a-f]{64}$'),
    UNIQUE (hypothesis_key, protocol_version)
);

ALTER TABLE research.trials ADD COLUMN hypothesis_id bigint REFERENCES research.hypotheses (id);

CREATE TRIGGER kill_switch_events_append_only BEFORE UPDATE OR DELETE ON ai.kill_switch_events
    FOR EACH ROW EXECUTE FUNCTION audit.reject_change();
CREATE TRIGGER cost_ledger_append_only BEFORE UPDATE OR DELETE ON ai.cost_ledger
    FOR EACH ROW EXECUTE FUNCTION audit.reject_change();
CREATE TRIGGER decision_traces_append_only BEFORE UPDATE OR DELETE ON ai.decision_traces
    FOR EACH ROW EXECUTE FUNCTION audit.reject_change();
CREATE TRIGGER qualification_protocols_append_only BEFORE UPDATE OR DELETE ON ai.qualification_protocols
    FOR EACH ROW EXECUTE FUNCTION audit.reject_change();
CREATE TRIGGER task_qualifications_append_only BEFORE UPDATE OR DELETE ON ai.task_qualifications
    FOR EACH ROW EXECUTE FUNCTION audit.reject_change();
CREATE TRIGGER hypotheses_append_only BEFORE UPDATE OR DELETE ON research.hypotheses
    FOR EACH ROW EXECUTE FUNCTION audit.reject_change();

-- Both hosts route through the ModelRouter (architecture §1): append-only access to the ai schema.
GRANT USAGE ON SCHEMA ai TO research_app, trading_app;
GRANT SELECT, INSERT ON ALL TABLES IN SCHEMA ai TO research_app, trading_app;
GRANT USAGE ON ALL SEQUENCES IN SCHEMA ai TO research_app, trading_app;
GRANT SELECT, INSERT ON research.hypotheses TO research_app;
GRANT USAGE ON SEQUENCE research.hypotheses_id_seq TO research_app;
