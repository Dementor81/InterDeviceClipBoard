using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;

namespace Clipboard.Server;

public static class BoardCrypto
{
    public const int TokenByteCount = 32;
    public const int KeyByteCount = 32;
    public const int NonceByteCount = 12;
    public const int TagByteCount = 16;
    public const int NoncePrefixByteCount = 8;

    public static (string Text, byte[] Bytes) CreateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenByteCount);
        return (Base64Url.EncodeToString(bytes), bytes);
    }

    public static byte[] CreateFileKey() => RandomNumberGenerator.GetBytes(KeyByteCount);

    public static bool IsWellFormedSecret(string text) => TryDecodeSecret(text, out _);

    public static bool TryDecodeSecret(string text, out byte[] bytes)
    {
        bytes = [];
        if (text.Length != 43 || !Base64Url.IsValid(text, out var decodedLength) || decodedLength != TokenByteCount)
            return false;

        var buffer = new byte[TokenByteCount];
        try
        {
            if (!Base64Url.TryDecodeFromChars(text, buffer, out var written) || written != TokenByteCount)
                return false;
        }
        catch (FormatException)
        {
            return false;
        }

        bytes = buffer;
        return true;
    }

    public static byte[] HashToken(ReadOnlySpan<byte> token) => SHA256.HashData(token);

    public static byte[] MasterKey(ReadOnlySpan<byte> token)
    {
        var key = new byte[KeyByteCount];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, token, key, "interdeviclipboard"u8, "master-v1"u8);
        return key;
    }

    public static string Encode(ReadOnlySpan<byte> bytes) => Base64Url.EncodeToString(bytes);

    public static void WrapKey(
        ReadOnlySpan<byte> masterKey,
        ReadOnlySpan<byte> fileKey,
        Span<byte> wrapped,
        Span<byte> nonce,
        Span<byte> tag)
    {
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(masterKey, TagByteCount);
        aes.Encrypt(nonce, fileKey, wrapped, tag, default);
    }

    public static bool TryUnwrapKey(
        ReadOnlySpan<byte> masterKey,
        ReadOnlySpan<byte> wrapped,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> tag,
        Span<byte> fileKey)
    {
        try
        {
            using var aes = new AesGcm(masterKey, TagByteCount);
            aes.Decrypt(nonce, wrapped, tag, fileKey, default);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public static void FillChunkNonce(ReadOnlySpan<byte> prefix, uint index, Span<byte> nonce)
    {
        prefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt32BigEndian(nonce[NoncePrefixByteCount..], index);
    }
}
