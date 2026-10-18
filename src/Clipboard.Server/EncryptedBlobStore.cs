using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Clipboard.Server;

public sealed class EncryptedBlobStore
{
    private const int ChunkSize = 1024 * 1024;
    private const uint KeyCheckIndex = uint.MaxValue;
    private const int HeaderLength = 4 + 4 + BoardCrypto.NoncePrefixByteCount + BoardCrypto.TagByteCount;
    private static readonly byte[] Magic = "IDCB"u8.ToArray();

    private readonly string _directory;

    public EncryptedBlobStore(string dataDir)
    {
        _directory = Path.Combine(dataDir, "blobs");
        Directory.CreateDirectory(_directory);
    }

    public async Task<(bool TooLarge, long Size)> WriteAsync(
        string id,
        byte[] fileKey,
        Stream input,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var prefix = RandomNumberGenerator.GetBytes(BoardCrypto.NoncePrefixByteCount);
        await using var output = new FileStream(
            PathFor(id, temp: true),
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var header = new byte[HeaderLength];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), ChunkSize);
        prefix.CopyTo(header.AsSpan(8));

        using var aes = new AesGcm(fileKey, BoardCrypto.TagByteCount);
        var nonce = new byte[BoardCrypto.NonceByteCount];
        BoardCrypto.FillChunkNonce(prefix, KeyCheckIndex, nonce);
        aes.Encrypt(nonce, ReadOnlySpan<byte>.Empty, Span<byte>.Empty, header.AsSpan(16), default);
        await output.WriteAsync(header, cancellationToken);

        var plain = ArrayPool<byte>.Shared.Rent(ChunkSize);
        var cipher = ArrayPool<byte>.Shared.Rent(ChunkSize);
        var tag = new byte[BoardCrypto.TagByteCount];
        long total = 0;
        var tooLarge = false;
        try
        {
            for (uint index = 0; ; index++)
            {
                var read = await ReadUpToAsync(input, plain.AsMemory(0, ChunkSize), cancellationToken);
                if (read == 0)
                    break;
                if (total > maxBytes - read)
                {
                    tooLarge = true;
                    break;
                }

                BoardCrypto.FillChunkNonce(prefix, index, nonce);
                aes.Encrypt(nonce, plain.AsSpan(0, read), cipher.AsSpan(0, read), tag, default);
                await output.WriteAsync(cipher.AsMemory(0, read), cancellationToken);
                await output.WriteAsync(tag, cancellationToken);
                total += read;
                if (read < ChunkSize)
                    break;
            }

            if (!tooLarge && total == maxBytes)
            {
                if (await input.ReadAsync(plain.AsMemory(0, 1), cancellationToken) > 0)
                    tooLarge = true;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(plain, clearArray: true);
            ArrayPool<byte>.Shared.Return(cipher, clearArray: true);
        }

        return tooLarge ? (true, 0) : (false, total);
    }

    public bool TryVerify(string id, ReadOnlySpan<byte> fileKey)
    {
        try
        {
            using var input = File.OpenRead(PathFor(id, temp: false));
            var header = new byte[HeaderLength];
            if (input.Read(header) != header.Length || !HeaderLooksRight(header))
                return false;

            Span<byte> nonce = stackalloc byte[BoardCrypto.NonceByteCount];
            BoardCrypto.FillChunkNonce(header.AsSpan(8, BoardCrypto.NoncePrefixByteCount), KeyCheckIndex, nonce);
            using var aes = new AesGcm(fileKey, BoardCrypto.TagByteCount);
            aes.Decrypt(nonce, ReadOnlySpan<byte>.Empty, header.AsSpan(16), Span<byte>.Empty, default);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public async Task CopyPlaintextAsync(
        string id,
        byte[] fileKey,
        long size,
        Stream output,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            PathFor(id, temp: false),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var header = new byte[HeaderLength];
        await ReadExactlyAsync(input, header, cancellationToken);
        if (!HeaderLooksRight(header))
            throw new CryptographicException("Bad blob.");

        using var aes = new AesGcm(fileKey, BoardCrypto.TagByteCount);
        var plain = ArrayPool<byte>.Shared.Rent(ChunkSize);
        var cipher = ArrayPool<byte>.Shared.Rent(ChunkSize);
        var tag = new byte[BoardCrypto.TagByteCount];
        var nonce = new byte[BoardCrypto.NonceByteCount];
        try
        {
            var remaining = size;
            for (uint index = 0; remaining > 0; index++)
            {
                var length = (int)Math.Min(ChunkSize, remaining);
                await ReadExactlyAsync(input, cipher.AsMemory(0, length), cancellationToken);
                await ReadExactlyAsync(input, tag, cancellationToken);
                BoardCrypto.FillChunkNonce(header.AsSpan(8, BoardCrypto.NoncePrefixByteCount), index, nonce);
                aes.Decrypt(nonce, cipher.AsSpan(0, length), tag, plain.AsSpan(0, length), default);
                await output.WriteAsync(plain.AsMemory(0, length), cancellationToken);
                remaining -= length;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(plain, clearArray: true);
            ArrayPool<byte>.Shared.Return(cipher, clearArray: true);
        }
    }

    public void Commit(string id) => File.Move(PathFor(id, temp: true), PathFor(id, temp: false));

    public void Delete(string id) => DeleteIfExists(PathFor(id, temp: false));

    public void DeleteTemp(string id) => DeleteIfExists(PathFor(id, temp: true));

    public void DeleteStale(IReadOnlySet<string> liveIds, TimeSpan minAge)
    {
        var cutoff = DateTime.UtcNow - minAge;
        foreach (var path in Directory.EnumerateFiles(_directory))
        {
            if (File.GetLastWriteTimeUtc(path) >= cutoff)
                continue;

            var name = Path.GetFileName(path);
            if (name.EndsWith(".tmp", StringComparison.Ordinal) || !liveIds.Contains(name))
                File.Delete(path);
        }
    }

    private string PathFor(string id, bool temp)
    {
        if (id.Length != 32 || !id.All(char.IsAsciiHexDigit))
            throw new ArgumentException("Invalid file id.", nameof(id));

        return Path.Combine(_directory, temp ? id + ".tmp" : id);
    }

    private static bool HeaderLooksRight(ReadOnlySpan<byte> header) =>
        header.Length >= HeaderLength
        && header[..4].SequenceEqual(Magic)
        && BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) == ChunkSize;

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static async Task<int> ReadUpToAsync(Stream input, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await input.ReadAsync(buffer[filled..], cancellationToken);
            if (read == 0)
                break;
            filled += read;
        }

        return filled;
    }

    private static async Task ReadExactlyAsync(Stream input, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await input.ReadAsync(buffer[filled..], cancellationToken);
            if (read == 0)
                throw new CryptographicException("Truncated blob.");
            filled += read;
        }
    }
}
