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
            using (IHost host = hostName == "trading" ? TradingHost.CreateBuilder(args).Build() : ResearchHost.CreateBuilder(args).Build())
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

/// <summary>The Testnet secret files of Trading.Host (m6-protocol §8.2): masked although they never enter the configuration.</summary>
[Collection(ConsoleCollection.Name)]
public class TestnetSecretCanaryTests
{
    [Fact]
    public async Task Testnet_key_files_are_masked_in_trading_host_output()
    {
        var secrets = Directory.CreateTempSubdirectory("pi-canary-");
        var key = "CANARY-KEY-" + Guid.NewGuid().ToString("N");
        var secret = "CANARY-SECRET-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(Path.Combine(secrets.FullName, TradingHost.TestnetKeyFile), key);
        await File.WriteAllTextAsync(Path.Combine(secrets.FullName, TradingHost.TestnetSecretFile), secret);
        string[] args = ["--Trading:Testnet=enabled", $"--Trading:SecretsDirectory={secrets.FullName}"];

        var originalOut = Console.Out;
        using var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            using (IHost host = TradingHost.CreateBuilder(args).Build())
            {
                host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Canary").LogWarning("Deliberately logging the Testnet key {Key} and secret {Secret}", key, secret);
            }
        }
        finally
        {
            Console.SetOut(originalOut);
            secrets.Delete(recursive: true);
        }

        var output = captured.ToString();
        Assert.Contains("Deliberately logging the Testnet key", output);
        Assert.DoesNotContain(key, output);
        Assert.DoesNotContain(secret, output);
        Assert.Null(TradingHost.TestnetCredentialsFrom(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build())); // off without Trading:Testnet = enabled
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleCollection
{
    public const string Name = "Console";
}
