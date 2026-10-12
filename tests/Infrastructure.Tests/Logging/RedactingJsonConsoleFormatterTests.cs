using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PortfolioIntelligence.Infrastructure.Logging;

namespace PortfolioIntelligence.Infrastructure.Tests.Logging;

public class RedactingJsonConsoleFormatterTests
{
    private const string Canary = "CANARY-formatter-0123456789";

    [Fact]
    public void Message_exception_and_scopes_are_redacted_and_output_is_valid_json()
    {
        var formatter = new RedactingJsonConsoleFormatter(new SecretRedactor([Canary]));
        var scopes = new LoggerExternalScopeProvider();
        using var _ = scopes.Push(new Dictionary<string, object?> { ["Token"] = Canary });
        var entry = new LogEntry<string>(
            LogLevel.Error, "Test", new EventId(7), $"message with {Canary}",
            new InvalidOperationException($"exception with {Canary}"), (s, _) => s);
        using var writer = new StringWriter();

        formatter.Write(entry, scopes, writer);

        var line = writer.ToString();
        Assert.DoesNotContain(Canary, line);
        using var json = JsonDocument.Parse(line);
        Assert.Equal("Error", json.RootElement.GetProperty("level").GetString());
        Assert.Contains(SecretRedactor.Mask, json.RootElement.GetProperty("message").GetString());
        Assert.Contains(SecretRedactor.Mask, json.RootElement.GetProperty("exception").GetString());
        Assert.Contains(SecretRedactor.Mask, json.RootElement.GetProperty("scopes")[0].GetString());
    }

    [Fact]
    public void Entries_without_message_or_exception_write_nothing()
    {
        var formatter = new RedactingJsonConsoleFormatter(new SecretRedactor([]));
        var entry = new LogEntry<string?>(LogLevel.Information, "Test", default, null, null, (_, _) => null!);
        using var writer = new StringWriter();

        formatter.Write(entry, scopeProvider: null, writer);

        Assert.Equal(string.Empty, writer.ToString());
    }
}
