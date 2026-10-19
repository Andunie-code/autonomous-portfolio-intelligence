using PortfolioIntelligence.Research.Host;

if (args is ["job", var job, ..])
{
    var builder = ResearchHost.CreateBuilder(args[2..]);
    return ResearchHost.RunJob(job, builder.Configuration, Console.Out);
}

var host = ResearchHost.CreateBuilder(args).Build();
await host.RunAsync();
return 0;
