using Microsoft.Extensions.Configuration;
using PortfolioIntelligence.Infrastructure.Logging;

namespace PortfolioIntelligence.Infrastructure.Tests.Logging;

public class SecretRedactorTests
{
    // Fake credential shapes are assembled at runtime so the source file never contains a
    // string that a secret scanner would (correctly) flag.
    private static readonly string FakeGoogleKey = "AIza" + new string('x', 35);
    private static readonly string FakeAnthropicKey = "sk-ant-" + new string('y', 30);
    private static readonly string FakeGitHubToken = "ghp_" + new string('z', 36);

    [Theory]
    [InlineData("Gemini:ApiKey")]
    [InlineData("Exchange:Secret")]
    [InlineData("Ops:Password")]
    [InlineData("Some:Token")]
    public void Values_of_secret_looking_configuration_keys_are_masked(string key)
    {
        var canary = "CANARY-" + Guid.NewGuid().ToString("N");
        var redactor = SecretRedactor.FromConfiguration(Config((key, canary)));

        var output = redactor.Redact($"connecting with {canary} now");

        Assert.DoesNotContain(canary, output);
        Assert.Contains(SecretRedactor.Mask, output);
    }

    [Fact]
    public void Secrets_read_from_files_are_masked_as_literals()
    {
        // Docker secret files (the Testnet key, the Ops API token) never enter the configuration (m6-protocol §8.2).
        var canary = "FILE-SECRET-" + Guid.NewGuid().ToString("N");
        var redactor = SecretRedactor.FromConfiguration(Config(), [canary, "short"]);

        Assert.Equal($"key {SecretRedactor.Mask} used", redactor.Redact($"key {canary} used"));
        Assert.Equal("short", redactor.Redact("short")); // values under six characters are not registered
    }

    [Fact]
    public void Signed_exchange_requests_lose_their_signature_and_key_header()
    {
        var redactor = SecretRedactor.FromConfiguration(Config());
        var signature = new string('a', 32) + new string('7', 32);

        var url = redactor.Redact($"GET https://testnet.binance.vision/api/v3/account?recvWindow=5000&timestamp=1&signature={signature}");
        var header = redactor.Redact("X-MBX-APIKEY: abcDEF0123456789abcDEF0123456789");

        Assert.DoesNotContain(signature, url);
        Assert.Contains("recvWindow=5000", url);
        Assert.DoesNotContain("abcDEF0123456789", header);
    }

    [Fact]
    public void Connection_string_password_is_masked_even_when_logged_alone()
    {
        var password = "Pw-" + Guid.NewGuid().ToString("N");
        var redactor = SecretRedactor.FromConfiguration(Config(("ConnectionStrings:Ops", $"Host=localhost;Username=pi;Password={password}")));

        Assert.DoesNotContain(password, redactor.Redact($"value was {password}"));
        Assert.DoesNotContain(password, redactor.Redact($"Host=x;Password={password};Database=y"));
    }

    [Fact]
    public void Known_credential_shapes_are_masked_without_configuration()
    {
        var redactor = new SecretRedactor([]);

        foreach (var secret in new[] { FakeGoogleKey, FakeAnthropicKey, FakeGitHubToken, "Bearer " + new string('t', 24) })
        {
            Assert.DoesNotContain(secret, redactor.Redact($"header: {secret}"));
        }

        var privateKey = "-----BEGIN " + "PRIVATE KEY-----\nabc\n-----END " + "PRIVATE KEY-----";
        Assert.DoesNotContain("abc", redactor.Redact(privateKey));
    }

    [Fact]
    public void Ordinary_text_and_non_secret_keys_are_untouched()
    {
        var redactor = SecretRedactor.FromConfiguration(Config(("Logging:LogLevel:Default", "Information"), ("Ops:Host", "localhost")));

        const string text = "Information from localhost: applied migration 1";
        Assert.Equal(text, redactor.Redact(text));
    }

    [Fact]
    public void Very_short_secret_values_are_not_registered()
    {
        var redactor = SecretRedactor.FromConfiguration(Config(("Some:Token", "abc")));

        Assert.Equal("abc", redactor.Redact("abc"));
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
}
