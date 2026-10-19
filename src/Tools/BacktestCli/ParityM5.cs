using System.Text.Json;
using Npgsql;
using PortfolioIntelligence.Execution;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.MarketData.Archive;
using PortfolioIntelligence.MarketData.Persistence;

/// <summary>
/// M5 (docs/m5-protocol.md §4.5, §5): freeze-import records a committed cohort manifest as frozen strategies on the
/// ops side (Candidate → Frozen); parity-m5 runs the L1/L2 parity replay on the research window and, with --record
/// and a pass, writes Frozen → PaperTrading with the report hash. Research role, research data (&lt; C) only.
/// </summary>
internal static class ParityM5
{
    public static async Task<int> FreezeImportAsync(string connectionString, string manifestPath, long cohortId, string manifestCommit)
    {
        var json = await File.ReadAllTextAsync(manifestPath);
        var manifest = CohortManifestReader.Read(json);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var exists = new NpgsqlCommand("SELECT count(*) FROM research.frozen_strategies WHERE cohort_id = $1", connection, transaction))
        {
            exists.Parameters.AddWithValue(cohortId);
            if ((long)(await exists.ExecuteScalarAsync())! > 0)
            {
                Console.Error.WriteLine($"Cohort {cohortId} is already frozen on the ops side; nothing written.");
                return 3;
            }
        }

        foreach (var member in manifest.Members)
        {
            await Execute("INSERT INTO research.strategy_versions (name, definition_hash, definition) VALUES ($1, $2, $3::jsonb) ON CONFLICT (definition_hash) DO NOTHING",
                member.Name, member.DefinitionHash, member.DefinitionJson);
            await using var frozen = new NpgsqlCommand("""
                INSERT INTO research.frozen_strategies (cohort_id, manifest_hash, member_index, name, definition_hash, manifest)
                VALUES ($1, $2, $3, $4, $5, $6) RETURNING id
                """, connection, transaction);
            frozen.Parameters.AddWithValue(cohortId);
            frozen.Parameters.AddWithValue(manifest.Hash);
            frozen.Parameters.AddWithValue(member.Index);
            frozen.Parameters.AddWithValue(member.Name);
            frozen.Parameters.AddWithValue(member.DefinitionHash);
            frozen.Parameters.AddWithValue(json);
            var id = (long)(await frozen.ExecuteScalarAsync())!;
            await Execute("INSERT INTO research.lifecycle_events (frozen_id, from_state, to_state, actor, evidence) VALUES ($1, 'Candidate', 'Frozen', 'research', $2::jsonb)",
                id, JsonSerializer.Serialize(new { cohortId, manifestHash = manifest.Hash, manifestCommit }));
            Console.WriteLine($"Frozen: cohort {cohortId} member {member.Index} {member.Name} ({member.DefinitionHash[..12]})");
        }

        await transaction.CommitAsync();
        return 0;

        async Task Execute(string sql, params object[] parameters)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            foreach (var p in parameters)
            {
                command.Parameters.AddWithValue(p);
            }

            await command.ExecuteNonQueryAsync();
        }
    }

    public static async Task<int> ParityAsync(string connectionString, string manifestPath, long cohortId, bool record, string engineSha)
    {
        var json = await File.ReadAllTextAsync(manifestPath);
        var manifest = CohortManifestReader.Read(json);
        var store = new CandleStore(connectionString);
        var range = new TimeRange(ResearchCutoff.RankingWindow.Start, ResearchCutoff.C);
        var candles = new Dictionary<string, IReadOnlyList<Candle>>();
        foreach (var (symbol, _) in manifest.Pairs)
        {
            candles[symbol] = await store.ReadAsync(symbol, manifest.Interval, range, CancellationToken.None);
        }

        var started = DateTimeOffset.UtcNow;
        var report = await ParityReplay.RunAsync(json, cohortId, candles, ResearchCutoff.U, ResearchCutoff.C, TimeSpan.FromDays(90), CancellationToken.None);
        var reports = Directory.CreateDirectory(Path.Combine(ArchiveCacheDirectory.DefaultRoot, "reports")).FullName;
        var path = Path.Combine(reports, $"m5-parity-cohort{cohortId}-{started:yyyyMMdd'T'HHmmss'Z'}.json");
        await File.WriteAllTextAsync(path, report.Json());

        Console.WriteLine($"Parity replay {manifest.Hash[..12]}: {report.Steps} steps, {report.Restarts} restarts, {report.Fills} fills, {(DateTimeOffset.UtcNow - started).TotalSeconds:F0} s");
        Console.WriteLine($"  L1 kernel {report.KernelL1Hash[..12]} paper {report.PaperL1Hash[..12]} -> {(report.L1Equal ? "EQUAL" : "DIFFERENT")}");
        Console.WriteLine($"  L2 kernel {report.KernelL2Hash[..12]} paper {report.PaperL2Hash[..12]} -> {(report.L2Equal ? "EQUAL" : "DIFFERENT")}");
        Console.WriteLine($"  reconciliation {(report.ReconciliationOk ? "ok" : "FAILED")}{(report.FirstDifference is { } d ? "; first difference " + d : string.Empty)}");
        foreach (var s in report.L3)
        {
            Console.WriteLine($"  L3 {s.Strategy}: trades isolated {s.IsolatedTrades} portfolio {s.PortfolioTrades}, timing match {s.TimingMatchRatio:P1}, " +
                              $"mean trade {s.IsolatedMeanTradeReturnPct:F2} % vs {s.PortfolioMeanTradeReturnPct:F2} %, constraints [{string.Join(", ", s.PortfolioConstraintDecisions.Select(kv => $"{kv.Key} {kv.Value}"))}]");
        }

        Console.WriteLine($"Report {report.Hash()}: {path}");
        if (!report.Passed)
        {
            Console.WriteLine("Parity FAILED: no lifecycle change.");
            return 2;
        }

        if (!record)
        {
            Console.WriteLine("Parity passed (dry run; --record writes Frozen -> PaperTrading).");
            return 0;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var member in manifest.Members)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO research.lifecycle_events (frozen_id, from_state, to_state, actor, evidence)
                SELECT id, 'Frozen', 'PaperTrading', 'parity-m5', $3::jsonb FROM research.frozen_strategies WHERE cohort_id = $1 AND member_index = $2
                """, connection, transaction);
            insert.Parameters.AddWithValue(cohortId);
            insert.Parameters.AddWithValue(member.Index);
            insert.Parameters.AddWithValue(JsonSerializer.Serialize(new
            {
                parityReportHash = report.Hash(),
                l1 = report.KernelL1Hash,
                l2 = report.KernelL2Hash,
                engineSha,
            }));
            if (await insert.ExecuteNonQueryAsync() != 1)
            {
                throw new InvalidOperationException($"Cohort {cohortId} member {member.Index} is not frozen on the ops side (run freeze-import first).");
            }
        }

        await transaction.CommitAsync();
        Console.WriteLine($"Recorded Frozen -> PaperTrading for cohort {cohortId} ({manifest.Members.Count} strategies).");
        return 0;
    }
}
