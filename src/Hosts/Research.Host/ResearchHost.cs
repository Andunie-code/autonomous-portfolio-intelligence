using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using PortfolioIntelligence.Infrastructure.Logging;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Research.Evaluation;

namespace PortfolioIntelligence.Research.Host;

public static class ResearchHost
{
    public static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
        builder.Logging.AddPortfolioLogging(builder.Configuration);
        return builder;
    }

    /// <summary>
    /// Job-based runs (ADR-0013 §1, m5-protocol §7.1): the container starts for one job and exits.
    ///   calibrate-quick    synthetic gate calibration (heavy CPU load, no database, no network; the cycle-time test)
    ///   henv-credentials   H25: no status_relay credential is present in this container (names only, no values)
    ///   submit-cohort      registers the manifest Registration:Manifest with the evaluator (ADR-0019 §5)
    /// </summary>
    public static int RunJob(string job, IConfiguration configuration, TextWriter output)
    {
        switch (job)
        {
            case "submit-cohort":
                return CohortSubmission.RunAsync(configuration, output, null, CancellationToken.None).GetAwaiter().GetResult();
            case "calibrate-quick":
            {
                var started = DateTimeOffset.UtcNow;
                var options = new CalibrationOptions(NullStrategies: 40, NullUniverses: 4, TruePositiveUniverses: 4, MaxParallelism: Environment.ProcessorCount);
                new CalibrationRunner(CostModel.V1, RiskSettings.Policy, 1000).Run(options, message => output.WriteLine($"[{DateTimeOffset.UtcNow - started:hh\\:mm\\:ss}] {message}"));
                return 0;
            }
            case "henv-credentials":
            {
                var found = CredentialScan(configuration, Environment.GetEnvironmentVariables().Keys.Cast<string>(), ["/run/secrets", "/app/secrets"]);
                output.WriteLine($"H25 {(found.Count == 0 ? "PASS" : "FAIL")} status_relay-credential-absent {(found.Count == 0 ? "ok" : string.Join("+", found))}");
                return found.Count == 0 ? 0 : 1;
            }
            default:
                output.WriteLine("usage: Research.Host job <calibrate-quick|henv-credentials|submit-cohort>");
                return 2;
        }
    }

    /// <summary>Locations (configuration keys, environment names, secret files) that mention the status_relay identity.</summary>
    public static IReadOnlyList<string> CredentialScan(IConfiguration configuration, IEnumerable<string> environmentNames, IEnumerable<string> secretDirectories)
    {
        static bool Mentions(string text) => text.Contains("status_relay", StringComparison.OrdinalIgnoreCase) || text.Contains("statusrelay", StringComparison.OrdinalIgnoreCase);
        var found = new List<string>();
        found.AddRange(configuration.AsEnumerable().Where(kv => Mentions(kv.Key) || (kv.Value is { } v && Mentions(v))).Select(kv => "config:" + kv.Key));
        found.AddRange(environmentNames.Where(Mentions).Select(n => "env:" + n));
        foreach (var directory in secretDirectories.Where(Directory.Exists))
        {
            found.AddRange(Directory.EnumerateFiles(directory).Where(f => Mentions(Path.GetFileName(f))).Select(f => "file:" + Path.GetFileName(f)));
        }

        return found.Distinct(StringComparer.Ordinal).ToList();
    }
}
