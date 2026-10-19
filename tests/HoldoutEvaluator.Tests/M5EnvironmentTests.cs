using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using PortfolioIntelligence.HoldoutEvaluator.Tests.Support;
using PortfolioIntelligence.Kernel.Backtest;

namespace PortfolioIntelligence.HoldoutEvaluator.Tests;

/// <summary>
/// M5 evaluator changes (m5-protocol §6, §10 E1–E5, E8): digest-bound environment flag, release equivalence for the
/// Kohort 0 seed, real role logins, the archive window source (in-memory handler), the H19 self-test, the record
/// export and manifest-hash parity with the Trading-side reader. Requires Docker; no network.
/// </summary>
public class M5EnvironmentTests(EvaluatorDatabase fixture) : IClassFixture<EvaluatorDatabase>
{
    private const string Other = "sha256:00000000000000000000000000000000000000000000000000000000000000ff";
    private static readonly FixedClock After = new(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task The_environment_flag_holds_only_for_its_digest_and_new_rows_must_name_digest_and_report()
    {
        var db = await fixture.NewHoldoutAsync();
        await EvaluatorDatabase.ExecuteAsync(db.ConnectionString, $"INSERT INTO results.evaluator_releases (image_digest, engine_sha) VALUES ('{Other}', 'other')");
        await EvaluatorDatabase.ExecuteAsync(db.ConnectionString, $"INSERT INTO results.environment_flags (verified, actor, image_digest, report_hash) VALUES (true, 'user', '{Other}', repeat('e', 64))");
        var unbound = await Assert.ThrowsAsync<PostgresException>(() => EvaluatorDatabase.ExecuteAsync(db.ConnectionString, "INSERT INTO results.environment_flags (verified, actor) VALUES (true, 'user')"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, unbound.SqlState);

        var id = await Seeding.SeedAsync(db, Manifests.Build([Manifests.Trend]));
        await new ProspectiveService(db, new SyntheticWindowSource(3, drift: 0.003), After, null).RunDueAsync(default);

        Assert.True(await EvaluatorDatabase.ScalarAsync<bool>(db.ConnectionString, $"SELECT results.environment_verified('{Other}')"));
        Assert.False(await EvaluatorDatabase.ScalarAsync<bool>(db.ConnectionString, $"SELECT results.environment_verified('{EvaluatorDatabase.Digest}')"));
        Assert.False(await EvaluatorDatabase.ScalarAsync<bool>(db.ConnectionString, "SELECT results.environment_verified()"));
        Assert.Equal("Invalid", await EvaluatorDatabase.ScalarAsync<string>(db.ConnectionString, $"SELECT status FROM results.validation_status WHERE cohort_id = {id}"));
        Assert.Equal(1L, await EvaluatorDatabase.ScalarAsync<long>(db.ConnectionString, "SELECT count(*) FROM results.audit_log WHERE event = 'environment-flag'"));
    }

    [Fact]
    public async Task A_cohort_is_evaluated_by_another_release_only_through_an_audited_equivalence()
    {
        var db = await fixture.NewHoldoutAsync();
        await EvaluatorDatabase.ExecuteAsync(db.ConnectionString, $"INSERT INTO results.evaluator_releases (image_digest, engine_sha) VALUES ('{Other}', 'other')");
        await Seeding.SeedAsync(db, Manifests.Build([Manifests.Family(1)]));
        var noLink = await Assert.ThrowsAsync<RegistryRejectedException>(() => new ProspectiveService(db, new SyntheticWindowSource(3), After, null, Other).RunDueAsync(default));
        Assert.Contains("equivalent release", noLink.Message);

        var manifest = Manifests.Build([Manifests.Trend]);
        await Assert.ThrowsAsync<RegistryRejectedException>(() => CohortSeeder.SeedAsync(db, manifest, Seeding.PastRegistration, null, default, Other, null));
        await CohortSeeder.SeedAsync(db, manifest, Seeding.PastRegistration, null, default, Other, "{\"m2Hashes\":\"reproduced\"}");
        await Assert.ThrowsAsync<PostgresException>(() => CohortSeeder.SeedAsync(db, manifest, Seeding.PastRegistration, null, default, Other, "{\"again\":true}"));

        Assert.Equal(1L, await EvaluatorDatabase.ScalarAsync<long>(db.ConnectionString, "SELECT count(*) FROM results.release_equivalences"));
        Assert.Equal(1L, await EvaluatorDatabase.ScalarAsync<long>(db.ConnectionString, "SELECT count(*) FROM results.audit_log WHERE event = 'release-equivalence'"));
        var change = await Assert.ThrowsAsync<PostgresException>(() => EvaluatorDatabase.ExecuteAsync(db.ConnectionString, "DELETE FROM results.release_equivalences"));
        Assert.Contains("append-only", change.MessageText);
    }

    [Fact]
    public async Task The_equivalent_release_runs_the_initial_evaluation_with_its_own_digest()
    {
        var db = await fixture.NewHoldoutAsync();
        await EvaluatorDatabase.ExecuteAsync(db.ConnectionString, $"INSERT INTO results.evaluator_releases (image_digest, engine_sha) VALUES ('{Other}', 'other')");
        var seeded = await CohortSeeder.SeedAsync(db, Manifests.Build([Manifests.Trend]), Seeding.PastRegistration, null, default, Other, "{\"m2Hashes\":\"reproduced\"}");

        Assert.Equal([seeded.CohortId], await new ProspectiveService(db, new SyntheticWindowSource(3), After, null, Other).RunDueAsync(default));
        Assert.Equal(Other, await EvaluatorDatabase.ScalarAsync<string>(db.ConnectionString, "SELECT image_digest FROM results.prospective_runs"));
    }

    [Fact]
    public async Task Real_role_logins_keep_the_role_separation()
    {
        var db = await fixture.NewNamedHoldoutAsync("holdout_logins", logins: true);
        var registration = await new RegistryService(db, null).RegisterAsync(Manifests.Build([Manifests.Trend]), default);
        Assert.True(registration.CohortId > 0);
        await using var registry = await db.OpenAsync("eval_registry", default);
        Assert.Equal("eval_registry", (string)(await new NpgsqlCommand("SELECT session_user", registry).ExecuteScalarAsync())!);
        var denied = await Assert.ThrowsAsync<PostgresException>(async () => await new NpgsqlCommand("SELECT * FROM results.prospective_results", registry).ExecuteScalarAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
    }

    [Fact]
    public async Task The_self_test_passes_on_holdout_henv_and_refuses_any_other_database()
    {
        var other = await fixture.NewHoldoutAsync();
        var refused = new StringWriter();
        Assert.Equal(1, await SelfTest.RunAsync(other, refused, default));
        Assert.Contains("wrong-database", refused.ToString());

        var henv = await fixture.NewNamedHoldoutAsync("holdout_henv");
        var output = new StringWriter();
        var exit = await SelfTest.RunAsync(henv, output, default);

        Assert.True(exit == 0, output.ToString());
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(12, lines.Length); // 11 checks and the H19 summary
        Assert.All(lines, l => Assert.Contains(" PASS ", l));
        Assert.Equal(0L, await EvaluatorDatabase.ScalarAsync<long>(fixture.OpsConnectionString, "SELECT count(*) FROM research.cohort_status"));
    }

    [Fact]
    public async Task Record_export_contains_the_evaluation_records_and_no_window_data()
    {
        var db = await fixture.NewHoldoutAsync();
        await Seeding.SeedAsync(db, Manifests.Build([Manifests.Trend]));
        await new ProspectiveService(db, new SyntheticWindowSource(3), After, null).RunDueAsync(default);
        var path = Path.Combine(Path.GetTempPath(), $"pi-records-{Guid.NewGuid():N}.json");
        try
        {
            await RecordExport.WriteAsync(db, path, default);
            var json = await File.ReadAllTextAsync(path);
            Assert.Contains("results.prospective_results", json);
            Assert.DoesNotContain("candles", json);
            Assert.Equal(1L, await EvaluatorDatabase.ScalarAsync<long>(db.ConnectionString, "SELECT count(*) FROM results.audit_log WHERE event = 'records-exported'"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Trading_and_the_evaluator_compute_the_same_manifest_hash()
    {
        var json = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "cohort-0.json"));
        Assert.Equal(ManifestParser.Parse(json).Hash, CohortManifestReader.Read(json).Hash);
    }

    private sealed class StubArchive(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request.RequestUri!));
        }
    }

    private static byte[] Zip(string name, IEnumerable<string> lines)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry(name.Replace(".zip", ".csv")).Open());
            foreach (var line in lines)
            {
                writer.WriteLine(line);
            }
        }

