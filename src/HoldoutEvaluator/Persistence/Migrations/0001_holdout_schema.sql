-- Holdout database schema (architecture.md §6.2–§6.5, ADR-0006, ADR-0009, ADR-0013, docs/m3-protocol.md §9).
-- M3: applied only to the synthetic database (holdout_synth); the real holdout database arrives in M5.
-- The authoritative cohort registry lives here; limits, server-assigned times, the early-evaluation guard and the
-- Validated gate are enforced by triggers, so they hold even when the evaluator application is bypassed.
-- Trigger functions are SECURITY DEFINER so roles never need direct rights on audit or limit-checking tables.

CREATE SCHEMA diagnostic;
CREATE SCHEMA prospective;
CREATE SCHEMA results;

CREATE FUNCTION results.research_cutoff() RETURNS timestamptz
    LANGUAGE sql IMMUTABLE AS $$ SELECT timestamptz '2026-01-01 00:00:00+00' $$;

CREATE FUNCTION results.publication_buffer() RETURNS interval
    LANGUAGE sql IMMUTABLE AS $$ SELECT interval '3 days' $$;

-- A_c = first UTC midnight strictly after registered_at + 24 h.
CREATE FUNCTION results.cohort_start(registered_at timestamptz) RETURNS timestamptz
    LANGUAGE sql IMMUTABLE AS $$
    SELECT (date_trunc('day', (registered_at + interval '24 hours') AT TIME ZONE 'UTC') + interval '1 day') AT TIME ZONE 'UTC'
$$;

-- ---------------------------------------------------------------------------------------------------------
-- Releases, environment flag and audit.
-- ---------------------------------------------------------------------------------------------------------
CREATE TABLE results.evaluator_releases (
    image_digest   text        PRIMARY KEY,
    engine_sha     text        NOT NULL,
    registered_at  timestamptz NOT NULL DEFAULT now()
);

-- HoldoutEnvironmentVerified (T3): set by the user session only; gates the Validated write and nothing else.
CREATE TABLE results.environment_flags (
    id           bigserial   PRIMARY KEY,
    recorded_at  timestamptz NOT NULL DEFAULT now(),
    verified     boolean     NOT NULL,
    actor        text        NOT NULL
);

CREATE FUNCTION results.environment_verified() RETURNS boolean
    LANGUAGE sql STABLE SECURITY DEFINER SET search_path = results, pg_temp AS $$
    SELECT coalesce((SELECT verified FROM results.environment_flags ORDER BY id DESC LIMIT 1), false)
$$;

CREATE TABLE results.audit_log (
    id            bigserial   PRIMARY KEY,
    recorded_at   timestamptz NOT NULL DEFAULT now(),
    event         text        NOT NULL,
    cohort_id     bigint,
    payload       jsonb       NOT NULL,
    payload_hash  text        NOT NULL
);

CREATE FUNCTION results.audit(event text, cohort bigint, payload jsonb) RETURNS void
    LANGUAGE sql SECURITY DEFINER SET search_path = results, pg_temp AS $$
    INSERT INTO results.audit_log (event, cohort_id, payload, payload_hash)
    VALUES (event, cohort, payload, encode(sha256(convert_to(payload::text, 'UTF8')), 'hex'))
$$;

-- ---------------------------------------------------------------------------------------------------------
-- Cohort registry.
-- ---------------------------------------------------------------------------------------------------------
CREATE TABLE results.cohort_manifests (
    cohort_id          bigserial   PRIMARY KEY,
    kind               text        NOT NULL DEFAULT 'normal' CHECK (kind IN ('normal', 'seed')),
    registered_at      timestamptz,
    a_c                timestamptz,
    l_c_months         integer     NOT NULL CHECK (l_c_months BETWEEN 6 AND 12),
    window_end         timestamptz,
    manifest           jsonb       NOT NULL,
    manifest_hash      text        NOT NULL UNIQUE CHECK (manifest_hash ~ '^[0-9a-f]{64}$'),
    protocol_version   text        NOT NULL,
    protocol_complete  boolean     NOT NULL,
    underpowered       boolean     NOT NULL,
    engine_sha         text        NOT NULL,
    image_digest       text        NOT NULL REFERENCES results.evaluator_releases (image_digest),
    CHECK (registered_at IS NOT NULL AND a_c IS NOT NULL AND window_end IS NOT NULL),
    CHECK (a_c > registered_at),
    CHECK (window_end > a_c)
);

