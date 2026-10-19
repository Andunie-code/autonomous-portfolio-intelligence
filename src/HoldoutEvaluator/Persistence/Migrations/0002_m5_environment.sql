-- M5 (docs/m5-protocol.md §6, §10 E5; ADR-0013 rev. note): the real local holdout environment.
--  * HoldoutEnvironmentVerified is bound to an evaluator image digest: a flag for one digest says nothing about
--    another, so every evaluator change needs a new H-ENV run (ADR-0013 §4). The report hash is recorded.
--  * Release equivalence: a cohort registered with one release (Kohort 0: local-build) may be evaluated by another
--    release only with an audited equivalence record (regression evidence; m5-protocol §6.3, user decision A-2).
--  * Validated now checks the environment flag of the run's own image digest.
-- 0001 is unchanged; functions are replaced here.

ALTER TABLE results.environment_flags ADD COLUMN image_digest text, ADD COLUMN report_hash text;
-- Rows written from M5 on must name the digest and the H-ENV report (M3 synthetic rows are left as they are).
ALTER TABLE results.environment_flags ADD CONSTRAINT environment_flags_bound
    CHECK (image_digest IS NOT NULL AND report_hash ~ '^[0-9a-f]{64}$') NOT VALID;

CREATE FUNCTION results.environment_verified(digest text) RETURNS boolean
    LANGUAGE sql STABLE SECURITY DEFINER SET search_path = results, pg_temp AS $$
    SELECT coalesce((SELECT verified FROM results.environment_flags WHERE image_digest = digest ORDER BY id DESC LIMIT 1), false)
$$;

-- The digest-less flag is superseded: it no longer verifies anything.
CREATE OR REPLACE FUNCTION results.environment_verified() RETURNS boolean
    LANGUAGE sql STABLE SECURITY DEFINER SET search_path = results, pg_temp AS $$ SELECT false $$;

CREATE TABLE results.release_equivalences (
    id            bigserial   PRIMARY KEY,
    recorded_at   timestamptz NOT NULL DEFAULT now(),
    from_digest   text        NOT NULL REFERENCES results.evaluator_releases (image_digest),
    to_digest     text        NOT NULL REFERENCES results.evaluator_releases (image_digest),
    actor         text        NOT NULL,
    evidence      jsonb       NOT NULL,
    UNIQUE (from_digest, to_digest),
    CHECK (from_digest <> to_digest)
);

CREATE TRIGGER release_equivalences_append_only BEFORE UPDATE OR DELETE ON results.release_equivalences
    FOR EACH ROW EXECUTE FUNCTION results.reject_change();

CREATE FUNCTION results.after_equivalence() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER SET search_path = results, pg_temp AS $$
BEGIN
    PERFORM results.audit('release-equivalence', NULL,
        jsonb_build_object('from', NEW.from_digest, 'to', NEW.to_digest, 'actor', NEW.actor, 'evidence', NEW.evidence));
    RETURN NEW;
END
$$;

CREATE TRIGGER equivalence_audit AFTER INSERT ON results.release_equivalences
    FOR EACH ROW EXECUTE FUNCTION results.after_equivalence();

CREATE FUNCTION results.after_environment_flag() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER SET search_path = results, pg_temp AS $$
BEGIN
    PERFORM results.audit('environment-flag', NULL,
        jsonb_build_object('verified', NEW.verified, 'actor', NEW.actor, 'image_digest', NEW.image_digest, 'report_hash', NEW.report_hash));
    RETURN NEW;
END
$$;

CREATE TRIGGER environment_flag_audit AFTER INSERT ON results.environment_flags
    FOR EACH ROW EXECUTE FUNCTION results.after_environment_flag();

-- Runs: as in 0001, except that the evaluating release may differ from the cohort's release when an
-- equivalence record links them.
CREATE OR REPLACE FUNCTION results.before_run() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER SET search_path = results, pg_temp AS $$
DECLARE
    cohort results.cohort_manifests;
    original results.prospective_runs;
BEGIN
    SELECT * INTO cohort FROM results.cohort_manifests WHERE cohort_id = NEW.cohort_id;
    IF NEW.manifest_hash <> cohort.manifest_hash
       OR (NEW.image_digest <> cohort.image_digest
           AND NOT EXISTS (SELECT 1 FROM results.release_equivalences e
                           WHERE e.from_digest = cohort.image_digest AND e.to_digest = NEW.image_digest)) THEN
        RAISE EXCEPTION 'a run must use the cohort manifest and evaluator digest (or an equivalent release)';
    END IF;
    IF NEW.reason = 'initial' THEN
        IF EXISTS (SELECT 1 FROM results.prospective_runs WHERE cohort_id = NEW.cohort_id AND reason = 'initial') THEN
            RAISE EXCEPTION 'cohort % already has an initial run', NEW.cohort_id;
        END IF;
    ELSE
        SELECT * INTO original FROM results.prospective_runs WHERE run_id = NEW.rerun_of;
        IF original.cohort_id IS DISTINCT FROM NEW.cohort_id OR original.data_digest <> NEW.data_digest
           OR original.image_digest <> NEW.image_digest THEN
            RAISE EXCEPTION 're-evaluation must use the same cohort, window and data, and the same digest';
        END IF;
        IF NEW.reason IN ('rerun-data-quality', 'rerun-environment')
           AND (SELECT count(*) FROM results.prospective_runs
                WHERE cohort_id = NEW.cohort_id AND reason IN ('rerun-data-quality', 'rerun-environment')) >= 2 THEN
            RAISE EXCEPTION 're-evaluation limit reached (2)';
        END IF;
    END IF;
    PERFORM results.audit('prospective-run', NEW.cohort_id,
        jsonb_build_object('reason', NEW.reason, 'rerun_of', NEW.rerun_of, 'data_digest', NEW.data_digest,
                           'result_hash', NEW.result_hash, 'evidence', NEW.evidence, 'image_digest', NEW.image_digest));
    RETURN NEW;
END
$$;

-- Results: Validated needs the environment flag of the run's digest, a complete protocol and a powered cohort.
CREATE OR REPLACE FUNCTION results.before_result() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER SET search_path = results, pg_temp AS $$
DECLARE
    cohort results.cohort_manifests;
    run results.prospective_runs;
BEGIN
    SELECT * INTO cohort FROM results.cohort_manifests WHERE cohort_id = NEW.cohort_id;
    SELECT * INTO run FROM results.prospective_runs WHERE run_id = NEW.run_id;
    IF run.cohort_id <> NEW.cohort_id THEN
        RAISE EXCEPTION 'result and run belong to different cohorts';
    END IF;
    IF NEW.status = 'Validated' THEN
        IF NOT results.environment_verified(run.image_digest) THEN
            RAISE EXCEPTION 'Validated requires HoldoutEnvironmentVerified for the evaluating release';
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

GRANT EXECUTE ON FUNCTION results.environment_verified(text) TO eval_prospective;
GRANT SELECT ON results.release_equivalences TO eval_prospective;
