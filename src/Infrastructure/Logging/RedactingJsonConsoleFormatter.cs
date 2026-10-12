using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace PortfolioIntelligence.Infrastructure.Logging;

/// <summary>
/// Writes one JSON object per log entry. Every free-text field (message, exception, scope values)
/// passes through <see cref="SecretRedactor"/> before serialization, so the JSON stays valid.
/// </summary>
public sealed class RedactingJsonConsoleFormatter : ConsoleFormatter
{
    public const string FormatterName = "redacting-json";

    private readonly SecretRedactor _redactor;

    public RedactingJsonConsoleFormatter(SecretRedactor redactor)
        : base(FormatterName)
    {
        _redactor = redactor;
    }

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (message is null && logEntry.Exception is null)
        {
            return;
        }

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("timestamp", DateTimeOffset.UtcNow);
            json.WriteString("level", logEntry.LogLevel.ToString());
            json.WriteString("category", logEntry.Category);
            if (logEntry.EventId.Id != 0)
            {
                json.WriteNumber("eventId", logEntry.EventId.Id);
            }

            json.WriteString("message", _redactor.Redact(message));
            if (logEntry.Exception is not null)
            {
                json.WriteString("exception", _redactor.Redact(logEntry.Exception.ToString()));
            }

            WriteScopes(json, scopeProvider);
            json.WriteEndObject();
        }

        textWriter.WriteLine(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
    }

    private void WriteScopes(Utf8JsonWriter json, IExternalScopeProvider? scopeProvider)
    {
        if (scopeProvider is null)
        {
            return;
        }

        var scopes = new List<string>();
        scopeProvider.ForEachScope((scope, list) =>
        {
            if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var (key, value) in pairs)
                {
                    list.Add($"{key}={value}");
                }
            }
            else if (scope is not null)
            {
                list.Add(scope.ToString() ?? string.Empty);
            }
        }, scopes);

        if (scopes.Count == 0)
        {
            return;
        }

        json.WriteStartArray("scopes");
        foreach (var scope in scopes)
        {
            json.WriteStringValue(_redactor.Redact(scope));
        }

        json.WriteEndArray();
    }
}
