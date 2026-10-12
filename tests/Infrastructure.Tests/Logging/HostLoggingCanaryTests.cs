using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PortfolioIntelligence.Infrastructure.Logging;
using PortfolioIntelligence.Research.Host;
using PortfolioIntelligence.Trading.Host;

namespace PortfolioIntelligence.Infrastructure.Tests.Logging;

/// <summary>
/// End-to-end canary (roadmap M0): a secret supplied through configuration and deliberately
/// written to the log by each real host must appear masked in the console output.
/// Redirects Console.Out, so it must not run in parallel with other console-writing tests.
/// </summary>
[Collection(ConsoleCollection.Name)]
public class HostLoggingCanaryTests
{
    public static TheoryData<string> Hosts => new() { "trading", "research" };

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task Canary_secret_is_masked_in_real_host_console_output(string hostName)
    {
        var canary = "CANARY-" + Guid.NewGuid().ToString("N");
        string[] args = [$"--Gemini:ApiKey={canary}"];

        var originalOut = Console.Out;
        using var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            var builder = hostName == "trading" ? TradingHost.CreateBuilder(args) : ResearchHost.CreateBuilder(args);
            using (var host = builder.Build())
            {
                var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Canary");
                logger.LogWarning("Deliberately logging the configured key: {Key}", canary);
            } // disposing the host flushes the console logger queue
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = captured.ToString();
        Assert.Contains("Deliberately logging the configured key", output);
        Assert.DoesNotContain(canary, output);
        Assert.Contains(SecretRedactor.Mask, output);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleCollection
{
    public const string Name = "Console";
}
