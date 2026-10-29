-- M8 Portfolio Allocator (m8-protocol §4–§7, ADR-0023): allocator mode events, proposals and shadow-engine steps;
-- the non-inferiority qualification design; the Allocation job kind. All new tables are append-only. research_app
-- gains nothing: the allocator tables are derived from live data and stay in the trading schema.

-- Mode state machine (§4.2). The latest row is the mode; without a row the allocator is disabled.
CREATE TABLE trading.allocator_mode_events (
    id                bigserial   PRIMARY KEY,
    recorded_at       timestamptz NOT NULL DEFAULT now(),
    from_mode         text        NOT NULL CHECK (from_mode IN ('Disabled', 'Shadow', 'Active')),
    to_mode           text        NOT NULL CHECK (to_mode IN ('Disabled', 'Shadow', 'Active')),
    protocol_hash     text        CHECK (protocol_hash ~ '^[0-9a-f]{64}$'),
    qualification_id  bigint,
    actor             text        NOT NULL,
    reason            text        NOT NULL CHECK (length(btrim(reason)) > 0),
    evidence          jsonb       NOT NULL
);

-- One decision per run, bar and role (shadow engine or the real run in active mode). Items carry the T11 weight, the
-- proposed and applied multiplier, a veto and the band clamp; no text of the model is stored.
CREATE TABLE trading.allocator_proposals (
    id             bigserial   PRIMARY KEY,
    recorded_at    timestamptz NOT NULL DEFAULT now(),
    run_id         bigint      NOT NULL REFERENCES trading.paper_runs (id),
    bar_time       timestamptz NOT NULL,
    role           text        NOT NULL CHECK (role IN ('shadow', 'active')),
    outcome        text        NOT NULL,
    task_id        uuid,
    protocol_hash  text        CHECK (protocol_hash ~ '^[0-9a-f]{64}$'),
    items          jsonb       NOT NULL,
    safety         jsonb       NOT NULL,
    UNIQUE (run_id, bar_time, role)
);

-- The shadow engine's steps (§3, ADR-0023 §3): its overrides rebuild it on restart without a model call; its state
-- hash verifies the rebuild; the shadow and real values give the daily marks of the evaluation (§6.2).
CREATE TABLE trading.allocator_shadow_steps (
    id             bigserial   PRIMARY KEY,
    recorded_at    timestamptz NOT NULL DEFAULT now(),
    run_id         bigint      NOT NULL REFERENCES trading.paper_runs (id),
    bar_time       timestamptz NOT NULL,
    protocol_hash  text        CHECK (protocol_hash ~ '^[0-9a-f]{64}$'),
    overrides      jsonb       NOT NULL,
    state_hash     text        NOT NULL CHECK (state_hash ~ '^[0-9a-f]{64}$'),
    shadow_equity  numeric     NOT NULL,
    real_equity    numeric     NOT NULL,
    UNIQUE (run_id, bar_time)
);

DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['trading.allocator_mode_events', 'trading.allocator_proposals', 'trading.allocator_shadow_steps']
    LOOP
        EXECUTE format('CREATE TRIGGER append_only BEFORE UPDATE OR DELETE ON %s FOR EACH ROW EXECUTE FUNCTION audit.reject_change()', t);
    END LOOP;
END
$$;

-- Non-inferiority design (§6): margin in minimum_effect, window days in sample_per_arm, no p0.
ALTER TABLE ai.qualification_protocols DROP CONSTRAINT qualification_protocols_design_check;
ALTER TABLE ai.qualification_protocols ADD CONSTRAINT qualification_protocols_design_check
    CHECK (design IN ('TwoArmFisher', 'OneArmBinomial', 'NonInferiority'));
ALTER TABLE ai.qualification_protocols DROP CONSTRAINT qualification_protocols_p0_check;
ALTER TABLE ai.qualification_protocols ADD CONSTRAINT qualification_protocols_p0_check
    CHECK (design <> 'OneArmBinomial' OR (p0 > 0 AND p0 < 1));

-- Production calls of an active allocator (ADR-0023 §4).
ALTER TABLE ai.cost_ledger DROP CONSTRAINT cost_ledger_job_kind_check;
ALTER TABLE ai.cost_ledger ADD CONSTRAINT cost_ledger_job_kind_check
    CHECK (job_kind IN ('Qualification', 'ResearchCycle', 'Spike', 'Allocation'));

ALTER TABLE trading.ops_actions DROP CONSTRAINT ops_actions_action_check;
ALTER TABLE trading.ops_actions ADD CONSTRAINT ops_actions_action_check
    CHECK (action IN ('disable-strategy', 'close-position', 'close-all', 'testnet-activate', 'testnet-rebaseline', 'allocator-mode'));

