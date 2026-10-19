using System.Buffers.Binary;
using System.Security.Cryptography;

// Usage: encrypt <input> <output> <keyfile> | decrypt <input> <output> <keyfile> | keygen <keyfile> | check-target <directory> <repository root>
// The key file holds 32 random bytes (base64). The output directory must be outside the repository and OneDrive.
return args switch
{
    ["encrypt", var input, var output, var key] => Run(() => BackupCipher.Encrypt(input, output, BackupCipher.LoadKey(key))),
    ["decrypt", var input, var output, var key] => Run(() => BackupCipher.Decrypt(input, output, BackupCipher.LoadKey(key))),
    ["keygen", var key] => Run(() => BackupCipher.WriteNewKey(key)),
    ["check-target", var directory, var root] => Run(() => BackupTarget.Check(directory, root)),
    _ => Usage(),
};

static int Run(Action action)
{
    try
    {
        action();
        return 0;
    }
    catch (Exception e) when (e is CryptographicException or IOException or InvalidDataException or ArgumentException)
    {
        Console.Error.WriteLine($"backup: {e.Message}");
        return 1;
    }
}

static int Usage()
{
    Console.Error.WriteLine("usage: encrypt <in> <out> <keyfile> | decrypt <in> <out> <keyfile> | keygen <keyfile> | check-target <dir> <repo root>");
    return 2;
}

/// <summary>
/// File format "PIBK1": magic, a random 8-byte file id, then chunks of up to 4 MiB, each sealed with AES-256-GCM
/// under the nonce (file id, chunk index) and the additional data (magic, file id, index, final flag), so chunks
/// cannot be reordered, dropped or truncated without detection.
/// </summary>
internal static class BackupCipher
{
    public const int ChunkSize = 4 * 1024 * 1024;
    private static readonly byte[] Magic = "PIBK1"u8.ToArray();

    public static byte[] LoadKey(string path)
    {
        var key = Convert.FromBase64String(File.ReadAllText(path).Trim());
        return key.Length == 32 ? key : throw new ArgumentException("the key must be 32 bytes");
    }

    public static void WriteNewKey(string path)
    {
        if (File.Exists(path))
        {
            throw new IOException($"{path} exists; a key is never overwritten");
        }

        File.WriteAllText(path, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }

    public static void Encrypt(string input, string output, byte[] key)
    {
        using var aes = new AesGcm(key, 16);
        using var source = File.OpenRead(input);
        using var target = File.Create(output);
        var fileId = RandomNumberGenerator.GetBytes(8);
        target.Write(Magic);
        target.Write(fileId);
        var buffer = new byte[ChunkSize];
        var next = new byte[ChunkSize];
        var length = ReadFull(source, buffer);
        long index = 0;
        while (true)
        {
            var nextLength = ReadFull(source, next);
            var final = nextLength == 0;
            var cipher = new byte[length];
            var tag = new byte[16];
            aes.Encrypt(Nonce(fileId, index), buffer.AsSpan(0, length), cipher, tag, Aad(fileId, index, final));
            var header = new byte[5];
            BinaryPrimitives.WriteInt32LittleEndian(header, length);
            header[4] = final ? (byte)1 : (byte)0;
            target.Write(header);
            target.Write(tag);
            target.Write(cipher);
            if (final)
            {
                return;
            }

            (buffer, next) = (next, buffer);
            length = nextLength;
            index++;
        }
    }

    public static void Decrypt(string input, string output, byte[] key)
    {
        using var aes = new AesGcm(key, 16);
        using var source = File.OpenRead(input);
        var magic = new byte[Magic.Length];
        var fileId = new byte[8];
        if (ReadFull(source, magic) != Magic.Length || !magic.SequenceEqual(Magic) || ReadFull(source, fileId) != 8)
        {
            throw new InvalidDataException("not a PIBK1 backup");
        }

        using var target = File.Create(output);
        long index = 0;
        var header = new byte[5];
        var tag = new byte[16];
        while (true)
        {
            if (ReadFull(source, header) != 5 || ReadFull(source, tag) != 16)
            {
                throw new InvalidDataException("the backup is truncated");
            }

            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            var final = header[4] == 1;
            if (length is < 0 or > ChunkSize)
            {
                throw new InvalidDataException("invalid chunk length");
            }

            var cipher = new byte[length];
            if (ReadFull(source, cipher) != length)
            {
                throw new InvalidDataException("the backup is truncated");
            }

            var plain = new byte[length];
            aes.Decrypt(Nonce(fileId, index), cipher, tag, plain, Aad(fileId, index, final));
            target.Write(plain);
            if (final)
            {
                if (source.ReadByte() != -1)
                {
                    throw new InvalidDataException("data after the final chunk");
                }

                return;
            }

            index++;
        }
    }

    /// <summary>96-bit nonce: the 64-bit random file id and the 32-bit chunk index (unique per key with overwhelming probability).</summary>
    private static byte[] Nonce(byte[] fileId, long index)
    {
        var nonce = new byte[12];
        fileId.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(nonce.AsSpan(8), checked((uint)index));
        return nonce;
    }

    private static byte[] Aad(byte[] fileId, long index, bool final)
    {
        var aad = new byte[Magic.Length + 8 + 8 + 1];
        Magic.CopyTo(aad, 0);
        fileId.CopyTo(aad, Magic.Length);
        BinaryPrimitives.WriteInt64LittleEndian(aad.AsSpan(Magic.Length + 8), index);
        aad[^1] = final ? (byte)1 : (byte)0;
        return aad;
    }

    private static int ReadFull(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}

/// <summary>The backup target is an absolute directory outside the repository and outside OneDrive (U-M5-3).</summary>
internal static class BackupTarget
{
    public static void Check(string directory, string repositoryRoot)
    {
        if (!Path.IsPathFullyQualified(directory))
        {
            throw new ArgumentException("the backup directory must be an absolute path");
        }

        var target = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var root = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("the backup directory is inside the repository");
        }

        var segments = target.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("the backup directory is under OneDrive");
        }

        Console.WriteLine("backup target ok");
    }
}