CREATE TABLE results.cohort_members (
    cohort_id        bigint  NOT NULL REFERENCES results.cohort_manifests (cohort_id),
    member_index     integer NOT NULL CHECK (member_index BETWEEN 1 AND 3),
    strategy_name    text    NOT NULL,
    definition_hash  text    NOT NULL CHECK (definition_hash ~ '^[0-9a-f]{64}$'),
    structure_hash   text    NOT NULL CHECK (structure_hash ~ '^[0-9a-f]{64}$'),
    attempt          integer,
    PRIMARY KEY (cohort_id, member_index)
);

CREATE TABLE results.prospective_runs (
    run_id          bigserial   PRIMARY KEY,
    cohort_id       bigint      NOT NULL REFERENCES results.cohort_manifests (cohort_id),
    started_at      timestamptz NOT NULL DEFAULT now(),
    reason          text        NOT NULL CHECK (reason IN ('initial', 'rerun-data-quality', 'rerun-environment', 'rerun-nondeterminism', 'rerun-engine-defect')),
    rerun_of        bigint      REFERENCES results.prospective_runs (run_id),
    evidence        jsonb,
    manifest_hash   text        NOT NULL,
    image_digest    text        NOT NULL,
    data_digest     text        NOT NULL CHECK (data_digest ~ '^[0-9a-f]{64}$'),
    result_hash     text        NOT NULL CHECK (result_hash ~ '^[0-9a-f]{64}$'),
    CHECK ((reason = 'initial') = (rerun_of IS NULL)),
    CHECK (reason = 'initial' OR evidence IS NOT NULL)
);

CREATE TABLE results.prospective_results (
    id            bigserial   PRIMARY KEY,
    recorded_at   timestamptz NOT NULL DEFAULT now(),
    run_id        bigint      NOT NULL REFERENCES results.prospective_runs (run_id),
    cohort_id     bigint      NOT NULL,
    member_index  integer     NOT NULL,
    status        text        NOT NULL CHECK (status IN ('Validated', 'Failed', 'Inconclusive', 'Invalid')),
    detail        text,
    summary       jsonb       NOT NULL,
    FOREIGN KEY (cohort_id, member_index) REFERENCES results.cohort_members (cohort_id, member_index),
    CHECK (status <> 'Invalid' OR detail IS NOT NULL)
);

CREATE TABLE results.diagnostic_results (
    id                 bigserial   PRIMARY KEY,
    recorded_at        timestamptz NOT NULL DEFAULT now(),
    research_cutoff    timestamptz NOT NULL DEFAULT results.research_cutoff(),
    structure_hash     text        NOT NULL CHECK (structure_hash ~ '^[0-9a-f]{64}$'),
    definition_hash    text        NOT NULL CHECK (definition_hash ~ '^[0-9a-f]{64}$'),
    window_start       timestamptz NOT NULL,
    window_end         timestamptz NOT NULL,
    -- Diagnostic holdout eliminates only (D10, V11).
    verdict            text        NOT NULL CHECK (verdict IN ('NotRejected', 'Rejected')),
    knowledge_overlap  boolean     NOT NULL,
    metrics            jsonb       NOT NULL,
    result_hash        text        NOT NULL CHECK (result_hash ~ '^[0-9a-f]{64}$'),
    CHECK (window_start >= research_cutoff AND window_end > window_start)
);

-- Window data: diagnostic [C, A_c) and prospective [A_c, A_c + L_c) (+ warm-up), imported only by the evaluator.
CREATE TABLE diagnostic.candles (
    request_id    text        NOT NULL,
    symbol        text        NOT NULL,
    open_time     timestamptz NOT NULL,
    open          numeric     NOT NULL,
    high          numeric     NOT NULL,
    low           numeric     NOT NULL,
    close         numeric     NOT NULL,
    quote_volume  numeric     NOT NULL,
    PRIMARY KEY (request_id, symbol, open_time)
);

