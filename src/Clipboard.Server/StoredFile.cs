namespace Clipboard.Server;

public sealed class StoredFile
{
    public required string Id { get; init; }

    public required string OriginalName { get; init; }

    public required string MediaType { get; init; }

    public required long SizeBytes { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required byte[] WrappedKey { get; init; }

    public required byte[] WrapNonce { get; init; }

    public required byte[] WrapTag { get; init; }
}
