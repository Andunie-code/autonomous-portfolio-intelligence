-- M7 (docs/m7-protocol.md, ADR-0022): controlled web research. Source documents and findings are append-only
-- provenance; an arXiv version submitted on or after the research cutoff can never be recorded (U-M7-2).

CREATE SCHEMA sources;

CREATE TABLE sources.source_documents (
    id                  bigserial   PRIMARY KEY,
    recorded_at         timestamptz NOT NULL DEFAULT now(),
    url                 text        NOT NULL CHECK (url LIKE 'https://%'),
    canonical_url       text        NOT NULL CHECK (canonical_url LIKE 'https://%'),
    kind                text        NOT NULL CHECK (kind IN ('ArxivApi', 'ArxivPdf', 'ArxivAbstract', 'BinanceDocs')),
    tier                text        NOT NULL CHECK (tier IN ('T1', 'T2')),
    text_kind           text        NOT NULL CHECK (text_kind IN ('Pdf', 'Abstract', 'Html')),
    arxiv_id            text,
    arxiv_version       integer     CHECK (arxiv_version >= 1),
    published_at        timestamptz,
    version_at          timestamptz,
    fetched_at          timestamptz NOT NULL,
    content_sha256      text        NOT NULL CHECK (content_sha256 ~ '^[0-9a-f]{64}$'),
    text_sha256         text        NOT NULL CHECK (text_sha256 ~ '^[0-9a-f]{64}$'),
    bytes               bigint      NOT NULL CHECK (bytes >= 0),
    content_type        text        NOT NULL,
    extractor_version   text        NOT NULL,
    normalizer_version  text        NOT NULL,
    hidden_characters   integer     NOT NULL CHECK (hidden_characters >= 0),
    removed_characters  integer     NOT NULL CHECK (removed_characters >= 0),
    truncated           boolean     NOT NULL,
    -- arXiv records carry their versioned identity and both dates, and both are before C (ADR-0006, U-M7-2).
    CHECK (kind NOT IN ('ArxivApi', 'ArxivPdf', 'ArxivAbstract')
           OR (arxiv_id IS NOT NULL AND arxiv_version IS NOT NULL AND published_at < market.research_cutoff() AND version_at < market.research_cutoff()))
);

CREATE TABLE sources.findings (
    id                bigserial   PRIMARY KEY,
    recorded_at       timestamptz NOT NULL DEFAULT now(),
    document_id       bigint      NOT NULL REFERENCES sources.source_documents (id),
    trace_id          uuid        NOT NULL,
    kind              text        NOT NULL,
    claim             text        NOT NULL,
    quote             text        NOT NULL,
    quote_start       integer,
    quote_end         integer,
    numbers           jsonb       NOT NULL,
    relevance         text        NOT NULL,
    verified          boolean     NOT NULL,
    rejection_reason  text,
    -- A verified finding has its quote located in the document text; a rejected one says why (m7-protocol §4.3).
    CHECK ((verified AND rejection_reason IS NULL AND quote_start IS NOT NULL AND quote_end > quote_start)
           OR (NOT verified AND rejection_reason IS NOT NULL))
);

CREATE TABLE sources.research_requests (
    id          bigserial   PRIMARY KEY,
    at          timestamptz NOT NULL DEFAULT now(),
    topic       text        NOT NULL CHECK (length(topic) BETWEEN 1 AND 200),
    categories  text[]      NOT NULL,
    status      text        NOT NULL CHECK (status IN ('recorded', 'refused-closed'))
);

DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['sources.source_documents', 'sources.findings', 'sources.research_requests']
    LOOP
        EXECUTE format('CREATE TRIGGER append_only BEFORE UPDATE OR DELETE ON %s FOR EACH ROW EXECUTE FUNCTION audit.reject_change()', t);
    END LOOP;
END
$$;

-- A hypothesis may name the findings it came from (m7-protocol §8.2). Every named finding must exist, be verified and
-- come from a pre-cutoff document; the application checks the same rule before inserting (HypothesisStore).
ALTER TABLE research.hypotheses ADD COLUMN source_finding_ids bigint[];

CREATE FUNCTION research.check_source_findings() RETURNS trigger
    LANGUAGE plpgsql SET search_path = pg_catalog AS $$
DECLARE
    usable integer;
BEGIN
    IF NEW.source_finding_ids IS NULL OR cardinality(NEW.source_finding_ids) = 0 THEN
        RETURN NEW;
    END IF;
    SELECT count(DISTINCT f.id) INTO usable
    FROM sources.findings f JOIN sources.source_documents d ON d.id = f.document_id
    WHERE f.id = ANY (NEW.source_finding_ids) AND f.verified
      AND (d.arxiv_id IS NULL OR (d.published_at < market.research_cutoff() AND d.version_at < market.research_cutoff()));
    IF usable <> (SELECT count(DISTINCT x) FROM unnest(NEW.source_finding_ids) x) THEN
        RAISE EXCEPTION 'hypotheses: every source finding must exist, be verified and come from a pre-cutoff document';
    END IF;
    RETURN NEW;
END
$$;

CREATE TRIGGER hypotheses_source_findings BEFORE INSERT ON research.hypotheses
    FOR EACH ROW EXECUTE FUNCTION research.check_source_findings();

-- One-arm qualification design (ADR-0022 §5). Existing protocols are two-arm Fisher; their documents and hashes are unchanged.
ALTER TABLE ai.qualification_protocols ADD COLUMN design text NOT NULL DEFAULT 'TwoArmFisher'
    CHECK (design IN ('TwoArmFisher', 'OneArmBinomial'));
ALTER TABLE ai.qualification_protocols ADD COLUMN p0 double precision NOT NULL DEFAULT 0;
ALTER TABLE ai.qualification_protocols ADD CONSTRAINT qualification_protocols_p0_check
    CHECK (design = 'TwoArmFisher' OR (p0 > 0 AND p0 < 1));

-- Research.Host only (architecture §1): append-only access to sources. Trading and the dashboard see nothing of it.
GRANT USAGE ON SCHEMA sources TO research_app;
GRANT SELECT, INSERT ON ALL TABLES IN SCHEMA sources TO research_app;
GRANT USAGE ON ALL SEQUENCES IN SCHEMA sources TO research_app;