CREATE TABLE prospective.candles (
    cohort_id     bigint      NOT NULL REFERENCES results.cohort_manifests (cohort_id),
    symbol        text        NOT NULL,
    open_time     timestamptz NOT NULL,
    open          numeric     NOT NULL,
    high          numeric     NOT NULL,
    low           numeric     NOT NULL,
    close         numeric     NOT NULL,
    quote_volume  numeric     NOT NULL,
    PRIMARY KEY (cohort_id, symbol, open_time)
);

-- ---------------------------------------------------------------------------------------------------------
-- Triggers.
-- ---------------------------------------------------------------------------------------------------------
CREATE FUNCTION results.reject_change() RETURNS trigger
    LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '%.% is append-only', TG_TABLE_SCHEMA, TG_TABLE_NAME;
END
$$;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['evaluator_releases', 'environment_flags', 'audit_log', 'cohort_manifests', 'cohort_members',
                             'prospective_runs', 'prospective_results', 'diagnostic_results'] LOOP
        EXECUTE format('CREATE TRIGGER %I BEFORE UPDATE OR DELETE ON results.%I FOR EACH ROW EXECUTE FUNCTION results.reject_change()',
                       t || '_append_only', t);
    END LOOP;
END
$$;

-- Manifest: server-assigned registered_at / A_c / window end (V13), release check (H15), open-cohort limit (D21).
CREATE FUNCTION results.before_manifest() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER SET search_path = results, pg_temp AS $$
DECLARE
    release_sha text;
    open_count integer;
BEGIN
    PERFORM pg_advisory_xact_lock(hashtext('results.cohort_registry'));
    IF NEW.kind = 'seed' THEN
        -- Kohort 0 seed exception (architecture §6.3): only through results.seed_cohort, owner only.
        IF coalesce(current_setting('results.allow_seed', true), '') <> 'on' OR NEW.registered_at IS NULL THEN
            RAISE EXCEPTION 'seed cohorts are registered only through results.seed_cohort';
        END IF;
        IF NEW.a_c IS NOT NULL OR NEW.window_end IS NOT NULL THEN
            RAISE EXCEPTION 'A_c and the window end are always derived by the server';
        END IF;
    ELSE
        IF NEW.registered_at IS NOT NULL OR NEW.a_c IS NOT NULL OR NEW.window_end IS NOT NULL THEN
            RAISE EXCEPTION 'registered_at, A_c and the window end are assigned by the server';
        END IF;
        NEW.registered_at := now();
    END IF;
    NEW.a_c := results.cohort_start(NEW.registered_at);
    NEW.window_end := NEW.a_c + make_interval(months => NEW.l_c_months);

    SELECT engine_sha INTO release_sha FROM results.evaluator_releases WHERE image_digest = NEW.image_digest;
    IF release_sha IS NULL OR release_sha <> NEW.engine_sha THEN
        RAISE EXCEPTION 'engine SHA % does not match the evaluator release %', NEW.engine_sha, NEW.image_digest;
    END IF;

    SELECT count(*) INTO open_count FROM results.cohort_manifests c
    WHERE NOT EXISTS (SELECT 1 FROM results.prospective_results r WHERE r.cohort_id = c.cohort_id);
    IF open_count >= 2 THEN
        RAISE EXCEPTION 'open cohort limit reached (2)';
    END IF;
    RETURN NEW;
END
$$;

CREATE TRIGGER manifest_rules BEFORE INSERT ON results.cohort_manifests
    FOR EACH ROW EXECUTE FUNCTION results.before_manifest();

-- Members: only in the registering transaction (frozen afterwards), ≤ 3 per cohort, family attempts ≤ 2 (D21).
CREATE FUNCTION results.before_member() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER SET search_path = results, pg_temp AS $$
DECLARE
    cohort results.cohort_manifests;
    previous integer;
