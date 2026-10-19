using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;

namespace PortfolioIntelligence.Infrastructure.Tests;

/// <summary>Backup encryption and target rules (m5-protocol §7.4, U-M5-3). Local temporary files only.</summary>
public class BackupCipherTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("pi-backup-");

    public void Dispose() => _directory.Delete(recursive: true);

    private string File(string name) => Path.Combine(_directory.FullName, name);

    [Fact]
    public void Multi_chunk_round_trip_and_every_kind_of_tampering_is_detected()
    {
        var plain = RandomNumberGenerator.GetBytes(2 * BackupCipher.ChunkSize + 12_345);
        System.IO.File.WriteAllBytes(File("dump"), plain);
        BackupCipher.WriteNewKey(File("key"));
        Assert.Throws<IOException>(() => BackupCipher.WriteNewKey(File("key")));
        var key = BackupCipher.LoadKey(File("key"));

        BackupCipher.Encrypt(File("dump"), File("dump.enc"), key);
        BackupCipher.Decrypt(File("dump.enc"), File("restored"), key);
        Assert.Equal(plain, System.IO.File.ReadAllBytes(File("restored")));

        var sealedBytes = System.IO.File.ReadAllBytes(File("dump.enc"));
        var flipped = (byte[])sealedBytes.Clone();
        flipped[^100] ^= 1;
        System.IO.File.WriteAllBytes(File("flipped.enc"), flipped);
        Assert.ThrowsAny<CryptographicException>(() => BackupCipher.Decrypt(File("flipped.enc"), File("x"), key));

        // Drop the final chunk: the previous chunk is not marked final, so decryption fails as truncated.
        var lastChunk = 5 + 16 + 12_345;
        System.IO.File.WriteAllBytes(File("truncated.enc"), sealedBytes[..^lastChunk]);
        Assert.Throws<InvalidDataException>(() => BackupCipher.Decrypt(File("truncated.enc"), File("x"), key));

        System.IO.File.WriteAllText(File("other"), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        Assert.ThrowsAny<CryptographicException>(() => BackupCipher.Decrypt(File("dump.enc"), File("x"), BackupCipher.LoadKey(File("other"))));
        Assert.DoesNotContain(plain[..64], sealedBytes.Chunk(64).ToList(), new ByteArrayComparer());
    }

    [Fact]
    public void The_backup_target_must_be_absolute_and_outside_the_repository_and_OneDrive()
    {
        var root = Path.Combine(_directory.FullName, "repo");
        BackupTarget.Check(Path.Combine(_directory.FullName, "external"), root);
        Assert.Throws<ArgumentException>(() => BackupTarget.Check(Path.Combine(root, "backups"), root));
        Assert.Throws<ArgumentException>(() => BackupTarget.Check(Path.Combine(_directory.FullName, "OneDrive - Personal", "b"), root));
        Assert.Throws<ArgumentException>(() => BackupTarget.Check("relative/backups", root));
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y) => x is not null && y is not null && x.SequenceEqual(y);

        public int GetHashCode(byte[] obj) => obj.Length;
    }
}

/// <summary>H25 self-check of Research.Host: no status_relay credential in its configuration, environment or secrets.</summary>
public class ResearchHostCredentialTests
{
    [Fact]
    public void The_scan_finds_a_planted_status_relay_credential_and_nothing_otherwise()
    {
        var clean = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Research"] = "Host=postgres;Username=research_app" }).Build();
        Assert.Empty(Research.Host.ResearchHost.CredentialScan(clean, ["PATH", "HOME"], []));

        var planted = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Relay"] = "Host=postgres;Username=status_relay" }).Build();
        var directory = Directory.CreateTempSubdirectory("pi-secrets-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "status_relay"), "x");
            var found = Research.Host.ResearchHost.CredentialScan(planted, ["STATUS_RELAY_PASSWORD"], [directory.FullName]);
            Assert.Equal(["config:ConnectionStrings:Relay", "env:STATUS_RELAY_PASSWORD", "file:status_relay"], found);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
