-- M3: trial families and the trial counter (docs/m3-protocol.md §8, ADR-0006, ADR-0015). Append-only.
-- A family is identified by the structure hash of its first member; further structure hashes join a family only
-- through the similarity check against the family representative (or a user assignment). Each distinct definition
-- hash recorded on the research window is one trial; the counter is global for the cutoff period.

CREATE TABLE research.trial_families (
    id                         bigserial   PRIMARY KEY,
    created_at                 timestamptz NOT NULL DEFAULT now(),
    representative_structure   text        NOT NULL UNIQUE CHECK (representative_structure ~ '^[0-9a-f]{64}$'),
    representative_definition  text        NOT NULL CHECK (representative_definition ~ '^[0-9a-f]{64}$'),
    research_cutoff            timestamptz NOT NULL DEFAULT market.research_cutoff()
);

-- Every structure hash belongs to exactly one family.
CREATE TABLE research.family_structures (
    structure_hash  text        PRIMARY KEY CHECK (structure_hash ~ '^[0-9a-f]{64}$'),
    family_id       bigint      NOT NULL REFERENCES research.trial_families (id),
    assigned_by     text        NOT NULL CHECK (assigned_by IN ('first-member', 'similarity', 'user')),
    evidence        jsonb,
    created_at      timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE research.trials (
    id                bigserial   PRIMARY KEY,
    created_at        timestamptz NOT NULL DEFAULT now(),
    definition_hash   text        NOT NULL CHECK (definition_hash ~ '^[0-9a-f]{64}$'),
    structure_hash    text        NOT NULL REFERENCES research.family_structures (structure_hash),
    family_id         bigint      NOT NULL REFERENCES research.trial_families (id),
    origin            text        NOT NULL CHECK (origin IN ('llm', 'grid', 'human', 'dev-assisted')),
    strategy_name     text        NOT NULL,
    definition        jsonb       NOT NULL,
    protocol_version  text        NOT NULL,
    research_cutoff   timestamptz NOT NULL DEFAULT market.research_cutoff(),
    period_sharpe     double precision,
    UNIQUE (definition_hash, research_cutoff)
);

-- Representative artefacts (ADR-0015): per pair, the 1h entry bitset and the 1d int8 exposure vector on the
-- research window of one dataset snapshot. Regenerated (new rows) when the snapshot changes.
CREATE TABLE research.family_artifacts (
    family_id        bigint  NOT NULL REFERENCES research.trial_families (id),
    snapshot_hash    text    NOT NULL CHECK (snapshot_hash ~ '^[0-9a-f]{64}$'),
    symbol           text    NOT NULL,
    entry_bitset     bytea   NOT NULL,
    exposure         bytea   NOT NULL,
    PRIMARY KEY (family_id, snapshot_hash, symbol)
);

CREATE FUNCTION research.reject_trial_change() RETURNS trigger
    LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% is append-only', TG_TABLE_NAME;
END
$$;

CREATE TRIGGER trial_families_append_only BEFORE UPDATE OR DELETE ON research.trial_families
    FOR EACH ROW EXECUTE FUNCTION research.reject_trial_change();
CREATE TRIGGER family_structures_append_only BEFORE UPDATE OR DELETE ON research.family_structures
    FOR EACH ROW EXECUTE FUNCTION research.reject_trial_change();
CREATE TRIGGER trials_append_only BEFORE UPDATE OR DELETE ON research.trials
    FOR EACH ROW EXECUTE FUNCTION research.reject_trial_change();
CREATE TRIGGER family_artifacts_append_only BEFORE UPDATE OR DELETE ON research.family_artifacts
    FOR EACH ROW EXECUTE FUNCTION research.reject_trial_change();
