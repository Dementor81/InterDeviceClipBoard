using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Clipboard.Server;

public static class ClipboardEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/clipboards", Create).RequireRateLimiting("create");
        app.MapGet("/api/files", List);
        app.MapPost("/api/files", Upload);
        app.MapDelete("/api/files/{id}", Delete);
        app.MapGet("/d/{fileId}/{fileKey}", Download);
        app.MapGet("/c/{token}", BoardPage);
        app.MapGet("/app.js", Script);
    }

    private static IResult Create(Database database, AppOptions options)
    {
        var (text, bytes) = BoardCrypto.CreateToken();
        try
        {
            if (!database.TryCreateClipboard(BoardCrypto.HashToken(bytes), DateTimeOffset.UtcNow, options.MaxClipboards))
                return Results.StatusCode(StatusCodes.Status409Conflict);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }

        return Results.Json(new { token = text }, statusCode: StatusCodes.Status201Created);
    }

    private static IResult List(HttpRequest request, Database database)
    {
        if (!TryAuthorize(request, out var tokenBytes))
            return Results.NotFound();

        var tokenHash = BoardCrypto.HashToken(tokenBytes);
        var master = BoardCrypto.MasterKey(tokenBytes);
        CryptographicOperations.ZeroMemory(tokenBytes);
        try
        {
            var files = database.ListFiles(tokenHash, DateTimeOffset.UtcNow);
            if (files is null)
                return Results.NotFound();

            var payload = new List<object>(files.Count);
            foreach (var file in files)
            {
                var fileKey = new byte[BoardCrypto.KeyByteCount];
                try
                {
                    if (!BoardCrypto.TryUnwrapKey(master, file.WrappedKey, file.WrapNonce, file.WrapTag, fileKey))
                        return Results.StatusCode(StatusCodes.Status500InternalServerError);

                    payload.Add(new
                    {
                        id = file.Id,
                        name = file.OriginalName,
                        mediaType = file.MediaType,
                        size = file.SizeBytes,
                        createdAt = file.CreatedAt,
                        downloadPath = "/d/" + file.Id + "/" + BoardCrypto.Encode(fileKey)
                    });
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(fileKey);
                }
            }

            return Results.Json(payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }
    }

    private static async Task<IResult> Upload(
        HttpRequest request,
        Database database,
        EncryptedBlobStore blobs,
        AppOptions options,
        CancellationToken cancellationToken)
    {
        if (!TryAuthorize(request, out var tokenBytes))
            return Results.NotFound();

        var tokenHash = BoardCrypto.HashToken(tokenBytes);
        var master = BoardCrypto.MasterKey(tokenBytes);
        CryptographicOperations.ZeroMemory(tokenBytes);
        if (!database.Exists(tokenHash))
        {
            CryptographicOperations.ZeroMemory(master);
            return Results.NotFound();
        }

        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType)
            || !contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            CryptographicOperations.ZeroMemory(master);
            return Results.BadRequest();
        }

        var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
        if (string.IsNullOrEmpty(boundary))
        {
            CryptographicOperations.ZeroMemory(master);
            return Results.BadRequest();
        }

        var reader = new MultipartReader(boundary, request.Body);
        var section = await FindFileSectionAsync(reader, cancellationToken);
        if (section is null)
        {
            CryptographicOperations.ZeroMemory(master);
            return Results.BadRequest();
        }

        var id = Guid.NewGuid().ToString("N");
        var fileKey = BoardCrypto.CreateFileKey();
        var wrapped = new byte[BoardCrypto.KeyByteCount];
        var wrapNonce = new byte[BoardCrypto.NonceByteCount];
        var wrapTag = new byte[BoardCrypto.TagByteCount];
        BoardCrypto.WrapKey(master, fileKey, wrapped, wrapNonce, wrapTag);
        CryptographicOperations.ZeroMemory(master);

        var committed = false;
        try
        {
            var (tooLarge, size) = await blobs.WriteAsync(
                id, fileKey, section.Body, options.MaxUploadBytes, cancellationToken);
            if (tooLarge)
            {
                blobs.DeleteTemp(id);
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var stored = new StoredFile
            {
                Id = id,
                OriginalName = SanitizeFileName(FileNameOf(section)),
                MediaType = MediaTypeOf(section.ContentType),
                SizeBytes = size,
                CreatedAt = DateTimeOffset.UtcNow,
                WrappedKey = wrapped,
                WrapNonce = wrapNonce,
                WrapTag = wrapTag
            };

            blobs.Commit(id);
            committed = true;
            if (!database.TryAddFile(tokenHash, stored, options.MaxTotalBytes, out var missing))
            {
                blobs.Delete(id);
                committed = false;
                return missing
                    ? Results.NotFound()
                    : Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            return Results.Json(new { id }, statusCode: StatusCodes.Status201Created);
        }
        catch
        {
            if (committed)
                blobs.Delete(id);
            else
                blobs.DeleteTemp(id);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileKey);
        }
    }

    private static IResult Delete(string id, HttpRequest request, Database database, EncryptedBlobStore blobs)
    {
        if (!IsFileId(id) || !TryAuthorize(request, out var tokenBytes))
            return Results.NotFound();

        var tokenHash = BoardCrypto.HashToken(tokenBytes);
        CryptographicOperations.ZeroMemory(tokenBytes);
        if (!database.DeleteFile(tokenHash, id, DateTimeOffset.UtcNow))
            return Results.NotFound();

        blobs.Delete(id);
        return Results.NoContent();
    }

    private static async Task Download(
        HttpContext context,
        string fileId,
        string fileKey,
        Database database,
        EncryptedBlobStore blobs,
        CancellationToken cancellationToken)
    {
        byte[] key = [];
        try
        {
            if (!IsFileId(fileId) || !BoardCrypto.TryDecodeSecret(fileKey, out key))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var file = database.FindFile(fileId);
            if (file is null || !blobs.TryVerify(file.Id, key))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = file.MediaType;
            context.Response.ContentLength = file.SizeBytes;
            context.Response.Headers.ContentDisposition = AttachmentDisposition(file.OriginalName);
            await blobs.CopyPlaintextAsync(file.Id, key, file.SizeBytes, context.Response.Body, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static IResult BoardPage(string token, IWebHostEnvironment environment)
    {
        if (!BoardCrypto.IsWellFormedSecret(token))
            return Results.NotFound();

        return Results.File(
            Path.Combine(environment.WebRootPath, "index.html"),
            "text/html; charset=utf-8");
    }

    private static IResult Script(IWebHostEnvironment environment)
    {
        var path = Path.Combine(environment.WebRootPath, "app.js");
        return File.Exists(path)
            ? Results.File(path, "text/javascript; charset=utf-8")
            : Results.NotFound();
    }

    private static bool TryAuthorize(HttpRequest request, out byte[] token)
    {
        token = [];
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        return BoardCrypto.TryDecodeSecret(header[prefix.Length..].Trim(), out token);
    }

    private static bool IsFileId(string id) =>
        id.Length == 32 && id.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static async Task<MultipartSection?> FindFileSectionAsync(MultipartReader reader, CancellationToken cancellationToken)
    {
        while (await reader.ReadNextSectionAsync(cancellationToken) is { } section)
        {
            if (ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                && disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase)
                && Unquote(disposition.Name) == "file")
            {
                return section;
            }

            await DrainAsync(section.Body, cancellationToken);
        }

        return null;
    }

    private static string? FileNameOf(MultipartSection section)
    {
        if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
            return null;

        if (disposition.FileNameStar.Value is { Length: > 0 } star)
            return star;
        return disposition.FileName.Value;
    }

    private static string Unquote(StringSegment value)
    {
        var text = value.Value ?? "";
        return text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;
    }

    private static async Task DrainAsync(Stream body, CancellationToken cancellationToken)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (await body.ReadAsync(buffer, cancellationToken) > 0)
            {
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string SanitizeFileName(string? raw)
    {
        var name = string.IsNullOrWhiteSpace(raw) ? "file" : raw.Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
            name = name[(slash + 1)..];

        var cleaned = new string(name.Where(static c => !char.IsControl(c) && c != '"').ToArray()).Trim();
        if (cleaned is "" or "." or "..")
            cleaned = "file";
        return cleaned.Length > 200 ? cleaned[..200] : cleaned;
    }

    private static string MediaTypeOf(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !MediaTypeHeaderValue.TryParse(raw, out var parsed))
            return "application/octet-stream";

        var type = parsed.MediaType.Value;
        if (string.IsNullOrEmpty(type) || type.Length > 127 || type.Any(static c => c < ' ' || c > '~' || c is ';' or '"' or '\\'))
            return "application/octet-stream";

        return type;
    }

    private static string AttachmentDisposition(string name)
    {
        var chars = new char[name.Length];
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            chars[i] = c >= ' ' && c <= '~' && c != '"' && c != '\\' ? c : '_';
        }

        var ascii = new string(chars);
        if (ascii.Trim('_', ' ', '.') == "")
            ascii = "file";

        return $"attachment; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(name)}";
    }
}
