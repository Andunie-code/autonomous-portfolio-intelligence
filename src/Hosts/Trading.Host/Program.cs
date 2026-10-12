using PortfolioIntelligence.Trading.Host;

var host = TradingHost.CreateBuilder(args).Build();
await host.RunAsync();
