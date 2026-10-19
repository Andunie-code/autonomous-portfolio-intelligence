-- M5 (docs/m5-protocol.md §2–§4, ADR-0018): live candles, the paper-trading schema, frozen strategies and the
-- operational lifecycle, research views and the remaining ops roles. Every table is append-only.
-- Logins and passwords are set by scripts/m5-bootstrap.ps1, never here.

-- ---------------------------------------------------------------------------------------------------------
-- Live candles (T4): written only by trading_app; strictly after the research cutoff; never read by research.
-- ---------------------------------------------------------------------------------------------------------
CREATE TABLE market.live_candles (
    symbol           text        NOT NULL CHECK (symbol ~ '^[A-Z0-9]{2,30}$'),
    candle_interval  text        NOT NULL CHECK (candle_interval IN ('1h', '4h')),
    open_time        timestamptz NOT NULL,
    open             numeric     NOT NULL,
    high             numeric     NOT NULL,
    low              numeric     NOT NULL,
    close            numeric     NOT NULL,
    base_volume      numeric     NOT NULL CHECK (base_volume >= 0),
    quote_volume     numeric     NOT NULL CHECK (quote_volume >= 0),
    trade_count      bigint      NOT NULL CHECK (trade_count >= 0),
    fetched_at       timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (symbol, candle_interval, open_time),
    CONSTRAINT live_candles_ohlc CHECK (low > 0 AND low <= LEAST(open, close) AND GREATEST(open, close) <= high),
    CONSTRAINT live_candles_after_research_cutoff CHECK (open_time >= market.research_cutoff())
);

-- A later different value for a stored open time is kept here; the first value stays authoritative (§2.3).
CREATE TABLE market.live_candle_anomalies (
    id               bigserial   PRIMARY KEY,
    recorded_at      timestamptz NOT NULL DEFAULT now(),
    symbol           text        NOT NULL,
    candle_interval  text        NOT NULL,
    open_time        timestamptz NOT NULL,
    kind             text        NOT NULL,
    detail           jsonb       NOT NULL
);

-- ---------------------------------------------------------------------------------------------------------
-- Frozen strategies and the operational lifecycle (ADR-0012, m5-protocol §4.5). No validation status here.
-- ---------------------------------------------------------------------------------------------------------
CREATE TABLE research.frozen_strategies (
    id               bigserial   PRIMARY KEY,
    recorded_at      timestamptz NOT NULL DEFAULT now(),
    cohort_id        bigint      NOT NULL,
    manifest_hash    text        NOT NULL CHECK (manifest_hash ~ '^[0-9a-f]{64}$'),
    member_index     integer     NOT NULL CHECK (member_index BETWEEN 1 AND 3),
    name             text        NOT NULL,
    definition_hash  text        NOT NULL REFERENCES research.strategy_versions (definition_hash),
    -- Kept as text so the manifest hash (canonical JSON, numbers as written) can be recomputed exactly.
    manifest         text        NOT NULL,
    UNIQUE (cohort_id, member_index)
);

CREATE TABLE research.lifecycle_events (
    id               bigserial   PRIMARY KEY,
    recorded_at      timestamptz NOT NULL DEFAULT now(),
    frozen_id        bigint      NOT NULL REFERENCES research.frozen_strategies (id),
    from_state       text        NOT NULL,
    to_state         text        NOT NULL,
    actor            text        NOT NULL,
    evidence         jsonb       NOT NULL,
    CHECK (to_state IN ('Frozen', 'PaperTrading', 'TestnetActive', 'Disabled', 'Retired', 'Rejected')),
    CHECK (from_state IN ('Candidate', 'Frozen', 'PaperTrading', 'TestnetActive', 'Disabled'))
);

-- Allowed transitions; TestnetActive needs a user session and arrives with M6 (V10).
CREATE FUNCTION research.lifecycle_rules() RETURNS trigger
    LANGUAGE plpgsql AS $$
DECLARE
    current_state text;
