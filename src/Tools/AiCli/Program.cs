using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using PortfolioIntelligence.Ai;
using PortfolioIntelligence.Ai.Gemini;
using PortfolioIntelligence.Ai.Persistence;

// M4 AI command line (ADR-0010, ADR-0017 §5). Local database only, except `spike`.
//   killswitch status                     — current AI kill switch state.
//   killswitch trip --reason "<text>"     — manual trip (audited).
//   killswitch clear --reason "<text>"    — manual clear: interactive terminal only, actor user:<name> (audited).
//   ledger [--month yyyy-MM]              — spend summary at list prices.
//   reconcile --month yyyy-MM [--invoice-try <amount> --usd-try <rate>] — monthly reconciliation report.
//   spike --allow-network [--model <id>]  — Gate C only: synthetic technical spike against Gemini after confirmation.
//   hg-pilot --allow-network [--design <path>] [--model <id>] — HypothesisGeneration pilot on synthetic items (no qualification).
//   qualify-register --protocol <path>    — registers a committed, unchanged qualification protocol (no network).
//   qualify --allow-network --protocol <path> — the qualification run through the harness (registered protocol only).
//                                           Model: --model, else Gemini:Model (Gemini__Model), else the configured default;
//                                           it must have a list price in ai.v1.json (no call without a price).
// The API key is never printed; output contains metrics only.
const string ConfirmationWord = "EVET";

var command = args.FirstOrDefault() ?? "";
var sub = args.Skip(1).FirstOrDefault() ?? "";
var configuration = AiConfiguration.LoadDefault();
var time = TimeProvider.System;

switch (command)
{
    case "killswitch":
    {
        var killSwitch = new AiKillSwitch(new NpgsqlKillSwitchStore(RequireConnectionString()), new NpgsqlAiAudit(RequireConnectionString()), time);
        switch (sub)
        {
            case "status":
                var state = await killSwitch.StateAsync(CancellationToken.None);
                Console.WriteLine(state is null
                    ? "AI kill switch: off (never tripped)."
                    : $"AI kill switch: {(state.Tripped ? "ON" : "off")} since {state.At:O} ({state.Trigger}, {state.Actor}): {state.Reason}");
                return 0;
            case "trip":
                await killSwitch.TripAsync(KillSwitchTrigger.Manual, "user:" + Environment.UserName, RequireOption("--reason"), CancellationToken.None);
                Console.WriteLine("AI kill switch: ON (manual).");
                return 0;
            case "clear":
                if (Console.IsInputRedirected || Console.IsOutputRedirected)
                {
                    Console.Error.WriteLine("Clearing the AI kill switch requires an interactive terminal; refused.");
                    return 1;
                }

                var reason = RequireOption("--reason");
                Console.Write($"Clear the AI kill switch as user:{Environment.UserName}? Type {ConfirmationWord} to continue: ");
                if (!string.Equals(Console.ReadLine()?.Trim(), ConfirmationWord, StringComparison.Ordinal))
                {
                    Console.Error.WriteLine("Not confirmed; the switch is unchanged.");
                    return 1;
                }

                await killSwitch.ClearAsync("user:" + Environment.UserName, reason, CancellationToken.None);
                Console.WriteLine("AI kill switch: off (manual clear, audited).");
                return 0;
            default:
                return Usage();
        }
    }

    case "ledger":
    {
        var (year, month) = Month(Option("--month"));
        var ledger = new NpgsqlCostLedger(RequireConnectionString());
        var start = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero);
        var report = ReconciliationReport.Build(await ledger.EntriesAsync(start, start.AddMonths(1), CancellationToken.None), year, month, configuration.Prices.Version, null);
        Console.WriteLine(report.ToMarkdown());
        Console.WriteLine($"Monthly cap: {configuration.Spend.MonthlyCap}.");
        return 0;
    }

    case "reconcile":
    {
        var (year, month) = Month(RequireOption("--month"));
        InvoiceInput? invoice = Option("--invoice-try") is { } amount
            ? new InvoiceInput(decimal.Parse(amount, CultureInfo.InvariantCulture), decimal.Parse(RequireOption("--usd-try"), CultureInfo.InvariantCulture))
            : null;
        var ledger = new NpgsqlCostLedger(RequireConnectionString());
        var start = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero);
        var report = ReconciliationReport.Build(await ledger.EntriesAsync(start, start.AddMonths(1), CancellationToken.None), year, month, configuration.Prices.Version, invoice);
        var markdown = report.ToMarkdown();
        Console.WriteLine(markdown);
        if (report.PriceTableReviewNeeded)
        {
            await new NpgsqlAiAudit(RequireConnectionString()).RecordAsync("user:" + Environment.UserName, "ai-reconciliation-difference",
                new { year, month, ledger = report.LedgerTotal.Value, invoiceUsd = report.InvoiceUsd?.Value, difference = report.Difference }, CancellationToken.None);
        }

        var path = Path.Combine(ReportsDirectory(), $"ai-reconciliation-{year:0000}-{month:00}.md");
        await File.WriteAllTextAsync(path, markdown, new UTF8Encoding(false));
        Console.WriteLine($"Report: {path}");
        return report.PriceTableReviewNeeded ? 3 : 0;
    }

    case "spike":
    {
        if (Prepare("Gate C spike: 5 synthetic scenarios", configuration.Spend.JobCap(JobKind.Spike)) is not { } call)
        {
            return 1;
        }

        var connection = RequireConnectionString();
        var killSwitch = new AiKillSwitch(new NpgsqlKillSwitchStore(connection), new NpgsqlAiAudit(connection), time);
        using var provider = new GeminiProvider(call.ApiKey, configuration.Prices, GeminiGuardHandler.CreateNetworkHandler());
        var guard = new AiCallGuard(provider, configuration, new NpgsqlCostLedger(connection), killSwitch, time);
        var job = new JobContext($"spike-{time.GetUtcNow():yyyyMMdd'T'HHmmss'Z'}", JobKind.Spike);
        var results = await new SpikeRunner(guard, call.Model).RunAsync(job, CancellationToken.None);
        foreach (var r in results)
        {
            Console.WriteLine($"{r.Scenario}: {(r.Passed ? "PASS" : "FAIL")} ({r.Status}; model {r.ActualModel}; finish {string.Join("/", r.Finishes)}; " +
                $"calls {r.Calls}; tokens {r.InputTokens}/{r.OutputTokens}; est {r.Estimated} actual {r.Actual}; {r.LatencyMs} ms) — {r.Observation}");
        }

        var path = Path.Combine(ReportsDirectory(), $"{job.JobId}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { job = job.JobId, model = call.Model, results }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        Console.WriteLine($"Details: {path}");
        return 0;
    }

    case "hg-pilot":
    {
        var design = Option("--design") ?? "docs/m4-hg-qualification-design.md";
        if (Prepare($"HypothesisGeneration pilot: {HgCommands.PilotItems} synthetic items (design {design}), budget {HgCommands.PilotBudgetUsd} USD",
                configuration.Spend.JobCap(JobKind.Qualification)) is not { } call)
        {
            return 1;
        }

        return await HgCommands.PilotAsync(configuration, RequireConnectionString(), call.ApiKey, call.Model, design, ReportsDirectory(), time);
    }

    case "qualify-register":
        return await HgCommands.RegisterAsync(RequireConnectionString(), RequireOption("--protocol"));

    case "qualify":
    {
        var protocolPath = RequireOption("--protocol");
        if (Prepare($"HypothesisGeneration qualification run (protocol {protocolPath})", configuration.Spend.JobCap(JobKind.Qualification), modelFromProtocol: true) is not { } call)
        {
            return 1;
        }

        return await HgCommands.QualifyAsync(configuration, RequireConnectionString(), call.ApiKey, protocolPath, ReportsDirectory(), time);
    }

    default:
        return Usage();
}

