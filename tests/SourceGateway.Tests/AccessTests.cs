using System.Net;
using PortfolioIntelligence.SourceGateway.Tests.Support;

namespace PortfolioIntelligence.SourceGateway.Tests;

/// <summary>
/// Roadmap M7 criterion 1 (m7-protocol §2.1–§2.3): URLs outside the allowlist and anything reached by a redirect are not
/// accessible; robots.txt, the per-host interval, retries and size caps. Zero network.
/// </summary>
public class AccessTests
{
    private static readonly SourceRequestPolicy Policy = new(SourceConfiguration.LoadDefault());

    private static string Api(string query) => "https://export.arxiv.org/api/query?" + query;

    private static readonly string Filter = Uri.EscapeDataString("(cat:q-fin.TR) AND " + SourceRequestPolicy.SubmittedBeforeCutoff);

    [Theory]
    [InlineData("https://arxiv.org/pdf/2301.01234v2", SourceKind.ArxivPdf)]
    [InlineData("https://arxiv.org/pdf/q-fin/0601001v1", SourceKind.ArxivPdf)]
    [InlineData("https://arxiv.org/abs/2301.01234v2", SourceKind.ArxivAbstract)]
    [InlineData("https://developers.binance.com/docs/binance-spot-api-docs/filters", SourceKind.BinanceDocs)]
    [InlineData("https://arxiv.org/robots.txt", SourceKind.Robots)]
    public void Allowlisted_urls_are_accepted(string url, SourceKind kind)
    {
        Assert.Equal(kind, Policy.Validate(new Uri(url)).Kind);
    }

    [Fact]
    public void Api_queries_built_by_the_query_builder_are_accepted()
    {
        var configuration = SourceConfiguration.LoadDefault();
        var search = ArxivQuery.Search(ArxivQuery.SearchQuery(["momentum", "crypto"], "abs", [], configuration), 0, 10);
        var version = ArxivQuery.Version("2301.01234v2", configuration);

        Assert.Equal(SourceKind.ArxivApi, Policy.Validate(search).Kind);
        Assert.Equal(SourceKind.ArxivApi, Policy.Validate(version).Kind);
        Assert.Contains("submittedDate", Uri.UnescapeDataString(search.Query));
    }

