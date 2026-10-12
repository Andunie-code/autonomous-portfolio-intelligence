-- M0: module schemas only. Tables arrive with the milestones that own them.
-- Holdout data never lives in this database (ADR-0006, ADR-0009); there is deliberately no holdout schema.
CREATE SCHEMA IF NOT EXISTS market;    -- candles, dataset snapshots (M1)
CREATE SCHEMA IF NOT EXISTS research;  -- hypotheses, experiments, registry (M3+)
CREATE SCHEMA IF NOT EXISTS trading;   -- orders, ledger, positions (M5+)
CREATE SCHEMA IF NOT EXISTS audit;     -- append-only audit events (M5)