static int Usage()
{
    Console.Error.WriteLine("Usage: killswitch status|trip|clear --reason <text> | ledger [--month yyyy-MM] | reconcile --month yyyy-MM [--invoice-try <x> --usd-try <rate>] | " +
        "spike --allow-network [--model <id>] | hg-pilot --allow-network [--design <path>] [--model <id>] | qualify-register --protocol <path> | qualify --allow-network --protocol <path>");
    return 1;
}

// Real Gemini calls (Gate C): the network flag, an interactive terminal, the user's own key, a priced model and a typed confirmation.
(string ApiKey, string Model)? Prepare(string what, UsdAmount jobCap, bool modelFromProtocol = false)
{
    if (!args.Contains("--allow-network"))
    {
        Console.Error.WriteLine("Real Gemini calls require --allow-network (M4 Gate C).");
        return null;
    }

    if (Console.IsInputRedirected || Console.IsOutputRedirected)
    {
        Console.Error.WriteLine("Real Gemini calls require an interactive terminal; refused.");
        return null;
    }

    var host = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = [],
        EnvironmentName = Environments.Development, // loads dotnet user-secrets of this tool
        ApplicationName = typeof(Program).Assembly.GetName().Name,
    });
    var apiKey = host.Configuration["Gemini:ApiKey"];
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        Console.Error.WriteLine("No Gemini API key: set Gemini__ApiKey in your own session or `dotnet user-secrets set Gemini:ApiKey ...` for this tool.");
        return null;
    }

    var model = modelFromProtocol ? "(from the protocol)" : Option("--model") ?? host.Configuration["Gemini:Model"] ?? configuration.Policy(TaskClass.Summary).Model!.Model;
    if (!modelFromProtocol && !configuration.Prices.Models.Any(p => p.Provider == GeminiProvider.ProviderName && p.Model == model))
    {
        Console.Error.WriteLine($"No verified list price for '{model}' in {configuration.Prices.Version}; add it from sources.md first. No call was made.");
        return null;
    }

    Console.Write($"{what} against https://{GeminiGuardHandler.AllowedHost} with model {model}; job cap {jobCap}, monthly cap {configuration.Spend.MonthlyCap}. " +
        $"Type {ConfirmationWord} to continue: ");
    if (!string.Equals(Console.ReadLine()?.Trim(), ConfirmationWord, StringComparison.Ordinal))
    {
        Console.Error.WriteLine("Not confirmed; no call was made.");
        return null;
    }

    return (apiKey, model);
}

string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

string RequireOption(string name) => Option(name) is { Length: > 0 } value ? value : throw new ArgumentException($"{name} is required.");

static (int Year, int Month) Month(string? text)
{
    var value = text is null ? DateTimeOffset.UtcNow : DateTimeOffset.ParseExact(text + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    return (value.Year, value.Month);
}

static string RequireConnectionString() => Environment.GetEnvironmentVariable("ConnectionStrings__Ops")
    ?? throw new InvalidOperationException("ConnectionStrings__Ops is not set (scripts/m4-ai.ps1 sets it).");

static string ReportsDirectory()
{
    var root = Environment.GetEnvironmentVariable("PI_ARCHIVE_CACHE")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortfolioIntelligence", "archive-cache");
    return Directory.CreateDirectory(Path.Combine(root, "reports")).FullName;
}
