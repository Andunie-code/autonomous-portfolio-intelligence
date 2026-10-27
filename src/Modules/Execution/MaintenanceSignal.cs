using System.Threading.Channels;

namespace PortfolioIntelligence.Execution;

/// <summary>
/// Wakes the trading worker for an immediate exchange maintenance pass (m6-protocol §5.5, A-5): engaging the kill
/// switch cancels open entry orders on the exchange without waiting for the hourly cycle. Requests coalesce.
/// </summary>
public sealed class MaintenanceSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public int Requests { get; private set; }

    public void Request()
    {
        Requests++;
        _channel.Writer.TryWrite(true);
    }

    public async Task WaitAsync(CancellationToken ct) => await _channel.Reader.ReadAsync(ct);
}
