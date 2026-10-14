namespace PortfolioIntelligence.Kernel.MarketData;

/// <summary>An archive file violates an integrity rule (docs/m1-data-policy.md §6). The whole file is rejected.</summary>
public class ArchiveDataException(string message) : Exception(message);

/// <summary>
/// A candle closing after the research cutoff C was encountered (contamination incident, policy §3 item 9).
/// The message carries only metadata, never candle contents.
/// </summary>
public sealed class CutoffViolationException(string message) : ArchiveDataException(message);