BEGIN
    SELECT to_state INTO current_state FROM research.lifecycle_events WHERE frozen_id = NEW.frozen_id ORDER BY id DESC LIMIT 1;
    IF coalesce(current_state, 'Candidate') <> NEW.from_state THEN
        RAISE EXCEPTION 'lifecycle: % is not the current state (%)', NEW.from_state, coalesce(current_state, 'Candidate');
    END IF;
    IF NOT ((NEW.from_state, NEW.to_state) IN (('Candidate', 'Frozen'), ('Frozen', 'PaperTrading'))
            OR NEW.to_state IN ('Disabled', 'Retired', 'Rejected')) THEN
        RAISE EXCEPTION 'lifecycle: % -> % is not allowed', NEW.from_state, NEW.to_state;
    END IF;
    IF NEW.to_state = 'PaperTrading' AND NOT (NEW.evidence ? 'parityReportHash') THEN
        RAISE EXCEPTION 'lifecycle: Frozen -> PaperTrading needs parity replay evidence';
    END IF;
    RETURN NEW;
END
$$;

CREATE TRIGGER lifecycle_rules BEFORE INSERT ON research.lifecycle_events
    FOR EACH ROW EXECUTE FUNCTION research.lifecycle_rules();

-- ---------------------------------------------------------------------------------------------------------
-- Paper trading (m5-protocol §2, §3).
-- ---------------------------------------------------------------------------------------------------------
CREATE TABLE trading.risk_config_versions (
    id            bigserial   PRIMARY KEY,
    created_at    timestamptz NOT NULL DEFAULT now(),
    actor         text        NOT NULL,
    reason        text        NOT NULL,
    config        jsonb       NOT NULL,
    config_hash   text        NOT NULL CHECK (config_hash ~ '^[0-9a-f]{64}$'),
    CHECK ((config ->> 'experimentalPairsMaxExposure')::numeric BETWEEN 0 AND 1)
);

CREATE TABLE trading.paper_runs (
    id              bigserial   PRIMARY KEY,
    created_at      timestamptz NOT NULL DEFAULT now(),
    trading_start   timestamptz NOT NULL,
    warmup_start    timestamptz NOT NULL,
    config          jsonb       NOT NULL,
    config_hash     text        NOT NULL CHECK (config_hash ~ '^[0-9a-f]{64}$'),
    CHECK (warmup_start < trading_start)
);

-- The step log is the source of truth for restarts: replaying candles with these contexts must rebuild state_hash.
CREATE TABLE trading.steps (
    run_id          bigint      NOT NULL REFERENCES trading.paper_runs (id),
    bar_time        timestamptz NOT NULL,
    processed_at    timestamptz NOT NULL DEFAULT now(),
    context         jsonb       NOT NULL,
    state_hash      text        NOT NULL CHECK (state_hash ~ '^[0-9a-f]{64}$'),
    l1_hash         text        NOT NULL,
    l2_hash         text        NOT NULL,
    PRIMARY KEY (run_id, bar_time)
);

CREATE TABLE trading.engine_events (
    run_id     bigint      NOT NULL,
    bar_time   timestamptz NOT NULL,
    seq        integer     NOT NULL,
    event_time timestamptz NOT NULL,
    sleeve     integer     NOT NULL,
    symbol     text        NOT NULL,
    kind       text        NOT NULL,
    value      numeric     NOT NULL,
    detail     text        NOT NULL,
    before     numeric     NOT NULL,
    after      numeric     NOT NULL,
    PRIMARY KEY (run_id, bar_time, seq),
    FOREIGN KEY (run_id, bar_time) REFERENCES trading.steps (run_id, bar_time) DEFERRABLE INITIALLY DEFERRED
);

-- Fills, tagged with strategy and cohort (ADR-0015). The unique key makes a duplicate fill impossible.
CREATE TABLE trading.fills (
    id           bigserial   PRIMARY KEY,
    run_id       bigint      NOT NULL REFERENCES trading.paper_runs (id),
    bar_time     timestamptz NOT NULL,
    sleeve       integer     NOT NULL,
    strategy     text        NOT NULL,
    cohort_id    bigint      NOT NULL,
    symbol       text        NOT NULL,
    side         text        NOT NULL CHECK (side IN ('buy', 'sell')),
    fill_time    timestamptz NOT NULL,
    price        numeric     NOT NULL CHECK (price > 0),
    quantity     numeric     NOT NULL CHECK (quantity > 0),
    fee          numeric     NOT NULL CHECK (fee >= 0),
    reason       text        NOT NULL,
    UNIQUE (run_id, sleeve, symbol, fill_time, side)
);

CREATE TABLE trading.ledger_entries (
    id           bigserial   PRIMARY KEY,
    run_id       bigint      NOT NULL REFERENCES trading.paper_runs (id),
    fill_id      bigint      NOT NULL REFERENCES trading.fills (id),
    sleeve       integer     NOT NULL,
    kind         text        NOT NULL CHECK (kind IN ('trade', 'fee')),
    cash_delta   numeric     NOT NULL,
    UNIQUE (fill_id, kind)
);

