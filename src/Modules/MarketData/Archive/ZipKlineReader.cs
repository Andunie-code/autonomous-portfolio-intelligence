using System.IO.Compression;
using System.Text;
using PortfolioIntelligence.Kernel.MarketData;

namespace PortfolioIntelligence.MarketData.Archive;

/// <summary>Reads the single CSV entry of a monthly archive zip and parses it with the Kernel rules.</summary>
public static class ZipKlineReader
{
    public static IReadOnlyList<Candle> Read(ArchiveFileKey key, byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        var entries = archive.Entries.Where(e => !e.FullName.EndsWith('/')).ToList();
        if (entries.Count != 1 || !entries[0].FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArchiveDataException($"{key.FileName}: expected exactly one CSV entry; the file is rejected.");
        }

        using var reader = new StreamReader(entries[0].Open(), Encoding.ASCII);
        return KlineCsvParser.Parse(ReadLines(reader), key);
    }

    private static IEnumerable<string> ReadLines(StreamReader reader)
    {
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }
}
