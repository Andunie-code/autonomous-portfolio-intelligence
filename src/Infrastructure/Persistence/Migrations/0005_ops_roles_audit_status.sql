-- M3: ops-side objects of architecture.md §6.2–§6.4 that the evaluator pipeline needs (synthetic in M3).
--  * audit.events: append-only audit trail (H8, H11).
--  * research.cohort_status: the disclosure gate; only status_relay may insert, research_app may only read (D24).
--  * research.strategy_versions: immutable registry strategy versions (V14).
--  * NOLOGIN roles research_app, trading_app and status_relay with their grants. Logins, passwords and pg_hba
--    rules come with the real local environment in M5 (ADR-0013); tests use SET ROLE.

CREATE TABLE audit.events (
    id            bigserial   PRIMARY KEY,
    recorded_at   timestamptz NOT NULL DEFAULT now(),
    actor         text        NOT NULL,
    event         text        NOT NULL,
    payload       jsonb       NOT NULL,
    payload_hash  text        NOT NULL CHECK (payload_hash ~ '^[0-9a-f]{64}$')
);

CREATE TABLE research.cohort_status (
    id            bigserial   PRIMARY KEY,
    recorded_at   timestamptz NOT NULL DEFAULT now(),
    cohort_id     bigint      NOT NULL,
    status        text        NOT NULL CHECK (status IN ('sealed', 'revealed')),
    -- Revealed rounded summary only (m3-protocol §9); never raw data.
    summary       jsonb,
    CHECK ((status = 'revealed') = (summary IS NOT NULL))
);

CREATE TABLE research.strategy_versions (
    id               bigserial   PRIMARY KEY,
    registered_at    timestamptz NOT NULL DEFAULT now(),
    name             text        NOT NULL,
    definition_hash  text        NOT NULL UNIQUE CHECK (definition_hash ~ '^[0-9a-f]{64}$'),
    definition       jsonb       NOT NULL
);

CREATE FUNCTION audit.reject_change() RETURNS trigger
    LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '%.% is append-only', TG_TABLE_SCHEMA, TG_TABLE_NAME;
END
$$;

CREATE TRIGGER events_append_only BEFORE UPDATE OR DELETE ON audit.events
    FOR EACH ROW EXECUTE FUNCTION audit.reject_change();
CREATE TRIGGER cohort_status_append_only BEFORE UPDATE OR DELETE ON research.cohort_status
    FOR EACH ROW EXECUTE FUNCTION audit.reject_change();
CREATE TRIGGER strategy_versions_append_only BEFORE UPDATE OR DELETE ON research.strategy_versions
    FOR EACH ROW EXECUTE FUNCTION audit.reject_change();

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'research_app') THEN CREATE ROLE research_app NOLOGIN; END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'trading_app') THEN CREATE ROLE trading_app NOLOGIN; END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'status_relay') THEN CREATE ROLE status_relay NOLOGIN; END IF;
END
$$;

-- research_app: research data and its own registries; cohort_status read-only; no trading schema.
GRANT USAGE ON SCHEMA market, research, audit TO research_app;
GRANT SELECT ON ALL TABLES IN SCHEMA market TO research_app;
GRANT SELECT, INSERT ON research.experiments, research.trial_families, research.family_structures,
    research.trials, research.family_artifacts, research.strategy_versions TO research_app;
GRANT SELECT ON research.cohort_status TO research_app;
GRANT INSERT ON audit.events TO research_app;
GRANT USAGE ON ALL SEQUENCES IN SCHEMA research, audit TO research_app;
REVOKE USAGE ON SEQUENCE research.cohort_status_id_seq FROM research_app;

-- trading_app: reads immutable strategy versions (hash check before running, V14); writes audit.
GRANT USAGE ON SCHEMA research, audit TO trading_app;
GRANT SELECT ON research.strategy_versions TO trading_app;
GRANT INSERT ON audit.events TO trading_app;
GRANT USAGE ON SEQUENCE audit.events_id_seq TO trading_app;

-- status_relay: insert-only into cohort_status (D24); reads nothing.
GRANT USAGE ON SCHEMA research TO status_relay;
GRANT INSERT ON research.cohort_status TO status_relay;
GRANT USAGE ON SEQUENCE research.cohort_status_id_seq TO status_relay;