CREATE TABLE trading.balance_snapshots (
    run_id       bigint      NOT NULL,
    bar_time     timestamptz NOT NULL,
    sleeve       integer     NOT NULL,
    cash         numeric     NOT NULL CHECK (cash >= 0),
    equity       numeric     NOT NULL,
    positions    jsonb       NOT NULL,
    PRIMARY KEY (run_id, bar_time, sleeve),
    FOREIGN KEY (run_id, bar_time) REFERENCES trading.steps (run_id, bar_time) DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE trading.reconciliation_runs (
    id           bigserial   PRIMARY KEY,
    checked_at   timestamptz NOT NULL DEFAULT now(),
    run_id       bigint      NOT NULL REFERENCES trading.paper_runs (id),
    bar_time     timestamptz,
    ok           boolean     NOT NULL,
    detail       jsonb       NOT NULL
);

CREATE TABLE trading.cycle_metrics (
    id               bigserial   PRIMARY KEY,
    started_at       timestamptz NOT NULL,
    finished_at      timestamptz NOT NULL,
    scheduled_for    timestamptz NOT NULL,
    bars_processed   integer     NOT NULL CHECK (bars_processed >= 0),
    catch_up         boolean     NOT NULL,
    stale            boolean     NOT NULL,
    outcome          text        NOT NULL,
    detail           jsonb       NOT NULL,
    CHECK (finished_at >= started_at)
);

CREATE TABLE trading.kill_switch_events (
    id           bigserial   PRIMARY KEY,
    recorded_at  timestamptz NOT NULL DEFAULT now(),
    engaged      boolean     NOT NULL,
    actor        text        NOT NULL,
    reason       text        NOT NULL
);

-- D11: automatic triggers may only engage the switch; releasing it is a user action through the Ops API role.
CREATE FUNCTION trading.kill_switch_rules() RETURNS trigger
    LANGUAGE plpgsql AS $$
BEGIN
    IF NOT NEW.engaged AND current_user <> 'ops_api' AND NOT pg_has_role(current_user, 'ops_api', 'MEMBER') THEN
        RAISE EXCEPTION 'the trading kill switch is released only by the user (Ops API)';
    END IF;
    RETURN NEW;
END
$$;

CREATE TRIGGER kill_switch_rules BEFORE INSERT ON trading.kill_switch_events
    FOR EACH ROW EXECUTE FUNCTION trading.kill_switch_rules();

-- Operational safety actions (D23): requested through the Ops API, applied by the next step, always allowed.
CREATE TABLE trading.ops_actions (
    id           bigserial   PRIMARY KEY,
    requested_at timestamptz NOT NULL DEFAULT now(),
    actor        text        NOT NULL,
    action       text        NOT NULL CHECK (action IN ('disable-strategy', 'close-position', 'close-all')),
    sleeve       integer,
    symbol       text,
    reason       text        NOT NULL
);

CREATE TABLE trading.correlation_snapshots (
    run_id           bigint      NOT NULL REFERENCES trading.paper_runs (id),
    bar_time         timestamptz NOT NULL,
    threshold        numeric     NOT NULL,
    clusters         jsonb       NOT NULL,
    effective_count  numeric     NOT NULL,
    max_cluster_share numeric    NOT NULL,
    PRIMARY KEY (run_id, bar_time)
);

DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['market.live_candles', 'market.live_candle_anomalies', 'research.frozen_strategies',
        'research.lifecycle_events', 'trading.risk_config_versions', 'trading.paper_runs', 'trading.steps',
        'trading.engine_events', 'trading.fills', 'trading.ledger_entries', 'trading.balance_snapshots',
        'trading.reconciliation_runs', 'trading.cycle_metrics', 'trading.kill_switch_events', 'trading.ops_actions',
        'trading.correlation_snapshots']
    LOOP
        EXECUTE format('CREATE TRIGGER append_only BEFORE UPDATE OR DELETE ON %s FOR EACH ROW EXECUTE FUNCTION audit.reject_change()', t);
    END LOOP;
END
$$;

-- ---------------------------------------------------------------------------------------------------------
-- Research views (architecture §6.2): research reads candles before C and revealed cohorts only.
-- Views run with the owner's privileges, so research_app needs no grant on the underlying trading tables.
-- ---------------------------------------------------------------------------------------------------------
CREATE VIEW research.candles AS
    SELECT symbol, candle_interval, open_time, open, high, low, close, base_volume, quote_volume, trade_count
    FROM market.candles;

CREATE VIEW research.revealed_cohorts AS
    SELECT DISTINCT ON (cohort_id) cohort_id, recorded_at AS revealed_at, summary
    FROM research.cohort_status
    WHERE status = 'revealed'
    ORDER BY cohort_id, id;

CREATE VIEW research.revealed_paper_summary AS
    SELECT f.cohort_id, f.strategy, count(*) AS fills,
           sum(CASE WHEN f.side = 'sell' THEN f.price * f.quantity ELSE -f.price * f.quantity END - f.fee) AS net_cash_flow
    FROM trading.fills f
    WHERE f.cohort_id IN (SELECT cohort_id FROM research.revealed_cohorts)
    GROUP BY f.cohort_id, f.strategy;

-- ---------------------------------------------------------------------------------------------------------
-- Roles (m5-protocol §4.2). NOLOGIN here; bootstrap turns them into logins.
-- ---------------------------------------------------------------------------------------------------------
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'market_import') THEN CREATE ROLE market_import NOLOGIN; END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'ops_api') THEN CREATE ROLE ops_api NOLOGIN; END IF;
    EXECUTE format('REVOKE CONNECT, TEMPORARY ON DATABASE %I FROM PUBLIC', current_database());
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO research_app, trading_app, ops_api, market_import, status_relay', current_database());
END
$$;

