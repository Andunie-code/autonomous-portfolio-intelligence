-- M1: research candles with the research cutoff enforced in the database (T4, docs/m1-data-policy.md §7).
-- C is defined once here as market.research_cutoff(); the code constant ResearchCutoff.C must equal it (H1d).
-- Close times are computed in UTC so the check does not depend on the session time zone (H1c).

CREATE FUNCTION market.research_cutoff() RETURNS timestamptz
    LANGUAGE sql IMMUTABLE PARALLEL SAFE
    AS $$ SELECT timestamptz '2026-01-01 00:00:00+00' $$;

CREATE FUNCTION market.candle_close_time(open_time timestamptz, candle_interval text) RETURNS timestamptz
    LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
    AS $$
        SELECT ((open_time AT TIME ZONE 'UTC') + CASE candle_interval
                    WHEN '1h' THEN interval '1 hour'
                    WHEN '4h' THEN interval '4 hours'
                    WHEN '1d' THEN interval '1 day'
                    WHEN '1w' THEN interval '7 days'
                    WHEN '1M' THEN interval '1 month'
                END) AT TIME ZONE 'UTC'
    $$;

CREATE TABLE market.source_files (
    id           bigserial   PRIMARY KEY,
    archive_key  text        NOT NULL UNIQUE,
    sha256       text        NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    size_bytes   bigint      NOT NULL CHECK (size_bytes >= 0),
    row_count    integer     NOT NULL CHECK (row_count >= 0),
    imported_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE market.candles (
    symbol           text        NOT NULL CHECK (symbol ~ '^[A-Z0-9]{2,30}$'),
    candle_interval  text        NOT NULL CHECK (candle_interval IN ('1h', '4h', '1d', '1w', '1M')),
    open_time        timestamptz NOT NULL,
    open             numeric     NOT NULL,
    high             numeric     NOT NULL,
    low              numeric     NOT NULL,
    close            numeric     NOT NULL,
    base_volume      numeric     NOT NULL CHECK (base_volume >= 0),
    quote_volume     numeric     NOT NULL CHECK (quote_volume >= 0),
    trade_count      bigint      NOT NULL CHECK (trade_count >= 0),
    source_file_id   bigint      NOT NULL REFERENCES market.source_files (id),
    PRIMARY KEY (symbol, candle_interval, open_time),
    CONSTRAINT candles_ohlc CHECK (low > 0 AND low <= LEAST(open, close) AND GREATEST(open, close) <= high),
    CONSTRAINT candles_before_research_cutoff
        CHECK (market.candle_close_time(open_time, candle_interval) <= market.research_cutoff())
);

CREATE TABLE market.dataset_snapshots (
    id              bigserial   PRIMARY KEY,
    created_at      timestamptz NOT NULL DEFAULT now(),
    policy_version  text        NOT NULL,
    content_hash    text        NOT NULL CHECK (content_hash ~ '^[0-9a-f]{64}$'),
    manifest        jsonb       NOT NULL
);
