using System.Text;
using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.MarketData.Archive;
using PortfolioIntelligence.MarketData.Persistence;

namespace PortfolioIntelligence.MarketData;

public enum FileStatus
{
    Imported,
    AlreadyImported,
    Missing,
    Rejected,
}

public sealed record FileImportResult(ArchiveFileKey Key, FileStatus Status, string? Sha256, int Rows, string? Reason);

/// <summary>
/// Imports one monthly file: cache or download, checksum, parse, write. Integrity failures reject the whole file
/// and are reported; a cutoff violation is a contamination incident: the bytes are deleted, the incident is
/// logged and the run stops (docs/m1-data-policy.md §3 item 9). Transient network errors propagate and stop the run.
/// </summary>
public sealed class ArchiveImporter(ArchiveClient? client, ArchiveCacheDirectory cache, CandleStore store, IncidentLog incidents)
{
    public async Task<FileImportResult> ImportAsync(ArchiveFileKey key, CancellationToken cancellationToken)
    {
        var zipPath = cache.PathFor(key.Path);
        var checksumPath = cache.PathFor(key.ChecksumPath);
        try
        {
            ArchiveFile? file;
            if (File.Exists(zipPath))
            {
                var zip = await File.ReadAllBytesAsync(zipPath, cancellationToken);
                var checksum = File.Exists(checksumPath) ? await File.ReadAllTextAsync(checksumPath, Encoding.ASCII, cancellationToken) : null;
                file = new ArchiveFile(key, zip, ChecksumVerifier.Verify(key, zip, checksum));
            }
            else
            {
                if (client is null)
                {
                    throw new InvalidOperationException($"{key.FileName} is not cached and network access is disabled.");
                }

                file = await client.DownloadAsync(key, cancellationToken);
                if (file is null)
                {
                    return new FileImportResult(key, FileStatus.Missing, null, 0, "not found in the archive");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
                await File.WriteAllBytesAsync(zipPath, file.Zip, cancellationToken);
                await File.WriteAllTextAsync(checksumPath, $"{file.Sha256}  {key.FileName}", Encoding.ASCII, cancellationToken);
            }

            var candles = ZipKlineReader.Read(key, file.Zip);
            var outcome = await store.ImportAsync(key, file.Sha256, file.Zip.LongLength, candles, cancellationToken);
            return new FileImportResult(key, outcome == ImportOutcome.Imported ? FileStatus.Imported : FileStatus.AlreadyImported,
                file.Sha256, candles.Count, null);
        }
        catch (CutoffViolationException ex)
        {
            DeleteQuietly(zipPath);
            DeleteQuietly(checksumPath);
            incidents.Append("cutoff-violation", key.Path, ex.Message);
            throw;
        }
        catch (ArchiveDataException ex)
        {
            DeleteQuietly(zipPath);
            DeleteQuietly(checksumPath);
            return new FileImportResult(key, FileStatus.Rejected, null, 0, ex.Message);
        }
    }

    private static void DeleteQuietly(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