-- research_app: candles before C, its registries, frozen strategies and research-side lifecycle; never live data.
REVOKE SELECT ON ALL TABLES IN SCHEMA market FROM research_app;
GRANT SELECT ON market.candles, market.source_files, market.dataset_snapshots TO research_app;
GRANT SELECT ON research.candles, research.revealed_cohorts, research.revealed_paper_summary TO research_app;
GRANT SELECT, INSERT ON research.frozen_strategies, research.lifecycle_events TO research_app;
GRANT USAGE ON SEQUENCE research.frozen_strategies_id_seq, research.lifecycle_events_id_seq TO research_app;

-- market_import: the archive importer (M1 tables only).
GRANT USAGE ON SCHEMA market TO market_import;
GRANT SELECT, INSERT ON market.candles, market.source_files, market.dataset_snapshots TO market_import;
GRANT USAGE ON SEQUENCE market.source_files_id_seq, market.dataset_snapshots_id_seq TO market_import;

-- trading_app: live candles, the paper books, read-only strategy registry, lifecycle and risk configuration.
GRANT USAGE ON SCHEMA market, trading TO trading_app;
GRANT SELECT, INSERT ON market.live_candles, market.live_candle_anomalies TO trading_app;
GRANT SELECT ON research.frozen_strategies, research.lifecycle_events TO trading_app;
GRANT SELECT, INSERT ON trading.paper_runs, trading.steps, trading.engine_events, trading.fills, trading.ledger_entries,
    trading.balance_snapshots, trading.reconciliation_runs, trading.cycle_metrics, trading.kill_switch_events,
    trading.correlation_snapshots TO trading_app;
GRANT SELECT ON trading.risk_config_versions, trading.ops_actions TO trading_app;
GRANT USAGE ON ALL SEQUENCES IN SCHEMA trading TO trading_app;
GRANT USAGE ON SEQUENCE market.live_candle_anomalies_id_seq TO trading_app;
REVOKE USAGE ON SEQUENCE trading.risk_config_versions_id_seq, trading.ops_actions_id_seq FROM trading_app;

-- ops_api: the user's operational interface (H23, H24): reads everything operational, writes only RiskConfig
-- versions, operational actions, kill switch events, Disabled transitions and audit.
GRANT USAGE ON SCHEMA market, trading, research, audit TO ops_api;
GRANT SELECT ON market.live_candles TO ops_api;
GRANT SELECT ON ALL TABLES IN SCHEMA trading TO ops_api;
GRANT SELECT ON research.frozen_strategies, research.lifecycle_events, research.cohort_status TO ops_api;
GRANT INSERT ON trading.risk_config_versions, trading.ops_actions, trading.kill_switch_events, research.lifecycle_events, audit.events TO ops_api;
GRANT USAGE ON SEQUENCE trading.risk_config_versions_id_seq, trading.ops_actions_id_seq, trading.kill_switch_events_id_seq,
    research.lifecycle_events_id_seq, audit.events_id_seq TO ops_api;
