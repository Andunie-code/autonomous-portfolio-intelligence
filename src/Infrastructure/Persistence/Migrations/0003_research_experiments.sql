-- M2: experiment registry (docs/m2-protocol.md §12, T18). Append-only: rows are never updated or deleted;
-- the trigger enforces this until per-module database roles arrive (architecture §6.2).

CREATE TABLE research.experiments (
    id                     bigserial   PRIMARY KEY,
    created_at             timestamptz NOT NULL DEFAULT now(),
    strategy_name          text        NOT NULL,
    strategy_version       text        NOT NULL,
    strategy_hash          text        NOT NULL CHECK (strategy_hash ~ '^[0-9a-f]{64}$'),
    variant                text        NOT NULL,
    protocol_version       text        NOT NULL,
    protocol_commit        text        NOT NULL,
    dataset_snapshot_hash  text        NOT NULL CHECK (dataset_snapshot_hash ~ '^[0-9a-f]{64}$'),
    engine_git_sha         text        NOT NULL,
    result_hash            text        NOT NULL CHECK (result_hash ~ '^[0-9a-f]{64}$'),
    metrics                jsonb       NOT NULL
);

CREATE FUNCTION research.reject_experiment_change() RETURNS trigger
    LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'research.experiments is append-only';
END
$$;

CREATE TRIGGER experiments_append_only
    BEFORE UPDATE OR DELETE ON research.experiments
    FOR EACH ROW EXECUTE FUNCTION research.reject_experiment_change();
