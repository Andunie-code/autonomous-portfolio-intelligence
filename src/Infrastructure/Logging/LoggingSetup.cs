using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace PortfolioIntelligence.Infrastructure.Logging;

public static class LoggingSetup
{
    /// <summary>
    /// The only supported logging configuration: structured JSON to the console, with secrets redacted.
    /// Every host and tool must call this instead of configuring providers itself. Secrets that do not come from
    /// the configuration (Docker secret files) are passed in <paramref name="additionalSecrets"/>.
    /// </summary>
    public static ILoggingBuilder AddPortfolioLogging(this ILoggingBuilder logging, IConfiguration configuration, IEnumerable<string>? additionalSecrets = null)
    {
        var redactor = SecretRedactor.FromConfiguration(configuration, additionalSecrets);

        logging.ClearProviders();
        logging.Services.AddSingleton(redactor);
        logging.AddConsole(options => options.FormatterName = RedactingJsonConsoleFormatter.FormatterName);
        logging.AddConsoleFormatter<RedactingJsonConsoleFormatter, ConsoleFormatterOptions>();
        return logging;
    }
}
