using System.Net;
using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.MarketData.Archive;
using PortfolioIntelligence.MarketData.Tests.Support;

namespace PortfolioIntelligence.MarketData.Tests;

/// <summary>H2 and H2c: request and response guard, redirects, checksums, cache location (policy §3).</summary>
public class ArchiveAccessTests
{
    private static readonly ArchiveRequestPolicy Core = ArchiveRequestPolicy.CorePhase();

    [Theory]
    [InlineData("https://data.binance.vision/data/spot/monthly/klines/BTCUSDT/1h/BTCUSDT-1h-2025-12.zip")]
    [InlineData("https://data.binance.vision/data/spot/monthly/klines/BTCUSDT/4h/BTCUSDT-4h-2022-01.zip.CHECKSUM")]
    [InlineData("https://s3-ap-northeast-1.amazonaws.com/data.binance.vision?prefix=data%2Fspot%2Fmonthly%2Fklines%2F&max-keys=1000&delimiter=%2F")]
    [InlineData("https://s3-ap-northeast-1.amazonaws.com/data.binance.vision?prefix=data%2Fspot%2Fmonthly%2Fklines%2FBTCUSDT%2F1h%2FBTCUSDT-1h-2021-&max-keys=1")]
    public void Allowed_requests_pass(string url)
    {
        Core.Validate(new Uri(url));
    }

    [Theory]
    [InlineData("https://data.binance.vision/data/spot/monthly/klines/BTCUSDT/1h/BTCUSDT-1h-2026-01.zip")]
    [InlineData("https://data.binance.vision/data/spot/monthly/klines/BTCUSDT/1w/BTCUSDT-1w-2025-12.zip")]
    [InlineData("https://data.binance.vision/data/spot/daily/klines/BTCUSDT/1h/BTCUSDT-1h-2025-12-31.zip")]
    [InlineData("https://data.binance.vision/data/futures/um/monthly/klines/BTCUSDT/1h/BTCUSDT-1h-2023-01.zip")]
    [InlineData("http://data.binance.vision/data/spot/monthly/klines/BTCUSDT/1h/BTCUSDT-1h-2023-01.zip")]
    [InlineData("https://api.binance.com/api/v3/exchangeInfo")]
    [InlineData("https://evil.example/data/spot/monthly/klines/BTCUSDT/1h/BTCUSDT-1h-2023-01.zip")]
    [InlineData("https://data.binance.vision/?prefix=data%2Fspot%2Fmonthly%2Fklines%2F&max-keys=1000&delimiter=%2F")]
    [InlineData("https://s3-ap-northeast-1.amazonaws.com/data.binance.vision/data/spot/monthly/klines/BTCUSDT/1h/BTCUSDT-1h-2023-01.zip")]
    [InlineData("https://s3-ap-northeast-1.amazonaws.com/other-bucket?prefix=data%2Fspot%2Fmonthly%2Fklines%2F&delimiter=%2F")]
    [InlineData("https://s3.amazonaws.com/data.binance.vision?prefix=data%2Fspot%2Fmonthly%2Fklines%2F&delimiter=%2F")]
    [InlineData("https://s3-ap-northeast-1.amazonaws.com/data.binance.vision?prefix=data%2Fspot%2Fmonthly%2Fklines%2FBTCUSDT%2F1h%2FBTCUSDT-1h-2022-&max-keys=1")]
    [InlineData("https://s3-ap-northeast-1.amazonaws.com/data.binance.vision?prefix=data%2Fspot%2Fmonthly%2Fklines%2FBTCUSDT%2F1h%2F&max-keys=1")]
    [InlineData("https://s3-ap-northeast-1.amazonaws.com/data.binance.vision?prefix=data%2Fspot%2Fmonthly%2Fklines%2F&max-keys=1000")]
    [InlineData("https://s3-ap-northeast-1.amazonaws.com/data.binance.vision?prefix=data%2Fspot%2Fmonthly%2Fklines%2F&delimiter=%2F&start-after=x")]
    public void Forbidden_requests_are_rejected(string url)
    {
        Assert.Throws<ArchivePolicyViolationException>(() => Core.Validate(new Uri(url)));
    }

