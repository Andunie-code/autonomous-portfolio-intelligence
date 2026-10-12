using Microsoft.Extensions.Hosting;
using PortfolioIntelligence.Infrastructure.Logging;

namespace PortfolioIntelligence.Trading.Host;

public static class TradingHost
{
    // M0: wiring only. Market data, risk and execution services are registered in M5+.
    public static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
        builder.Logging.AddPortfolioLogging(builder.Configuration);
        return builder;
    }
}
