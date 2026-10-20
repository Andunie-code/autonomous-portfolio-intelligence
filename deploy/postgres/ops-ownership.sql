-- M5 bootstrap (docs/m5-protocol.md §4.2): transfers the M0-M4 ops objects from the superuser to ops_owner, so the
-- ops migrations run as ops_owner afterwards. Applied by scripts/m5-bootstrap.ps1; pinned by OpsBootstrapTests.
ALTER DATABASE ops OWNER TO ops_owner;
DO $$
DECLARE r record;
BEGIN
    FOR r IN SELECT nspname FROM pg_namespace WHERE nspname IN ('market', 'research', 'trading', 'audit', 'ai') LOOP
        EXECUTE format('ALTER SCHEMA %I OWNER TO ops_owner', r.nspname);
    END LOOP;
    FOR r IN SELECT n.nspname, c.relname, c.relkind FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname IN ('market', 'research', 'trading', 'audit', 'ai', 'public') AND c.relkind IN ('r', 'p', 'v', 'm')
               AND c.relowner IN (SELECT oid FROM pg_roles WHERE rolsuper) LOOP
        EXECUTE format('ALTER %s %I.%I OWNER TO ops_owner', CASE r.relkind WHEN 'v' THEN 'VIEW' WHEN 'm' THEN 'MATERIALIZED VIEW' ELSE 'TABLE' END, r.nspname, r.relname);
    END LOOP;
    FOR r IN SELECT n.nspname, c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname IN ('market', 'research', 'trading', 'audit', 'ai', 'public') AND c.relkind = 'S'
               AND c.relowner IN (SELECT oid FROM pg_roles WHERE rolsuper) LOOP
        EXECUTE format('ALTER SEQUENCE %I.%I OWNER TO ops_owner', r.nspname, r.relname);
    END LOOP;
    FOR r IN SELECT p.oid::regprocedure AS signature FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
             WHERE n.nspname IN ('market', 'research', 'trading', 'audit', 'ai') AND p.proowner IN (SELECT oid FROM pg_roles WHERE rolsuper) LOOP
        EXECUTE format('ALTER FUNCTION %s OWNER TO ops_owner', r.signature);
    END LOOP;
END
$$;