BEGIN
    PERFORM pg_advisory_xact_lock(hashtext('results.cohort_registry'));
    SELECT * INTO cohort FROM results.cohort_manifests WHERE cohort_id = NEW.cohort_id;
    IF cohort.kind = 'normal' AND cohort.registered_at <> now() THEN
        RAISE EXCEPTION 'cohort % is frozen', NEW.cohort_id;
    END IF;
    IF cohort.kind = 'seed' AND coalesce(current_setting('results.allow_seed', true), '') <> 'on' THEN
        RAISE EXCEPTION 'cohort % is frozen', NEW.cohort_id;
    END IF;
    IF (SELECT count(*) FROM results.cohort_members WHERE cohort_id = NEW.cohort_id) >= 3 THEN
        RAISE EXCEPTION 'a cohort holds at most 3 strategies';
    END IF;
    IF NEW.attempt IS NOT NULL THEN
        RAISE EXCEPTION 'the attempt number is assigned by the server';
    END IF;
    SELECT count(*) INTO previous FROM results.cohort_members WHERE structure_hash = NEW.structure_hash;
    IF previous >= 2 THEN
        RAISE EXCEPTION 'family quota reached: at most 2 prospective attempts per family';
    END IF;
    NEW.attempt := previous + 1;
    RETURN NEW;
END
$$;

CREATE TRIGGER member_rules BEFORE INSERT ON results.cohort_members
    FOR EACH ROW EXECUTE FUNCTION results.before_member();

CREATE FUNCTION results.after_member_check() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER SET search_path = results, pg_temp AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM results.cohort_members WHERE cohort_id = NEW.cohort_id) THEN
        RAISE EXCEPTION 'cohort % has no strategies', NEW.cohort_id;
    END IF;
    PERFORM results.audit('manifest-registered', NEW.cohort_id,
        jsonb_build_object('manifest_hash', NEW.manifest_hash, 'registered_at', NEW.registered_at, 'a_c', NEW.a_c,
                           'window_end', NEW.window_end, 'kind', NEW.kind));
    RETURN NULL;
END
$$;

CREATE CONSTRAINT TRIGGER manifest_has_members AFTER INSERT ON results.cohort_manifests
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION results.after_member_check();

-- Diagnostic quota K_D: one per family for life, ten per cutoff period (D21, H9).
CREATE FUNCTION results.before_diagnostic() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER SET search_path = results, pg_temp AS $$
BEGIN
    PERFORM pg_advisory_xact_lock(hashtext('results.diagnostic_quota'));
    IF EXISTS (SELECT 1 FROM results.diagnostic_results WHERE structure_hash = NEW.structure_hash) THEN
        RAISE EXCEPTION 'diagnostic quota: family already evaluated';
    END IF;
    IF (SELECT count(*) FROM results.diagnostic_results WHERE research_cutoff = NEW.research_cutoff) >= 10 THEN
        RAISE EXCEPTION 'diagnostic quota: 10 evaluations per cutoff period';
    END IF;
    PERFORM results.audit('diagnostic-recorded', NULL,
        jsonb_build_object('structure_hash', NEW.structure_hash, 'verdict', NEW.verdict, 'result_hash', NEW.result_hash));
    RETURN NEW;
END
$$;

CREATE TRIGGER diagnostic_quota BEFORE INSERT ON results.diagnostic_results
    FOR EACH ROW EXECUTE FUNCTION results.before_diagnostic();

-- Early evaluation and import guard (V4): only prospective.* and results.prospective_* are guarded.
CREATE FUNCTION results.window_closed(cohort bigint) RETURNS boolean
    LANGUAGE sql STABLE SECURITY DEFINER SET search_path = results, pg_temp AS $$
    SELECT coalesce((SELECT now() >= window_end + results.publication_buffer() FROM results.cohort_manifests WHERE cohort_id = cohort), false)
$$;

CREATE FUNCTION results.reject_early() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER SET search_path = results, pg_temp AS $$
BEGIN
    IF NOT results.window_closed(NEW.cohort_id) THEN
        RAISE EXCEPTION 'cohort % window has not closed (window end + publication buffer)', NEW.cohort_id;
    END IF;
    RETURN NEW;
END
$$;

CREATE TRIGGER candles_not_early BEFORE INSERT ON prospective.candles
    FOR EACH ROW EXECUTE FUNCTION results.reject_early();
CREATE TRIGGER runs_not_early BEFORE INSERT ON results.prospective_runs
    FOR EACH ROW EXECUTE FUNCTION results.reject_early();
