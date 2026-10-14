using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.MarketData.Archive;
using PortfolioIntelligence.MarketData.Persistence;
using PortfolioIntelligence.MarketData.Tests.Support;
using static PortfolioIntelligence.MarketData.Tests.Support.SyntheticKlines;

namespace PortfolioIntelligence.MarketData.Tests;

/// <summary>
/// End-to-end M1 pipeline against an in-memory archive and a real PostgreSQL (no network): discovery,
/// classification, ranking, quality gate with replacement, E1 listing, snapshot, report.
/// </summary>
public class M1PipelineTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>, IDisposable
{
    private readonly string _cacheRoot = Path.Combine(Path.GetTempPath(), "pi-m1-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_cacheRoot))
        {
            Directory.Delete(_cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Pipeline_selects_ranks_replaces_and_reports_without_touching_holdout_data()
    {
        var archive = BuildArchive();
        var result = await RunAsync(archive);

        Assert.True(result.Selection.Complete);
        Assert.Equal(new[] { "AAAUSDT", "CCCUSDT" }, result.Selection.Selected.Select(s => s.Symbol));
        Assert.Equal(
            new[] { ("FFFUSDT", EliminationReason.Integrity), ("BBBUSDT", EliminationReason.Ended) },
            result.Selection.Eliminated.Select(e => (e.Symbol, e.Reason)));

        var ddd = result.Ranking.Single(r => r.Symbol == "DDDUSDT");
        Assert.Equal(RankingStatus.InsufficientObservations, ddd.Status);
        Assert.Equal(83, ddd.Stats.Expected);

        Assert.Contains(result.Classification, c => c.Symbol == "USDCUSDT" && c.Category == ExclusionCategory.Stablecoin);
        var e1 = Assert.Single(result.E1);
        Assert.Equal(("EEEUSDT", 2023, 2), (e1.Symbol, e1.FirstYear, e1.FirstMonth));

        // Holdout protection: nothing dated 2026 was ever requested, and the stablecoin's data was never fetched.
        Assert.DoesNotContain(archive.Requests, r => Uri.UnescapeDataString(r.ToString()).Contains("-2026-", StringComparison.Ordinal));
        Assert.DoesNotContain(archive.Requests, r => r.AbsolutePath.EndsWith(".zip", StringComparison.Ordinal) && r.AbsolutePath.Contains("USDCUSDT", StringComparison.Ordinal));
        Assert.Contains(result.Files, f => f.Status == FileStatus.Rejected && f.Key.Symbol == "FFFUSDT");
        Assert.False(File.Exists(Path.Combine(_cacheRoot, "incidents.jsonl")));

        var store = new CandleStore(database.ConnectionString);
        Assert.Equal(0, await store.CountCutoffViolationsAsync(CancellationToken.None));

        var markdown = M1ReportWriter.Markdown(result);
        Assert.Contains("core-4y-S", markdown, StringComparison.Ordinal);
        Assert.Contains("FFFUSDT", markdown, StringComparison.Ordinal);

        // Same archive, same rules: same snapshot hash (files now come from the cache).
        var second = await RunAsync(archive);
        Assert.Equal(result.SnapshotHash, second.SnapshotHash);
    }

    private async Task<M1Result> RunAsync(FakeArchive archive)
    {
        var cache = new ArchiveCacheDirectory(_cacheRoot, TimeSemanticsTests.RepositoryRootPath());
        var store = new CandleStore(database.ConnectionString);
        var incidents = new IncidentLog(cache.IncidentLogPath);
        var pipeline = new M1Pipeline(
            policy => new ArchiveClient(new ArchiveGuardHandler(policy, archive)),
            client => new ArchiveImporter(client, cache, store, incidents),
            store,
            ExclusionListLoader.LoadDefault(),
            new M1PipelineOptions { CoreSize = 2 });
        return await pipeline.RunAsync(CancellationToken.None);
    }

    private static FakeArchive BuildArchive()
    {
        var archive = new FakeArchive();
        AddFull(archive, "AAAUSDT", 3m);
        AddFull(archive, "FFFUSDT", 2.5m, corruptChecksum: (2023, 5));
        AddFull(archive, "BBBUSDT", 2m, lastMonth: (2023, 6));
        AddFull(archive, "CCCUSDT", 1m);

        // Listed 2021-10-10: in the core pool, but fewer than 90 observed ranking days.
        var listed = Utc(2021, 10, 10);
        archive.Add(new ArchiveFileKey("DDDUSDT", CandleInterval.OneHour, 2021, 10), SyntheticKlines.Lines(new ArchiveFileKey("DDDUSDT", CandleInterval.OneHour, 2021, 10), o => o >= listed));
        foreach (var month in new[] { 10, 11, 12 })
        {
            var key = new ArchiveFileKey("DDDUSDT", CandleInterval.OneDay, 2021, month);
            archive.Add(key, SyntheticKlines.Lines(key, o => o >= listed));
        }

        archive.Add(new ArchiveFileKey("USDCUSDT", CandleInterval.OneHour, 2020, 1), []);
        archive.Add(new ArchiveFileKey("EEEUSDT", CandleInterval.OneHour, 2023, 2), []);

        // Holdout-period files exist in the archive but must never be requested.
        archive.Add(new ArchiveFileKey("NEWUSDT", CandleInterval.OneHour, 2026, 2), []);
        archive.Add(new ArchiveFileKey("AAAUSDT", CandleInterval.OneHour, 2026, 1), []);
        return archive;
    }

    private static void AddFull(FakeArchive archive, string symbol, decimal volumeScale, (int Year, int Month)? lastMonth = null, (int Year, int Month)? corruptChecksum = null)
    {
        archive.Add(new ArchiveFileKey(symbol, CandleInterval.OneHour, 2020, 1), []);
        foreach (var month in new[] { 7, 8, 9, 10, 11, 12 })
        {
            var daily = new ArchiveFileKey(symbol, CandleInterval.OneDay, 2021, month);
            archive.Add(daily, SyntheticKlines.Lines(daily, volumeScale: volumeScale));
        }

        for (var start = Utc(2021, 7, 1); start < ResearchCutoff.C; start = start.AddMonths(1))
        {
            if (lastMonth is { } last && (start.Year, start.Month).CompareTo(last) > 0)
            {
                break;
            }

            foreach (var interval in new[] { CandleInterval.OneHour, CandleInterval.FourHours })
            {
                var key = new ArchiveFileKey(symbol, interval, start.Year, start.Month);
                var corrupt = interval == CandleInterval.OneHour && corruptChecksum == (start.Year, start.Month);
                archive.Add(key, SyntheticKlines.Lines(key, volumeScale: volumeScale), checksumOverride: corrupt ? new string('0', 64) + "  " + key.FileName : null);
            }
        }
    }
}
