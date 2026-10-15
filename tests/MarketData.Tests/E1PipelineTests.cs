using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.MarketData.Archive;
using PortfolioIntelligence.MarketData.Persistence;
using PortfolioIntelligence.MarketData.Tests.Support;
using static PortfolioIntelligence.MarketData.Tests.Support.SyntheticKlines;

namespace PortfolioIntelligence.MarketData.Tests;

/// <summary>E1 category rules and the M3 E1 subset pipeline against an in-memory archive (no network; m3-protocol §11).</summary>
public class E1PipelineTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>, IDisposable
{
    private readonly string _cacheRoot = Path.Combine(Path.GetTempPath(), "pi-e1-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_cacheRoot))
        {
            Directory.Delete(_cacheRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(2021, 12, 2, 0, false)] // core deadline itself: core-eligible, not E1
    [InlineData(2021, 12, 2, 1, true)]
    [InlineData(2024, 12, 2, 0, true)]  // U0 = 2025-01-01 exactly
    [InlineData(2024, 12, 2, 1, false)] // U0 after 2025-01-01: E2
    public void E1_candidates_lie_between_the_core_deadline_and_U0_2025_01_01(int year, int month, int day, int hour, bool expected)
    {
        Assert.Equal(expected, PairCategories.IsE1Candidate(Utc(year, month, day, hour)));
    }

    [Fact]
    public void Quality_failure_eliminates_and_never_demotes_and_labels_count_full_years()
    {
        var open = Utc(2022, 3, 10);

        Assert.Equal(PairCategory.E1, PairCategories.Categorize(open, qualityPasses: true));
        Assert.Null(PairCategories.Categorize(open, qualityPasses: false));
        Assert.Equal(PairCategory.E2, PairCategories.Categorize(Utc(2024, 12, 20), qualityPasses: true));
        Assert.Equal(Utc(2022, 4, 9), PairCategories.U0(open));
        Assert.Equal(new TimeRange(Utc(2022, 4, 9), ResearchCutoff.C), PairCategories.ResearchWindow(open));
        Assert.Equal("E1-3y", PairCategories.EvidenceLabel(PairCategory.E1, open));
        Assert.Equal("E1-1y", PairCategories.EvidenceLabel(PairCategory.E1, Utc(2024, 11, 1)));
        Assert.Equal("core-4y", PairCategories.EvidenceLabel(PairCategory.Core));
    }

    [Fact]
    public async Task Pipeline_ranks_replaces_failing_pairs_and_never_requests_holdout_data()
    {
        var archive = new FakeArchive();
        Add(archive, "AAAUSDT", Utc(2020, 1, 1), 5m);          // core pair: skipped
        Add(archive, "EAAUSDT", Utc(2022, 3, 10, 6), 3m);       // rank 1, passes
        Add(archive, "EBBUSDT", Utc(2022, 6, 5), 2m, lastMonth: (2024, 6)); // rank 2, data ends: eliminated
        Add(archive, "ECCUSDT", Utc(2023, 1, 20), 1m);          // rank 3, replaces EBB
        Add(archive, "EDGUSDT", Utc(2021, 12, 1), 9m, onlyFirstMonth: true); // first open before the core deadline
        Add(archive, "LATUSDT", Utc(2024, 12, 10), 9m, onlyFirstMonth: true); // U0 after 2025-01-01
        archive.Add(new ArchiveFileKey("USDCUSDT", CandleInterval.OneHour, 2022, 5), []);
        archive.Add(new ArchiveFileKey("EAAUSDT", CandleInterval.OneHour, 2026, 1), []); // holdout file exists, must not be requested

        var store = new CandleStore(database.ConnectionString);
        var cache = new ArchiveCacheDirectory(_cacheRoot, TimeSemanticsTests.RepositoryRootPath());
        var pipeline = new E1Pipeline(
            policy => new ArchiveClient(new ArchiveGuardHandler(policy, archive)),
            client => new ArchiveImporter(client, cache, store, new IncidentLog(cache.IncidentLogPath)),
            store,
            ExclusionListLoader.LoadDefault(),
            new HashSet<string>(StringComparer.Ordinal) { "AAAUSDT" },
            new E1PipelineOptions { Limit = 2 });

        var result = await pipeline.RunAsync(CancellationToken.None);

        Assert.Equal(new[] { "EAAUSDT", "EBBUSDT", "ECCUSDT" }, result.Candidates.Select(c => c.Symbol));
        Assert.Equal(Utc(2022, 3, 10, 6), result.Candidates[0].FirstOpen);
        Assert.Equal(new[] { "EAAUSDT", "ECCUSDT" }, result.Selected.Select(s => s.Candidate.Symbol));
        var eliminated = Assert.Single(result.Eliminated);
        Assert.Equal(("EBBUSDT", EliminationReason.Ended), (eliminated.Candidate.Symbol, eliminated.Quality.Reason));
        Assert.True(result.Complete);
        Assert.All(result.Selected, s => Assert.Equal(PairCategory.E1, s.Category));

        Assert.DoesNotContain(archive.Requests, r => Uri.UnescapeDataString(r.ToString()).Contains("-2026-", StringComparison.Ordinal));
        Assert.DoesNotContain(archive.Requests, r => r.AbsolutePath.EndsWith(".zip", StringComparison.Ordinal) && r.AbsolutePath.Contains("AAAUSDT", StringComparison.Ordinal));
        Assert.Equal(0, await store.CountCutoffViolationsAsync(CancellationToken.None));
        Assert.Contains("EAAUSDT", E1ReportWriter.Markdown(result), StringComparison.Ordinal);
    }

    private static void Add(FakeArchive archive, string symbol, DateTimeOffset firstOpen, decimal volumeScale, (int Year, int Month)? lastMonth = null, bool onlyFirstMonth = false)
    {
        bool Listed(DateTimeOffset open) => open >= firstOpen;
        for (var month = new DateTimeOffset(firstOpen.Year, firstOpen.Month, 1, 0, 0, 0, TimeSpan.Zero); month < ResearchCutoff.C; month = month.AddMonths(1))
        {
            if (lastMonth is { } last && (month.Year, month.Month).CompareTo(last) > 0)
            {
                break;
            }

            foreach (var interval in new[] { CandleInterval.OneHour, CandleInterval.FourHours, CandleInterval.OneDay })
            {
                var key = new ArchiveFileKey(symbol, interval, month.Year, month.Month);
                archive.Add(key, Lines(key, Listed, volumeScale));
            }

            if (onlyFirstMonth)
            {
                break;
            }
        }
    }
}
