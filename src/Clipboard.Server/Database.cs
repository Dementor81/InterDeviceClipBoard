using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Clipboard.Server;

public sealed class Database : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();

    public Database(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };
        _connection = new SqliteConnection(builder.ConnectionString);
        _connection.Open();
        Execute("PRAGMA journal_mode = WAL;");
        Execute("PRAGMA foreign_keys = ON;");
        Execute("PRAGMA busy_timeout = 5000;");
        Execute("""
            CREATE TABLE IF NOT EXISTS clipboard (
                token_hash BLOB PRIMARY KEY,
                created_at TEXT NOT NULL,
                last_seen_at TEXT NOT NULL
            );
            """);
        Execute("""
            CREATE TABLE IF NOT EXISTS file (
                id TEXT PRIMARY KEY,
                token_hash BLOB NOT NULL REFERENCES clipboard (token_hash) ON DELETE CASCADE,
                original_name TEXT NOT NULL,
                media_type TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                wrapped_key BLOB NOT NULL,
                wrap_nonce BLOB NOT NULL,
                wrap_tag BLOB NOT NULL
            );
            """);
        Execute("CREATE INDEX IF NOT EXISTS file_clipboard ON file (token_hash);");
    }

    public void CreateClipboard(byte[] tokenHash, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO clipboard (token_hash, created_at, last_seen_at)
                VALUES ($token_hash, $now, $now)
                """;
            AddBlob(command, "$token_hash", tokenHash);
            command.Parameters.AddWithValue("$now", Format(now));
            command.ExecuteNonQuery();
        }
    }

    public bool Exists(byte[] tokenHash)
    {
        lock (_gate)
            return ExistsHeld(tokenHash);
    }

    public IReadOnlyList<StoredFile>? ListFiles(byte[] tokenHash, DateTimeOffset seenAt)
    {
        lock (_gate)
        {
            if (!ExistsHeld(tokenHash))
                return null;

            TouchHeld(tokenHash, seenAt);
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, original_name, media_type, size_bytes, created_at, wrapped_key, wrap_nonce, wrap_tag
                FROM file
                WHERE token_hash = $token_hash
                ORDER BY created_at DESC, id DESC
                """;
            AddBlob(command, "$token_hash", tokenHash);
            using var reader = command.ExecuteReader();
            var files = new List<StoredFile>();
            while (reader.Read())
                files.Add(ReadFile(reader));
            return files;
        }
    }

    public bool TryAddFile(byte[] tokenHash, StoredFile file, long maxTotalBytes, out bool missing)
    {
        lock (_gate)
        {
            if (!ExistsHeld(tokenHash))
            {
                missing = true;
                return false;
            }

            if (TotalBytesHeld() > maxTotalBytes - file.SizeBytes)
            {
                missing = false;
                return false;
            }

            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO file (
                    id, token_hash, original_name, media_type, size_bytes, created_at,
                    wrapped_key, wrap_nonce, wrap_tag)
                VALUES (
                    $id, $token_hash, $original_name, $media_type, $size_bytes, $created_at,
                    $wrapped_key, $wrap_nonce, $wrap_tag)
                """;
            command.Parameters.AddWithValue("$id", file.Id);
            AddBlob(command, "$token_hash", tokenHash);
            command.Parameters.AddWithValue("$original_name", file.OriginalName);
            command.Parameters.AddWithValue("$media_type", file.MediaType);
            command.Parameters.AddWithValue("$size_bytes", file.SizeBytes);
            command.Parameters.AddWithValue("$created_at", Format(file.CreatedAt));
            AddBlob(command, "$wrapped_key", file.WrappedKey);
            AddBlob(command, "$wrap_nonce", file.WrapNonce);
            AddBlob(command, "$wrap_tag", file.WrapTag);
            command.ExecuteNonQuery();
            TouchHeld(tokenHash, file.CreatedAt);
            missing = false;
            return true;
        }
    }

    public StoredFile? FindFile(string id)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, original_name, media_type, size_bytes, created_at, wrapped_key, wrap_nonce, wrap_tag
                FROM file
                WHERE id = $id
                """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadFile(reader) : null;
        }
    }

    public bool DeleteFile(byte[] tokenHash, string id, DateTimeOffset seenAt)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM file WHERE id = $id AND token_hash = $token_hash";
            command.Parameters.AddWithValue("$id", id);
            AddBlob(command, "$token_hash", tokenHash);
            var deleted = command.ExecuteNonQuery() == 1;
            if (deleted)
                TouchHeld(tokenHash, seenAt);
            return deleted;
        }
    }

    public IReadOnlyList<string> DeleteAbandoned(DateTimeOffset cutoff)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            var ids = new List<string>();
            using (var select = _connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = """
                    SELECT f.id
                    FROM file f
                    JOIN clipboard c ON c.token_hash = f.token_hash
                    WHERE c.last_seen_at < $cutoff
                    """;
                select.Parameters.AddWithValue("$cutoff", Format(cutoff));
                using var reader = select.ExecuteReader();
                while (reader.Read())
                    ids.Add(reader.GetString(0));
            }

            using (var delete = _connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM clipboard WHERE last_seen_at < $cutoff";
                delete.Parameters.AddWithValue("$cutoff", Format(cutoff));
                delete.ExecuteNonQuery();
            }

            transaction.Commit();
            return ids;
        }
    }

    public HashSet<string> AllFileIds()
    {
        lock (_gate)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT id FROM file";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                ids.Add(reader.GetString(0));
            return ids;
        }
    }

    public void Dispose() => _connection.Dispose();

    private bool ExistsHeld(byte[] tokenHash)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM clipboard WHERE token_hash = $token_hash";
        AddBlob(command, "$token_hash", tokenHash);
        return command.ExecuteScalar() is not null;
    }

    private void TouchHeld(byte[] tokenHash, DateTimeOffset seenAt)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE clipboard SET last_seen_at = $seen_at WHERE token_hash = $token_hash";
        command.Parameters.AddWithValue("$seen_at", Format(seenAt));
        AddBlob(command, "$token_hash", tokenHash);
        command.ExecuteNonQuery();
    }

    private long TotalBytesHeld()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(size_bytes), 0) FROM file";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static StoredFile ReadFile(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        OriginalName = reader.GetString(1),
        MediaType = reader.GetString(2),
        SizeBytes = reader.GetInt64(3),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        WrappedKey = (byte[])reader.GetValue(5),
        WrapNonce = (byte[])reader.GetValue(6),
        WrapTag = (byte[])reader.GetValue(7)
    };

    private static void AddBlob(SqliteCommand command, string name, byte[] value) =>
        command.Parameters.Add(name, SqliteType.Blob).Value = value;

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
}
