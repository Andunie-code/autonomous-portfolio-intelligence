using PortfolioIntelligence.Research.Host;

var host = ResearchHost.CreateBuilder(args).Build();
await host.RunAsync();