-- Yes/no checks of the qualification records for the mode rules. They run with the owner's privileges, so ops_api
-- needs no grant on the ai qualification tables (its dashboard grants of 0008 stay as they are).
CREATE FUNCTION trading.allocator_protocol_registered(hash text) RETURNS boolean
    LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT EXISTS (SELECT 1 FROM ai.qualification_protocols p
                   WHERE p.protocol_hash = hash AND p.task_class = 'AllocationProposal' AND p.design = 'NonInferiority')
$$;

CREATE FUNCTION trading.allocator_qualification_passed(qualification bigint) RETURNS boolean
    LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT EXISTS (SELECT 1 FROM ai.task_qualifications q JOIN ai.qualification_protocols p ON p.protocol_hash = q.protocol_hash
                   WHERE q.id = qualification AND q.verdict = 'Passed' AND p.task_class = 'AllocationProposal'
                     AND p.design = 'NonInferiority' AND p.sample_per_arm >= 28)
$$;

-- Mode rules (§4.2): written only through the user's Ops API session. Disabled is always allowed. Shadow needs
-- confirm and a registered non-inferiority AllocationProposal protocol. Active comes only from Shadow and needs
-- confirm and a Passed qualification of such a protocol with a window of at least 28 days.
CREATE FUNCTION trading.allocator_mode_rules() RETURNS trigger
    LANGUAGE plpgsql AS $$
DECLARE
    current_mode text;
BEGIN
    IF NOT (current_user = 'ops_api' OR pg_has_role(current_user, 'ops_api', 'MEMBER')) THEN
        RAISE EXCEPTION 'allocator: the mode is written only by the user (Ops API)';
    END IF;
    SELECT to_mode INTO current_mode FROM trading.allocator_mode_events ORDER BY id DESC LIMIT 1;
    IF coalesce(current_mode, 'Disabled') <> NEW.from_mode THEN
        RAISE EXCEPTION 'allocator: % is not the current mode (%)', NEW.from_mode, coalesce(current_mode, 'Disabled');
    END IF;
    IF NEW.to_mode = 'Disabled' THEN
        RETURN NEW;
    END IF;
    IF (NEW.evidence -> 'confirm') IS DISTINCT FROM 'true'::jsonb THEN
        RAISE EXCEPTION 'allocator: % needs confirm = true', NEW.to_mode;
    END IF;
    IF NEW.to_mode = 'Shadow' AND NOT coalesce(trading.allocator_protocol_registered(NEW.protocol_hash), false) THEN
        RAISE EXCEPTION 'allocator: Shadow needs a registered AllocationProposal protocol';
    END IF;
    IF NEW.to_mode = 'Active' THEN
        IF NEW.from_mode <> 'Shadow' THEN
            RAISE EXCEPTION 'allocator: Active comes only from Shadow';
        END IF;
        IF NOT coalesce(trading.allocator_qualification_passed(NEW.qualification_id), false) THEN
            RAISE EXCEPTION 'allocator: Active needs a Passed AllocationProposal qualification with a window of at least 28 days';
        END IF;
    END IF;
    RETURN NEW;
END
$$;

CREATE TRIGGER allocator_mode_rules BEFORE INSERT ON trading.allocator_mode_events
    FOR EACH ROW EXECUTE FUNCTION trading.allocator_mode_rules();

-- trading_app: the cycle reads the mode and the revealed cohort outcomes, writes proposals and shadow steps.
GRANT SELECT ON trading.allocator_mode_events, research.cohort_status TO trading_app;
GRANT SELECT, INSERT ON trading.allocator_proposals, trading.allocator_shadow_steps TO trading_app;
GRANT USAGE ON SEQUENCE trading.allocator_proposals_id_seq, trading.allocator_shadow_steps_id_seq TO trading_app;

-- ops_api (the user): the mode transition and the dashboard; the qualification checks go through the two functions.
GRANT SELECT ON trading.allocator_mode_events, trading.allocator_proposals, trading.allocator_shadow_steps TO ops_api;
GRANT INSERT ON trading.allocator_mode_events TO ops_api;
GRANT USAGE ON SEQUENCE trading.allocator_mode_events_id_seq TO ops_api;
REVOKE EXECUTE ON FUNCTION trading.allocator_protocol_registered(text), trading.allocator_qualification_passed(bigint) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION trading.allocator_protocol_registered(text), trading.allocator_qualification_passed(bigint) TO ops_api;
