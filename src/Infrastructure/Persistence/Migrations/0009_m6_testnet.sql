-- M6 (docs/m6-protocol.md, ADR-0021): Binance Spot Testnet execution. The outbox is append-only: an order is one
-- row, its states are events, its trades are fills; the latest event is the order's state (§2.1, §3).

-- A run is paper or Testnet (§4.1). Existing runs are paper.
ALTER TABLE trading.paper_runs ADD COLUMN venue text NOT NULL DEFAULT 'paper' CHECK (venue IN ('paper', 'testnet'));

-- A Testnet run ends after an exchange reset (§4.4); its history stays and a new run with a new baseline starts.
CREATE TABLE trading.run_closures (
    run_id      bigint      PRIMARY KEY REFERENCES trading.paper_runs (id),
    closed_at   timestamptz NOT NULL DEFAULT now(),
    reason      text        NOT NULL
);

-- One row per order, written in the same transaction as its Submitting (or LocalRejected) event, before sending.
CREATE TABLE trading.exchange_orders (
    id               bigserial   PRIMARY KEY,
    created_at       timestamptz NOT NULL DEFAULT now(),
    run_id           bigint      NOT NULL REFERENCES trading.paper_runs (id),
    sleeve           integer     NOT NULL,
    symbol           text        NOT NULL,
    purpose          text        NOT NULL CHECK (purpose IN ('Entry', 'Stop', 'Exit', 'Close')),
    side             text        NOT NULL CHECK (side IN ('Buy', 'Sell')),
    order_type       text        NOT NULL CHECK (order_type IN ('Limit', 'Market', 'StopLoss', 'StopLossLimit')),
    time_in_force    text,
    quantity         numeric     NOT NULL CHECK (quantity >= 0),
    price            numeric,
    stop_price       numeric,
    decision_bar     timestamptz NOT NULL,
    attempt          integer     NOT NULL CHECK (attempt >= 1),
    client_order_id  text        NOT NULL UNIQUE CHECK (client_order_id ~ '^[a-zA-Z0-9_-]{1,36}$'),
    sent_at          timestamptz,
    replaces         text,
    UNIQUE (run_id, sleeve, symbol, decision_bar, purpose, attempt)
);

CREATE TABLE trading.exchange_order_events (
    id                 bigserial   PRIMARY KEY,
    order_id           bigint      NOT NULL REFERENCES trading.exchange_orders (id),
    recorded_at        timestamptz NOT NULL DEFAULT now(),
    status             text        NOT NULL CHECK (status IN ('Submitting', 'Accepted', 'PartiallyFilled', 'Filled', 'Canceled', 'Expired',
                                                               'ExchangeRejected', 'Unknown', 'NeverAccepted', 'LocalRejected', 'CancelRequested')),
    exchange_order_id  bigint,
    executed_quantity  numeric,
    detail             jsonb       NOT NULL DEFAULT '{}'
);

CREATE INDEX exchange_order_events_order ON trading.exchange_order_events (order_id, id);

CREATE FUNCTION trading.order_status(order_id bigint) RETURNS text
    LANGUAGE sql STABLE AS $$
    SELECT status FROM trading.exchange_order_events e WHERE e.order_id = $1 ORDER BY e.id DESC LIMIT 1
$$;

-- Duplicate orders (§2.3): at most one non-terminal order per intent (run, sleeve, symbol, decision bar, purpose).
CREATE FUNCTION trading.exchange_order_rules() RETURNS trigger
    LANGUAGE plpgsql AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM trading.exchange_orders o
               WHERE o.run_id = NEW.run_id AND o.sleeve = NEW.sleeve AND o.symbol = NEW.symbol AND o.decision_bar = NEW.decision_bar AND o.purpose = NEW.purpose
                 AND coalesce(trading.order_status(o.id), 'Submitting') NOT IN ('Filled', 'Canceled', 'Expired', 'ExchangeRejected', 'NeverAccepted', 'LocalRejected')) THEN
        RAISE EXCEPTION 'exchange order: an active order for this intent already exists (duplicate)';
    END IF;
    RETURN NEW;
END
$$;

CREATE TRIGGER exchange_order_rules BEFORE INSERT ON trading.exchange_orders
    FOR EACH ROW EXECUTE FUNCTION trading.exchange_order_rules();

-- State machine (§2.1): terminal states never change; NeverAccepted only follows Unknown.
CREATE FUNCTION trading.exchange_order_event_rules() RETURNS trigger
    LANGUAGE plpgsql AS $$
DECLARE
    previous text := trading.order_status(NEW.order_id);
BEGIN
    IF previous IS NULL AND NEW.status NOT IN ('Submitting', 'LocalRejected') THEN
        RAISE EXCEPTION 'exchange order: the first event must be Submitting or LocalRejected';
    END IF;
    IF previous IS NOT NULL AND NEW.status IN ('Submitting', 'LocalRejected') THEN
        RAISE EXCEPTION 'exchange order: % only starts an order', NEW.status;
    END IF;
    IF previous IN ('Filled', 'Canceled', 'Expired', 'ExchangeRejected', 'NeverAccepted', 'LocalRejected') THEN
        RAISE EXCEPTION 'exchange order: % is terminal', previous;
    END IF;
    IF NEW.status = 'NeverAccepted' AND previous IS DISTINCT FROM 'Unknown' THEN
        RAISE EXCEPTION 'exchange order: NeverAccepted only follows Unknown';
    END IF;
    RETURN NEW;
END
$$;

CREATE TRIGGER exchange_order_event_rules BEFORE INSERT ON trading.exchange_order_events
    FOR EACH ROW EXECUTE FUNCTION trading.exchange_order_event_rules();