        return stream.ToArray();
    }

    private static IEnumerable<string> Day(DateTimeOffset day) => Enumerable.Range(0, 6).Select(i =>
    {
        var open = day.AddHours(4 * i);
        var micro = (open - DateTimeOffset.UnixEpoch).Ticks / 10;
        return string.Create(CultureInfo.InvariantCulture, $"{micro},100,101,99,100.5,10,{micro + 4L * 3_600_000_000 - 1},1000,5,4,400,0");
    });

    [Fact]
    public void Archive_source_verifies_checksums_cuts_the_window_and_refuses_open_windows()
    {
        var start = new DateTimeOffset(2026, 2, 27, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 4, 2, 0, 0, 0, TimeSpan.Zero);
        var files = ArchiveWindowSource.Files("BTCUSDT", start, end).Select(f => f.Path).ToList();
        Assert.Equal(["/data/spot/daily/klines/BTCUSDT/4h/BTCUSDT-4h-2026-02-27.zip", "/data/spot/daily/klines/BTCUSDT/4h/BTCUSDT-4h-2026-02-28.zip",
            "/data/spot/monthly/klines/BTCUSDT/4h/BTCUSDT-4h-2026-03.zip", "/data/spot/daily/klines/BTCUSDT/4h/BTCUSDT-4h-2026-04-01.zip"], files);

        var corrupt = false;
        var stub = new StubArchive(uri =>
        {
            var name = uri.AbsolutePath[(uri.AbsolutePath.LastIndexOf('/') + 1)..];
            var zipName = name.Replace(".CHECKSUM", "");
            var first = zipName.Contains("-4h-2026-03.zip") ? new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)
                : DateTimeOffset.ParseExact(zipName[^14..^4], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            var days = zipName.Contains("-4h-2026-03.zip") ? 31 : 1;
            var zip = Zip(zipName, Enumerable.Range(0, days).SelectMany(d => Day(first.AddDays(d))));
            if (name.EndsWith(".CHECKSUM", StringComparison.Ordinal))
            {
                var hash = Convert.ToHexString(SHA256.HashData(corrupt ? [1, 2, 3] : zip)).ToLowerInvariant();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{hash}  {zipName}") };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
        });
        var source = new ArchiveWindowSource(new HttpClient(new ArchiveWindowGuard(stub)), new FixedClock(end.AddDays(10)));

        var candles = source.Load(["BTCUSDT"], start.AddHours(8), end)["BTCUSDT"];
        Assert.Equal(start.AddHours(8), candles[0].OpenTime);
        Assert.Equal(end.AddHours(-4), candles[^1].OpenTime);
        Assert.Equal((int)((end - start.AddHours(8)).TotalHours / 4), candles.Count);
        Assert.Equal(8, stub.Requests.Count);

        corrupt = true;
        Assert.Throws<ArchiveWindowException>(() => source.Load(["BTCUSDT"], start, end));
        var requests = stub.Requests.Count;
        Assert.Throws<EarlyEvaluationException>(() => new ArchiveWindowSource(new HttpClient(new ArchiveWindowGuard(stub)), new FixedClock(end.AddDays(2))).Load(["BTCUSDT"], start, end));
        Assert.Equal(requests, stub.Requests.Count);

        foreach (var bad in new[]
                 {
                     "https://api.binance.com/data/spot/daily/klines/BTCUSDT/4h/BTCUSDT-4h-2026-02-27.zip",
                     "https://data.binance.vision/data/futures/um/daily/klines/BTCUSDT/4h/BTCUSDT-4h-2026-02-27.zip",
                     "https://data.binance.vision/data/spot/daily/klines/BTCUSDT/1m/BTCUSDT-1m-2026-02-27.zip",
                     "https://data.binance.vision/?prefix=data/spot/",
                 })
        {
            Assert.Throws<ArchiveWindowException>(() => ArchiveWindowGuard.Validate(new Uri(bad)));
        }

        var redirect = new ArchiveWindowSource(new HttpClient(new ArchiveWindowGuard(new StubArchive(_ => new HttpResponseMessage(HttpStatusCode.Found)))), new FixedClock(end.AddDays(10)));
        Assert.Throws<ArchiveWindowException>(() => redirect.Load(["BTCUSDT"], start, end));
        Assert.Throws<ArchiveWindowException>(() => ArchiveWindowSource.Parse("BTCUSDT", ["1767225600000,100,101,99,100.5,10,1767239999999,1000,5,4,400,0"], new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)));
    }
}
