-- M5b (ADR-0019, ADR-0020): one paper run per cohort and the read-only dashboard of the Ops API.
-- No new table: the append-only trigger set of 0007 is unchanged.

-- A position close targets one run: sleeve indices are per run (ADR-0019 §3). Rows written before M5b have no run.
ALTER TABLE trading.ops_actions ADD COLUMN run_id bigint REFERENCES trading.paper_runs (id);

-- Provenance of the frozen strategies (ADR-0020 §4): their research-window experiments and dataset snapshots.
-- Runs with the owner's privileges; only experiments of frozen definitions are visible, metrics are not.
CREATE VIEW research.frozen_provenance AS
    SELECT f.cohort_id, f.member_index, f.definition_hash, e.id AS experiment_id, e.created_at, e.variant,
           e.protocol_version, e.protocol_commit, e.dataset_snapshot_hash, e.engine_git_sha, e.result_hash
    FROM research.frozen_strategies f
    JOIN research.experiments e ON e.strategy_hash = f.definition_hash;

-- ops_api (the user's dashboard): read-only access to the AI ledger, traces and kill switch, the audit trail and
-- the strategy registry. research_app's grants and the holdout database are unchanged.
GRANT USAGE ON SCHEMA ai TO ops_api;
GRANT SELECT ON ai.cost_ledger, ai.decision_traces, ai.kill_switch_events TO ops_api;
GRANT SELECT ON audit.events, research.strategy_versions, research.frozen_provenance TO ops_api;
