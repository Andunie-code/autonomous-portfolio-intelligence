using Microsoft.Extensions.Hosting;
using PortfolioIntelligence.Infrastructure.Logging;

namespace PortfolioIntelligence.Research.Host;

public static class ResearchHost
{
    // M0: wiring only. Research services are registered in M3+.
    public static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
        builder.Logging.AddPortfolioLogging(builder.Configuration);
        return builder;
    }
}
