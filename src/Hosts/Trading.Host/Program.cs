using PortfolioIntelligence.Trading.Host;

// "testnet-drill" is the user's Gate C drill (m6-protocol §8.3); anything else starts the host.
if (args.Length > 0 && args[0] == "testnet-drill")
{
    return await TestnetDrillCommand.RunAsync(args[1..]);
}

await TradingHost.Build(args).RunAsync();
return 0;