CREATE TRIGGER results_not_early BEFORE INSERT ON results.prospective_results
    FOR EACH ROW EXECUTE FUNCTION results.reject_early();

-- Runs: same manifest, digest and data for re-evaluations; at most 2 re-runs for data quality and environment (V6).
CREATE FUNCTION results.before_run() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER SET search_path = results, pg_temp AS $$
DECLARE
    cohort results.cohort_manifests;
    original results.prospective_runs;
BEGIN
    SELECT * INTO cohort FROM results.cohort_manifests WHERE cohort_id = NEW.cohort_id;
    IF NEW.manifest_hash <> cohort.manifest_hash OR NEW.image_digest <> cohort.image_digest THEN
        RAISE EXCEPTION 'a run must use the cohort manifest and evaluator digest';
    END IF;
    IF NEW.reason = 'initial' THEN
        IF EXISTS (SELECT 1 FROM results.prospective_runs WHERE cohort_id = NEW.cohort_id AND reason = 'initial') THEN
            RAISE EXCEPTION 'cohort % already has an initial run', NEW.cohort_id;
        END IF;
    ELSE
        SELECT * INTO original FROM results.prospective_runs WHERE run_id = NEW.rerun_of;
        IF original.cohort_id IS DISTINCT FROM NEW.cohort_id OR original.data_digest <> NEW.data_digest THEN
            RAISE EXCEPTION 're-evaluation must use the same cohort, window and data';
        END IF;
        IF NEW.reason IN ('rerun-data-quality', 'rerun-environment')
           AND (SELECT count(*) FROM results.prospective_runs
                WHERE cohort_id = NEW.cohort_id AND reason IN ('rerun-data-quality', 'rerun-environment')) >= 2 THEN
            RAISE EXCEPTION 're-evaluation limit reached (2)';
        END IF;
    END IF;
    PERFORM results.audit('prospective-run', NEW.cohort_id,
        jsonb_build_object('reason', NEW.reason, 'rerun_of', NEW.rerun_of, 'data_digest', NEW.data_digest,
                           'result_hash', NEW.result_hash, 'evidence', NEW.evidence));
    RETURN NEW;
END
$$;

CREATE TRIGGER run_rules BEFORE INSERT ON results.prospective_runs
    FOR EACH ROW EXECUTE FUNCTION results.before_run();

-- Results: Validated needs a verified environment, a complete protocol and a powered cohort (T3, V3).
CREATE FUNCTION results.before_result() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER SET search_path = results, pg_temp AS $$
DECLARE
    cohort results.cohort_manifests;
BEGIN
    SELECT * INTO cohort FROM results.cohort_manifests WHERE cohort_id = NEW.cohort_id;
    IF (SELECT cohort_id FROM results.prospective_runs WHERE run_id = NEW.run_id) <> NEW.cohort_id THEN
        RAISE EXCEPTION 'result and run belong to different cohorts';
    END IF;
    IF NEW.status = 'Validated' THEN
        IF NOT results.environment_verified() THEN
            RAISE EXCEPTION 'Validated requires HoldoutEnvironmentVerified';
        END IF;
        IF NOT cohort.protocol_complete OR cohort.underpowered THEN
            RAISE EXCEPTION 'Validated is impossible for an incomplete-protocol or underpowered cohort';
        END IF;
    END IF;
    PERFORM results.audit('prospective-result', NEW.cohort_id,
        jsonb_build_object('run_id', NEW.run_id, 'member', NEW.member_index, 'status', NEW.status, 'summary', NEW.summary));
    RETURN NEW;
END
$$;

CREATE TRIGGER result_rules BEFORE INSERT ON results.prospective_results
    FOR EACH ROW EXECUTE FUNCTION results.before_result();

-- Validation status: derived only from prospective results (latest run wins); no other path can set it (V1).
CREATE VIEW results.validation_status AS
SELECT m.cohort_id,
       m.member_index,
       m.strategy_name,
       m.definition_hash,
       coalesce(latest.status, 'ProspectiveSealed') AS status,
       latest.detail