-- Trades of the system's orders; (symbol, trade id) makes a duplicate fill impossible. step_bar is the bar whose
-- step applies the fill, fixed when the fill is recorded, so a replay rebuilds the same state (§4.1).
CREATE TABLE trading.exchange_fills (
    id                bigserial   PRIMARY KEY,
    order_id          bigint      NOT NULL REFERENCES trading.exchange_orders (id),
    recorded_at       timestamptz NOT NULL DEFAULT now(),
    symbol            text        NOT NULL,
    trade_id          bigint      NOT NULL,
    price             numeric     NOT NULL CHECK (price > 0),
    quantity          numeric     NOT NULL CHECK (quantity > 0),
    quote_quantity    numeric     NOT NULL CHECK (quote_quantity >= 0),
    commission        numeric     NOT NULL CHECK (commission >= 0),
    commission_asset  text        NOT NULL,
    trade_time        timestamptz NOT NULL,
    step_bar          timestamptz NOT NULL,
    UNIQUE (symbol, trade_id)
);

-- exchangeInfo snapshots of the traded symbols (§2.4); a changed hash is audited.
CREATE TABLE trading.exchange_symbol_rules (
    id           bigserial   PRIMARY KEY,
    recorded_at  timestamptz NOT NULL DEFAULT now(),
    rules        jsonb       NOT NULL,
    rules_hash   text        NOT NULL CHECK (rules_hash ~ '^[0-9a-f]{64}$')
);

-- Account balance baseline (§4.3): expected balance = baseline + the system's fills recorded after it.
CREATE TABLE trading.testnet_baselines (
    id             bigserial   PRIMARY KEY,
    recorded_at    timestamptz NOT NULL DEFAULT now(),
    run_id         bigint      NOT NULL REFERENCES trading.paper_runs (id),
    source         text        NOT NULL,
    last_fill_id   bigint      NOT NULL,
    balances       jsonb       NOT NULL,
    balances_hash  text        NOT NULL CHECK (balances_hash ~ '^[0-9a-f]{64}$')
);

DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['trading.run_closures', 'trading.exchange_orders', 'trading.exchange_order_events', 'trading.exchange_fills',
        'trading.exchange_symbol_rules', 'trading.testnet_baselines']
    LOOP
        EXECUTE format('CREATE TRIGGER append_only BEFORE UPDATE OR DELETE ON %s FOR EACH ROW EXECUTE FUNCTION audit.reject_change()', t);
    END LOOP;
END
$$;

-- Operational actions of M6 (§4.4, §6.2).
ALTER TABLE trading.ops_actions DROP CONSTRAINT ops_actions_action_check;
ALTER TABLE trading.ops_actions ADD CONSTRAINT ops_actions_action_check
    CHECK (action IN ('disable-strategy', 'close-position', 'close-all', 'testnet-activate', 'testnet-rebaseline'));

-- V10 (§6.2, D3, D30): PaperTrading -> TestnetActive is written only through the user's Ops API session, with the
-- D30 evidence and an explicit confirmation. Frozen -> TestnetActive stays impossible (lineage via PaperTrading).
CREATE OR REPLACE FUNCTION research.lifecycle_rules() RETURNS trigger
    LANGUAGE plpgsql AS $$
DECLARE
    current_state text;
BEGIN
    SELECT to_state INTO current_state FROM research.lifecycle_events WHERE frozen_id = NEW.frozen_id ORDER BY id DESC LIMIT 1;
    IF coalesce(current_state, 'Candidate') <> NEW.from_state THEN
        RAISE EXCEPTION 'lifecycle: % is not the current state (%)', NEW.from_state, coalesce(current_state, 'Candidate');
    END IF;
    IF NOT ((NEW.from_state, NEW.to_state) IN (('Candidate', 'Frozen'), ('Frozen', 'PaperTrading'), ('PaperTrading', 'TestnetActive'))
            OR NEW.to_state IN ('Disabled', 'Retired', 'Rejected')) THEN
        RAISE EXCEPTION 'lifecycle: % -> % is not allowed', NEW.from_state, NEW.to_state;
    END IF;
    IF NEW.to_state = 'PaperTrading' AND NOT (NEW.evidence ? 'parityReportHash') THEN
        RAISE EXCEPTION 'lifecycle: Frozen -> PaperTrading needs parity replay evidence';
    END IF;
    IF NEW.to_state = 'TestnetActive' THEN
        IF NOT (current_user = 'ops_api' OR pg_has_role(current_user, 'ops_api', 'MEMBER')) THEN
            RAISE EXCEPTION 'lifecycle: PaperTrading -> TestnetActive is written only by the user (Ops API)';
        END IF;
        IF NOT (NEW.evidence ? 'd30') OR (NEW.evidence -> 'confirm') IS DISTINCT FROM 'true'::jsonb THEN
            RAISE EXCEPTION 'lifecycle: PaperTrading -> TestnetActive needs the D30 evidence and confirm = true';
        END IF;
    END IF;
    RETURN NEW;
END
$$;

-- trading_app writes the outbox; ops_api reads it for the dashboard; research_app gains nothing.
GRANT SELECT, INSERT ON trading.run_closures, trading.exchange_orders, trading.exchange_order_events, trading.exchange_fills,
    trading.exchange_symbol_rules, trading.testnet_baselines TO trading_app;
GRANT USAGE ON ALL SEQUENCES IN SCHEMA trading TO trading_app;
REVOKE USAGE ON SEQUENCE trading.risk_config_versions_id_seq, trading.ops_actions_id_seq FROM trading_app;
GRANT EXECUTE ON FUNCTION trading.order_status(bigint) TO trading_app, ops_api;
GRANT SELECT ON trading.run_closures, trading.exchange_orders, trading.exchange_order_events, trading.exchange_fills,
    trading.exchange_symbol_rules, trading.testnet_baselines TO ops_api;