    [Fact]
    public void E1_phase_allows_2024_prefixes_but_never_2025()
    {
        var e1 = ArchiveRequestPolicy.E1Phase();
        e1.Validate(new Uri("https://s3-ap-northeast-1.amazonaws.com/data.binance.vision?prefix=data%2Fspot%2Fmonthly%2Fklines%2FXUSDT%2F1h%2FXUSDT-1h-2024-&max-keys=1"));
        Assert.Throws<ArchivePolicyViolationException>(() =>
            e1.Validate(new Uri("https://s3-ap-northeast-1.amazonaws.com/data.binance.vision?prefix=data%2Fspot%2Fmonthly%2Fklines%2FXUSDT%2F1h%2FXUSDT-1h-2025-&max-keys=1")));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArchiveRequestPolicy(2026));
    }

    [Fact]
    public async Task Rejected_request_never_reaches_the_inner_handler()
    {
        var fake = new FakeArchive();
        using var client = new HttpClient(new ArchiveGuardHandler(Core, fake));

        await Assert.ThrowsAsync<ArchivePolicyViolationException>(() =>
            client.GetAsync("https://data.binance.vision/data/spot/monthly/klines/BTCUSDT/1h/BTCUSDT-1h-2026-01.zip"));
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task Redirect_responses_are_rejected()
    {
        using var client = new HttpClient(new ArchiveGuardHandler(Core, new StaticHandler(HttpStatusCode.Found)));

        await Assert.ThrowsAsync<ArchivePolicyViolationException>(() =>
            client.GetAsync("https://data.binance.vision/data/spot/monthly/klines/BTCUSDT/1h/BTCUSDT-1h-2023-01.zip"));
    }

    [Fact]
    public void Network_handler_does_not_follow_redirects()
    {
        using var handler = (SocketsHttpHandler)ArchiveGuardHandler.CreateNetworkHandler();
        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public void Listed_entries_outside_the_prefix_or_after_the_listing_year_stop_the_run()
    {
        const string prefix = "data/spot/monthly/klines/BTCUSDT/1h/BTCUSDT-1h-2021-";
        Core.ValidateListedKey(prefix, prefix + "03.zip");
        Assert.Throws<ArchivePolicyViolationException>(() => Core.ValidateListedKey(prefix, "data/spot/monthly/klines/BTCUSDT/1h/BTCUSDT-1h-2026-01.zip"));
        Assert.Throws<ArchivePolicyViolationException>(() => Core.ValidateListedKey("data/spot/monthly/klines/", "data/spot/monthly/klines/X/1h/X-1h-2026-01.zip"));
    }

    [Fact]
    public async Task Paginated_symbol_listing_is_read_completely()
    {
        var fake = new FakeArchive { ListingPageSize = 2 };
        foreach (var symbol in new[] { "AAAUSDT", "BBBUSDT", "CCCUSDT", "DDDUSDT", "EEEUSDT" })
        {
            fake.Add(new ArchiveFileKey(symbol, CandleInterval.OneDay, 2021, 1), []);
        }

        var client = new ArchiveClient(new ArchiveGuardHandler(Core, fake));

        Assert.Equal(new[] { "AAAUSDT", "BBBUSDT", "CCCUSDT", "DDDUSDT", "EEEUSDT" }, await client.ListSymbolsAsync(CancellationToken.None));
        Assert.Equal(3, fake.Requests.Count);
    }

    [Fact]
    public async Task First_month_is_found_through_year_prefixes_only()
    {
        var fake = new FakeArchive();
        fake.Add(new ArchiveFileKey("XYZUSDT", CandleInterval.OneHour, 2020, 3), []);
        fake.Add(new ArchiveFileKey("XYZUSDT", CandleInterval.OneHour, 2020, 4), []);
        var client = new ArchiveClient(new ArchiveGuardHandler(Core, fake));

        var first = await client.FindFirstMonthAsync("XYZUSDT", Enumerable.Range(2017, 5), CancellationToken.None);

        Assert.Equal((2020, 3), (first!.Year, first.Month));
        Assert.All(fake.Requests, r => Assert.Contains("max-keys=1", r.Query, StringComparison.Ordinal));
        Assert.Equal(4, fake.Requests.Count);
    }

    [Fact]
    public async Task Html_listing_response_stops_the_run_with_a_policy_violation()
    {
        var client = new ArchiveClient(new ArchiveGuardHandler(Core, new FakeArchive { ListingReturnsHtml = true }));

        var error = await Assert.ThrowsAsync<ArchivePolicyViolationException>(() => client.ListSymbolsAsync(CancellationToken.None));
        Assert.Contains("not XML", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listing_goes_to_the_s3_host_and_files_to_the_file_host()
    {
        var fake = new FakeArchive();
        var key = new ArchiveFileKey("AAAUSDT", CandleInterval.OneDay, 2021, 1);
        fake.Add(key, SyntheticKlines.Lines(key));
        var client = new ArchiveClient(new ArchiveGuardHandler(Core, fake));

        await client.ListSymbolsAsync(CancellationToken.None);
        await client.DownloadAsync(key, CancellationToken.None);

        Assert.Equal(ArchiveRequestPolicy.ListingHost, fake.Requests[0].Host);
        Assert.Equal("/data.binance.vision", fake.Requests[0].AbsolutePath);
        Assert.All(fake.Requests.Skip(1), r => Assert.Equal(ArchiveRequestPolicy.FileHost, r.Host));
    }

    [Fact]
    public void Checksum_mismatch_or_missing_checksum_rejects_the_file()
    {
        var key = new ArchiveFileKey("BTCUSDT", CandleInterval.OneDay, 2023, 1);
        var zip = SyntheticKlines.Zip(key, SyntheticKlines.Lines(key));

        Assert.Equal(ChecksumVerifier.Sha256Hex(zip), ChecksumVerifier.Verify(key, zip, SyntheticKlines.Checksum(key, zip)));
        Assert.Throws<ArchiveDataException>(() => ChecksumVerifier.Verify(key, zip, null));
        Assert.Throws<ArchiveDataException>(() => ChecksumVerifier.Verify(key, zip, new string('0', 64) + "  " + key.FileName));
        Assert.Throws<ArchiveDataException>(() => ChecksumVerifier.Verify(key, zip, ChecksumVerifier.Sha256Hex(zip) + "  OTHER.zip"));
    }

    [Fact]
    public void Cache_directory_inside_the_repository_or_onedrive_is_rejected()
    {
        var repository = TimeSemanticsTests.RepositoryRootPath();

        Assert.Throws<ArgumentException>(() => new ArchiveCacheDirectory(Path.Combine(repository, "data", "cache"), repository));
        Assert.Throws<ArgumentException>(() => new ArchiveCacheDirectory(Path.Combine(Path.GetTempPath(), "OneDrive", "cache"), repository));
        var outside = Path.Combine(Path.GetTempPath(), "pi-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(Path.GetFullPath(outside), new ArchiveCacheDirectory(outside, repository).Root);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    private sealed class StaticHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }
}