FROM results.cohort_members m
LEFT JOIN LATERAL (
    SELECT r.status, r.detail FROM results.prospective_results r
    WHERE r.cohort_id = m.cohort_id AND r.member_index = m.member_index
    ORDER BY r.run_id DESC, r.id DESC LIMIT 1
) latest ON true;

-- Kohort 0 seed exception (architecture §6.3, V13): owner only; registered_at comes from the local commit time.
CREATE FUNCTION results.seed_cohort(registered timestamptz, months integer, manifest jsonb, manifest_hash text,
                                    protocol_version text, protocol_complete boolean, underpowered boolean,
                                    engine_sha text, image_digest text) RETURNS bigint
    LANGUAGE plpgsql AS $$
DECLARE id bigint;
BEGIN
    PERFORM set_config('results.allow_seed', 'on', true);
    INSERT INTO results.cohort_manifests (kind, registered_at, l_c_months, manifest, manifest_hash, protocol_version,
                                          protocol_complete, underpowered, engine_sha, image_digest)
    VALUES ('seed', registered, months, manifest, manifest_hash, protocol_version, protocol_complete, underpowered, engine_sha, image_digest)
    RETURNING cohort_id INTO id;
    PERFORM results.audit('seed-cohort', id, jsonb_build_object('registered_at', registered, 'manifest_hash', manifest_hash));
    RETURN id;
END
$$;

CREATE FUNCTION results.end_seed() RETURNS void
    LANGUAGE sql AS $$ SELECT set_config('results.allow_seed', 'off', true) $$;

-- ---------------------------------------------------------------------------------------------------------
-- Roles (NOLOGIN in M3; logins and pg_hba arrive in M5).
-- ---------------------------------------------------------------------------------------------------------
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'eval_registry') THEN CREATE ROLE eval_registry NOLOGIN; END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'eval_diagnostic') THEN CREATE ROLE eval_diagnostic NOLOGIN; END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'eval_prospective') THEN CREATE ROLE eval_prospective NOLOGIN; END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'research_app') THEN CREATE ROLE research_app NOLOGIN; END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'trading_app') THEN CREATE ROLE trading_app NOLOGIN; END IF;
    EXECUTE format('REVOKE CONNECT, TEMPORARY ON DATABASE %I FROM PUBLIC', current_database());
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO eval_registry, eval_diagnostic, eval_prospective', current_database());
END
$$;

REVOKE ALL ON FUNCTION results.seed_cohort(timestamptz, integer, jsonb, text, text, boolean, boolean, text, text) FROM PUBLIC;
REVOKE ALL ON FUNCTION results.end_seed() FROM PUBLIC;
REVOKE ALL ON FUNCTION results.audit(text, bigint, jsonb) FROM PUBLIC;

-- eval_registry: registers manifests and members; cannot read window data or results.
GRANT USAGE ON SCHEMA results TO eval_registry;
GRANT SELECT, INSERT ON results.cohort_manifests, results.cohort_members TO eval_registry;
GRANT SELECT ON results.evaluator_releases TO eval_registry;
GRANT USAGE ON SEQUENCE results.cohort_manifests_cohort_id_seq TO eval_registry;

-- eval_diagnostic: diagnostic.* and insert-only on diagnostic results; never prospective.
GRANT USAGE ON SCHEMA diagnostic, results TO eval_diagnostic;
GRANT SELECT, INSERT, DELETE ON diagnostic.candles TO eval_diagnostic;
GRANT INSERT ON results.diagnostic_results TO eval_diagnostic;
GRANT USAGE ON SEQUENCE results.diagnostic_results_id_seq TO eval_diagnostic;

-- eval_prospective: prospective.* and the prospective result tables; never diagnostic.
GRANT USAGE ON SCHEMA prospective, results TO eval_prospective;
GRANT SELECT, INSERT ON prospective.candles TO eval_prospective;
GRANT SELECT ON results.cohort_manifests, results.cohort_members, results.validation_status TO eval_prospective;
GRANT SELECT, INSERT ON results.prospective_runs, results.prospective_results TO eval_prospective;
GRANT USAGE ON SEQUENCE results.prospective_runs_run_id_seq, results.prospective_results_id_seq TO eval_prospective;
GRANT EXECUTE ON FUNCTION results.audit(text, bigint, jsonb) TO eval_prospective;