    [Theory]
    [InlineData("http://arxiv.org/pdf/2301.01234v2")] // not HTTPS
    [InlineData("https://arxiv.org:8443/pdf/2301.01234v2")] // non-default port
    [InlineData("https://user@arxiv.org/pdf/2301.01234v2")] // user info
    [InlineData("https://arxiv.org/pdf/2301.01234v2#page=2")] // fragment
    [InlineData("https://arxiv.org/pdf/2301.01234")] // no version: content would not be pinned, may redirect
    [InlineData("https://arxiv.org/pdf/2301.01234v2?download=1")] // query on a PDF
    [InlineData("https://arxiv.org/list/q-fin/new")] // path outside the allowlist
    [InlineData("https://arxiv.org/pdf/2301.01234v2/../../list/q-fin")] // traversal, normalised by Uri to a path outside the allowlist
    [InlineData("https://arxiv.org/pdf/2301.01234v2%2F..%2Fx")] // encoded characters
    [InlineData("https://www.arxiv.org/pdf/2301.01234v2")] // another host
    [InlineData("https://api.semanticscholar.org/graph/v1/paper/search?query=momentum")] // deferred (U-M7-3)
    [InlineData("https://export.arxiv.org/oai2?verb=ListRecords")] // OAI-PMH is out of scope
    [InlineData("https://developers.binance.com/en/support")] // not under /docs/
    [InlineData("https://data.binance.vision/data/spot/monthly/klines/BTCUSDT/1h/x.zip")] // market data host
    [InlineData("https://testnet.binance.vision/api/v3/ping")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/models")]
    public void Anything_outside_the_allowlist_is_rejected(string url)
    {
        Assert.Throws<SourcePolicyViolationException>(() => Policy.Validate(new Uri(url)));
    }

    [Fact]
    public void Api_queries_without_the_cutoff_filter_or_with_other_parameters_are_rejected()
    {
        foreach (var query in new[]
                 {
                     "search_query=" + Uri.EscapeDataString("cat:q-fin.TR"), // no submission filter
                     "search_query=" + Filter + "&max_results=51", // over the cap
                     "search_query=" + Filter + "&start=1001",
                     "search_query=" + Filter + "&id_list=2301.01234", // unversioned id
                     "search_query=" + Filter + "&format=json", // undocumented parameter
                     "search_query=" + Filter + "&search_query=" + Filter, // duplicate
                     "search_query=" + Filter + "&sortBy=random",
                     "search_query=" + Uri.EscapeDataString("cat:q-fin.TR; DROP " + SourceRequestPolicy.SubmittedBeforeCutoff), // characters outside the builder's grammar
                     "id_list=2301.01234v1", // no search_query at all
                 })
        {
            Assert.Throws<SourcePolicyViolationException>(() => Policy.Validate(new Uri(Api(query))));
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.SeeOther)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    [InlineData(HttpStatusCode.NotModified)]
    public async Task Every_redirect_is_rejected_even_to_the_same_host(HttpStatusCode status)
    {
        using var rig = new Rig();
        const string url = "https://arxiv.org/pdf/2301.01234v2";
        rig.Web.Status(url, status, location: "https://arxiv.org/pdf/2301.01234v3");

        await Assert.ThrowsAsync<SourcePolicyViolationException>(() => rig.Http.GetAsync(new Uri(url), ContentClass.Pdf, default));
        Assert.Single(rig.Web.Requests); // the redirect target was never requested
    }

    [Fact]
    public void The_network_handler_never_follows_redirects_and_holds_one_connection_per_host()
    {
        using var handler = (SocketsHttpHandler)SourceGuardHandler.CreateNetworkHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.Equal(1, handler.MaxConnectionsPerServer);
    }

    [Fact]
    public async Task A_rejected_request_never_reaches_the_network_and_only_get_is_allowed()
    {
        using var rig = new Rig();
        using var client = new HttpClient(rig.Guard, disposeHandler: false);

        await Assert.ThrowsAsync<SourcePolicyViolationException>(() => client.GetAsync("https://evil.example/pdf/2301.01234v2"));
        await Assert.ThrowsAsync<SourcePolicyViolationException>(() => client.PostAsync("https://arxiv.org/pdf/2301.01234v2", new StringContent("x")));
        Assert.Empty(rig.Web.Requests);
        Assert.Equal(0, rig.Guard.Sent);
    }

    [Fact]
    public async Task Requests_carry_the_configured_user_agent()
    {
        using var rig = new Rig();
        rig.Web.Serve("https://arxiv.org/pdf/2301.01234v2", "%PDF", "application/pdf");

        await rig.Http.GetAsync(new Uri("https://arxiv.org/pdf/2301.01234v2"), ContentClass.Pdf, default);

        Assert.Equal(rig.Configuration.UserAgent, rig.Web.Requests.Single().UserAgent);
    }

    [Fact]
    public async Task Requests_to_one_host_start_at_least_the_configured_interval_apart()
    {
        using var rig = new Rig();
        for (var i = 1; i <= 3; i++)
        {
            rig.Web.Serve($"https://arxiv.org/pdf/2301.0123{i}v1", "%PDF", "application/pdf");
        }

        for (var i = 1; i <= 3; i++)
        {
            await rig.Http.GetAsync(new Uri($"https://arxiv.org/pdf/2301.0123{i}v1"), ContentClass.Pdf, default);
        }

        var starts = rig.Web.Requests.Select(r => r.At).ToList();
        Assert.All(starts.Zip(starts.Skip(1)), pair => Assert.True(pair.Second - pair.First >= TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void The_interval_cannot_be_configured_below_the_arxiv_terms()
    {
        var json = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "src", "Modules", "SourceGateway", "Config", "sources.v1.json"))
            .Replace("\"minIntervalSeconds\": 3", "\"minIntervalSeconds\": 1", StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => SourceConfiguration.Parse(json));
    }

    [Fact]
    public void Only_the_arxiv_api_may_follow_its_terms_instead_of_robots()
    {
        var json = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "src", "Modules", "SourceGateway", "Config", "sources.v1.json"))
            .Replace("\"kind\": \"ArxivPdf\", \"host\": \"arxiv.org\", \"tier\": \"T1\", \"robots\": \"Binding\"",
                "\"kind\": \"ArxivPdf\", \"host\": \"arxiv.org\", \"tier\": \"T1\", \"robots\": \"ApiTerms\"", StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => SourceConfiguration.Parse(json));
    }

    [Fact]
    public async Task Rate_limited_responses_honour_retry_after_and_stop_after_two_retries()
    {
        using var rig = new Rig();
        const string url = "https://arxiv.org/pdf/2301.01234v2";
        rig.Web.Status(url, HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(20))
            .Status(url, HttpStatusCode.ServiceUnavailable)
            .Status(url, HttpStatusCode.TooManyRequests)
            .Status(url, HttpStatusCode.TooManyRequests);

        var result = await rig.Http.GetAsync(new Uri(url), ContentClass.Pdf, default);

        Assert.Equal(429, result.Status);
        Assert.Equal(3, rig.Web.Requests.Count); // one call and two retries
        Assert.True(rig.Web.Requests[1].At - rig.Web.Requests[0].At >= TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Forbidden_is_never_retried_and_blocks_the_host_for_the_rest_of_the_run()
    {
        using var rig = new Rig();
        rig.Web.Status("https://arxiv.org/pdf/2301.01234v2", HttpStatusCode.Forbidden);

        var first = await rig.Http.GetAsync(new Uri("https://arxiv.org/pdf/2301.01234v2"), ContentClass.Pdf, default);

        Assert.Equal(403, first.Status);
        Assert.True(rig.Guard.IsBlocked("arxiv.org"));
        await Assert.ThrowsAsync<SourcePolicyViolationException>(() => rig.Http.GetAsync(new Uri("https://arxiv.org/pdf/2301.05555v1"), ContentClass.Pdf, default));
        Assert.Single(rig.Web.Requests);
    }

    [Fact]
    public async Task Oversized_bodies_are_refused_from_the_header_and_from_the_stream()
    {
        var json = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "src", "Modules", "SourceGateway", "Config", "sources.v1.json"))
            .Replace("\"Pdf\": 20971520", "\"Pdf\": 100", StringComparison.Ordinal);
        using var rig = new Rig(SourceConfiguration.Parse(json));
        rig.Web.Serve("https://arxiv.org/pdf/2301.01234v2", new byte[101], "application/pdf");

        await Assert.ThrowsAsync<SourceTooLargeException>(() => rig.Http.GetAsync(new Uri("https://arxiv.org/pdf/2301.01234v2"), ContentClass.Pdf, default));
    }

    [Fact]
    public void Robots_rules_use_the_longest_match_and_our_agent_group()
    {
        var rules = RobotsRules.Parse("""
            User-agent: *
            Disallow: /pdf/
            Allow: /pdf/public/

            User-agent: OtherBot
            Disallow: /
            """, "PortfolioIntelligence-SourceGateway");

        Assert.False(rules.IsAllowed("/pdf/2301.01234v2"));
        Assert.True(rules.IsAllowed("/pdf/public/x"));
        Assert.True(rules.IsAllowed("/abs/2301.01234v2"));
        Assert.False(RobotsRules.Parse("User-agent: *\nDisallow: /", "x").IsAllowed("/anything"));
        Assert.True(RobotsRules.Parse("User-agent: *\nDisallow:", "x").IsAllowed("/anything"));
        Assert.False(RobotsRules.Parse("User-agent: portfolio\nDisallow: /pdf\nUser-agent: *\nDisallow:", "PortfolioIntelligence").IsAllowed("/pdf/1v1"));
        Assert.False(RobotsRules.Parse("User-agent: *\nDisallow: /*.pdf$", "x").IsAllowed("/a/b.pdf"));
        Assert.True(RobotsRules.Parse("User-agent: *\nDisallow: /*.pdf$", "x").IsAllowed("/a/b.pdf?x"));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, true)]
    [InlineData(HttpStatusCode.Gone, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    public async Task Robots_404_means_no_restriction_and_any_other_failure_disallows_the_host(HttpStatusCode status, bool allowed)
    {
        using var rig = new Rig();
        rig.Web.Status(Feeds.Robots, status).Status(Feeds.Robots, status).Status(Feeds.Robots, status);
        var uri = new Uri("https://arxiv.org/pdf/2301.01234v2");

        Assert.Equal(allowed, await rig.Robots.IsAllowedAsync(uri, rig.Policy.Validate(uri), default));
    }

    [Fact]
    public async Task Robots_is_read_once_per_host_within_its_lifetime_and_the_api_is_exempt()
    {
        using var rig = new Rig();
        rig.Web.Serve(Feeds.Robots, "User-agent: *\nDisallow: /pdf/", "text/plain");
        var pdf = new Uri("https://arxiv.org/pdf/2301.01234v2");
        var api = ArxivQuery.Version("2301.01234v2", rig.Configuration);

        Assert.False(await rig.Robots.IsAllowedAsync(pdf, rig.Policy.Validate(pdf), default));
        Assert.True(await rig.Robots.IsAllowedAsync(new Uri("https://arxiv.org/abs/2301.01234v2"), rig.Policy.Validate(new Uri("https://arxiv.org/abs/2301.01234v2")), default));
        Assert.True(await rig.Robots.IsAllowedAsync(api, rig.Policy.Validate(api), default)); // export.arxiv.org disallows everything (S-X5); the API follows its terms
        Assert.Single(rig.Web.Requests); // robots.txt of arxiv.org, read once; export.arxiv.org's is never consulted
    }
}
