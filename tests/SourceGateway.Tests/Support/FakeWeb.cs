using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace PortfolioIntelligence.SourceGateway.Tests.Support;

/// <summary>
/// In-memory web (zero network): responses by absolute URL, a default of 404, scripted status sequences, and a record of
/// every request that reached it with its User-Agent header and the fake clock time.
/// </summary>
public sealed class FakeWeb(TimeProvider? time = null) : HttpMessageHandler
{
    private readonly Dictionary<string, Queue<Func<HttpResponseMessage>>> _routes = new(StringComparer.Ordinal);

    public List<(Uri Uri, string? UserAgent, DateTimeOffset At)> Requests { get; } = [];

    public FakeWeb Serve(string url, string body, string contentType, HttpStatusCode status = HttpStatusCode.OK) =>
        Serve(url, Encoding.UTF8.GetBytes(body), contentType, status);

    public FakeWeb Serve(string url, byte[] body, string contentType, HttpStatusCode status = HttpStatusCode.OK, int times = 1000)
    {
        for (var i = 0; i < times; i++)
        {
            Enqueue(url, () =>
            {
                var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
                return response;
            });
        }

        return this;
    }

    public FakeWeb Status(string url, HttpStatusCode status, TimeSpan? retryAfter = null, string? location = null)
    {
        Enqueue(url, () =>
        {
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent([]) };
            if (retryAfter is { } wait)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(wait);
            }

            if (location is not null)
            {
                response.Headers.Location = new Uri(location);
            }

            return response;
        });
        return this;
    }

    public FakeWeb Then(string url, string body, string contentType) => Serve(url, Encoding.UTF8.GetBytes(body), contentType, times: 1);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add((request.RequestUri!, request.Headers.UserAgent.ToString(), (time ?? TimeProvider.System).GetUtcNow()));
        if (_routes.TryGetValue(request.RequestUri!.AbsoluteUri, out var queue) && queue.Count > 0)
        {
            return Task.FromResult(queue.Count > 1 ? queue.Dequeue()() : queue.Peek()());
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) });
    }

    private void Enqueue(string url, Func<HttpResponseMessage> response)
    {
        if (!_routes.TryGetValue(url, out var queue))
        {
            _routes[url] = queue = new Queue<Func<HttpResponseMessage>>();
        }

        queue.Enqueue(response);
    }
}

/// <summary>Gateway wiring over <see cref="FakeWeb"/> with a fake clock that advances by itself while a request waits.</summary>
public sealed class Rig : IDisposable
{
    public Rig(SourceConfiguration? configuration = null)
    {
        Configuration = configuration ?? SourceConfiguration.LoadDefault();
        Policy = new SourceRequestPolicy(Configuration);
        Web = new FakeWeb(Time);
        Limiter = new DomainRateLimiter(TimeSpan.FromSeconds(Configuration.MinIntervalSeconds), Time);
        Guard = new SourceGuardHandler(Policy, Limiter, Web);
        Http = new SourceHttp(Guard, Time, retryBaseDelay: TimeSpan.FromSeconds(1));
        Robots = new RobotsPolicy(Http, Configuration, Time);
        CacheRoot = Directory.CreateTempSubdirectory("pi-source-cache-").FullName;
        Cache = new SourceCache(CacheRoot, TestPaths.RepositoryRoot);
        Fetcher = new SourceFetcher(Policy, Cache, Http, Robots, Time);
        Arxiv = new ArxivClient(Fetcher, Configuration);
        Store = new InMemorySourceStore();
        Ingestor = new DocumentIngestor(Arxiv, Store, Configuration);
    }

    public AutoAdvancingTime Time { get; } = new();

    public SourceConfiguration Configuration { get; }

    public SourceRequestPolicy Policy { get; }

    public FakeWeb Web { get; }

    public DomainRateLimiter Limiter { get; }

    public SourceGuardHandler Guard { get; }

    public SourceHttp Http { get; }

    public RobotsPolicy Robots { get; }

    public string CacheRoot { get; }

    public SourceCache Cache { get; }

    public SourceFetcher Fetcher { get; }

    public ArxivClient Arxiv { get; }

    public InMemorySourceStore Store { get; }

    public DocumentIngestor Ingestor { get; }

    public SourceFetcher Offline => new(Policy, Cache, null, null, Time);

    public void Dispose()
    {
        Guard.Dispose();
        Directory.Delete(CacheRoot, recursive: true);
    }
}

/// <summary>A fake clock whose timers fire at once by advancing the clock to their due time (rate-limit waits take no real time).</summary>
public sealed class AutoAdvancingTime : FakeTimeProvider
{
    public AutoAdvancingTime() : base(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero))
    {
        AutoAdvanceAmount = TimeSpan.Zero;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        if (dueTime > TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
        {
            Advance(dueTime);
        }

        return timer;
    }
}

public static class Feeds
{
    public const string Robots = "https://arxiv.org/robots.txt";

    public static string Entry(string id, int version, string published, string updated, string categories = "q-fin.TR", string title = "A study", string summary =
        "We find that a lookback of 20 days yields an annualized Sharpe ratio of 1.10 after costs.") =>
        $"""
          <entry>
            <id>http://arxiv.org/abs/{id}v{version}</id>
            <updated>{updated}</updated>
            <published>{published}</published>
            <title>{title}</title>
            <summary>{summary}</summary>
            {string.Concat(categories.Split(',').Select(c => $"<category term=\"{c}\" scheme=\"http://arxiv.org/schemas/atom\"/>"))}
          </entry>
        """;

    public static string Feed(params string[] entries) =>
        $"""<?xml version="1.0" encoding="UTF-8"?><feed xmlns="http://www.w3.org/2005/Atom">{string.Concat(entries)}</feed>""";

    public static byte[] Pdf(params (string Text, bool Hidden)[] lines) => PdfWith(lines.Select(l => (l.Text, l.Hidden ? HiddenKind.White : HiddenKind.None)).ToArray());

    public enum HiddenKind
    {
        None,
        White,
        Invisible,
        Tiny,
    }

    public static byte[] PdfWith(params (string Text, HiddenKind Hidden)[] lines)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var y = 780.0;
        foreach (var (text, hidden) in lines)
        {
            switch (hidden)
            {
                case HiddenKind.White:
                    page.SetTextAndFillColor(255, 255, 255);
                    page.AddText(text, 11, new PdfPoint(40, y), font);
                    page.ResetColor();
                    break;
                case HiddenKind.Invisible:
                    page.SetTextRenderingMode(TextRenderingMode.Neither);
                    page.AddText(text, 11, new PdfPoint(40, y), font);
                    page.SetTextRenderingMode(TextRenderingMode.Fill);
                    break;
                case HiddenKind.Tiny:
                    page.AddText(text, 0.5, new PdfPoint(40, y), font);
                    break;
                default:
                    page.AddText(text, 11, new PdfPoint(40, y), font);
                    break;
            }

            y -= 18;
        }

        return builder.Build();
    }
}

internal static class TestPaths
{
    public static string RepositoryRoot { get; } = Find();

    private static string Find()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PortfolioIntelligence.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
