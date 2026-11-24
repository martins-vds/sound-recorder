using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace SqliteRetrieval;

/// <summary>Compatibility key for one immutable successful transcript and embedding configuration.</summary>
public sealed record IndexIdentity(string Attempt, string TranscriptHash, string ModelHash, int Dimensions,
    string Pooling, string Normalization, string Chunking);

/// <summary>A stable excerpt with its saved-audio timestamp and controlled embedding.</summary>
public sealed record IndexedExcerpt(int Ordinal, string Reference, long? TimestampMilliseconds, float[] Vector);

/// <summary>A citation and cosine similarity, not a global coverage claim.</summary>
public sealed record Match(int Ordinal, string Reference, long? TimestampMilliseconds, double Similarity);

/// <summary>Completed excerpt checkpoints are distinct from a published whole index.</summary>
public sealed record IndexProgress(int CompletedExcerpts, bool Complete);

/// <summary>Isolated SQLite cache experiment; never stores authoritative transcripts or analysis.</summary>
// shortcut: one worker owns the cache; add transactional concurrency tests before supporting multiple writers.
public sealed class TranscriptIndex : IDisposable
{
    private readonly SqliteConnection connection;

    /// <summary>Opens an explicitly selected isolated cache database.</summary>
    public TranscriptIndex(string path)
    {
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        Execute("""
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS targets(
                attempt TEXT PRIMARY KEY, transcript_hash TEXT NOT NULL, model_hash TEXT NOT NULL,
                dimensions INTEGER NOT NULL, pooling TEXT NOT NULL, normalization TEXT NOT NULL,
                chunking TEXT NOT NULL, expected INTEGER NOT NULL, complete INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS excerpts(
                attempt TEXT NOT NULL REFERENCES targets(attempt) ON DELETE CASCADE,
                ordinal INTEGER NOT NULL, reference TEXT NOT NULL, timestamp_ms INTEGER, vector BLOB NOT NULL,
                PRIMARY KEY(attempt, ordinal), UNIQUE(attempt, reference));
            """);
    }

    /// <summary>Stages an index before any excerpt becomes queryable.</summary>
    public IndexProgress Begin(IndexIdentity identity, int expectedExcerpts)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedExcerpts);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(identity.Dimensions);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Attempt);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.TranscriptHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.ModelHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Pooling);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Normalization);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Chunking);
        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM targets WHERE attempt=$attempt";
        exists.Parameters.AddWithValue("$attempt", identity.Attempt);
        if ((long)exists.ExecuteScalar()! != 0)
        {
            var state = ReadState(identity);
            if (state.Expected != expectedExcerpts)
            {
                throw new InvalidOperationException("Incompatible excerpt count.");
            }
            return new(state.Count, state.Complete);
        }
        Execute("""
            INSERT INTO targets(attempt,transcript_hash,model_hash,dimensions,pooling,normalization,chunking,expected)
            VALUES($attempt,$hash,$model,$dimensions,$pooling,$normalization,$chunking,$expected)
            """, ("$attempt", identity.Attempt), ("$hash", identity.TranscriptHash), ("$model", identity.ModelHash),
            ("$dimensions", identity.Dimensions), ("$pooling", identity.Pooling), ("$normalization", identity.Normalization),
            ("$chunking", identity.Chunking), ("$expected", expectedExcerpts));
        return new(0, false);
    }

    /// <summary>Persists one bounded excerpt as a durable checkpoint.</summary>
    public void Append(IndexIdentity identity, IndexedExcerpt excerpt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(excerpt.Reference);
        if (excerpt.TimestampMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(excerpt));
        }
        var state = ReadState(identity);
        if (state.Complete)
        {
            throw new InvalidOperationException("Completed caches are immutable.");
        }
        if (excerpt.Ordinal != state.Count || excerpt.Ordinal >= state.Expected)
        {
            throw new ArgumentException("Excerpts must form a contiguous checkpoint prefix.");
        }
        ValidateVector(excerpt.Vector, identity.Dimensions);
        Execute("INSERT INTO excerpts VALUES($attempt,$ordinal,$reference,$timestamp,$vector)",
            ("$attempt", identity.Attempt), ("$ordinal", excerpt.Ordinal), ("$reference", excerpt.Reference),
            ("$timestamp", excerpt.TimestampMilliseconds), ("$vector", MemoryMarshal.AsBytes(excerpt.Vector.AsSpan()).ToArray()));
    }

    /// <summary>Publishes the staged cache.</summary>
    public void Complete(IndexIdentity identity)
    {
        var state = ReadState(identity);
        if (state.Count != state.Expected)
        {
            throw new InvalidOperationException("Partial work is not a complete index.");
        }
        Execute("UPDATE targets SET complete=1 WHERE attempt=$attempt", ("$attempt", identity.Attempt));
    }

    /// <summary>Explicitly removes only the named attempt's rebuildable cache, including checkpoints.</summary>
    public void DeleteCache(string attempt)
    {
        Execute("DELETE FROM targets WHERE attempt=$attempt", ("$attempt", attempt));
    }

    /// <summary>Streams vectors for one selected attempt and keeps only bounded top-k results.</summary>
    public IReadOnlyList<Match> Search(IndexIdentity identity, float[] query, int count, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ValidateVector(query, identity.Dimensions);
        if (!ReadState(identity).Complete)
        {
            throw new InvalidOperationException("Partial work is not queryable.");
        }
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ordinal,reference,timestamp_ms,vector FROM excerpts WHERE attempt=$attempt ORDER BY ordinal";
        command.Parameters.AddWithValue("$attempt", identity.Attempt);
        using var reader = command.ExecuteReader();
        var results = new List<Match>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var vector = MemoryMarshal.Cast<byte, float>((byte[])reader[3]);
            double dot = 0, left = 0, right = 0;
            for (var i = 0; i < query.Length; i++)
            {
                dot += (double)query[i] * vector[i];
                left += (double)query[i] * query[i];
                right += (double)vector[i] * vector[i];
            }
            results.Add(new(reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt64(2),
                dot / Math.Sqrt(left * right)));
            results.Sort((a, b) =>
            {
                var comparison = b.Similarity.CompareTo(a.Similarity);
                return comparison != 0 ? comparison : a.Ordinal.CompareTo(b.Ordinal);
            });
            if (results.Count > count)
            {
                results.RemoveAt(count);
            }
        }
        return results;
    }

    private static void ValidateVector(float[] vector, int dimensions)
    {
        if (vector.Length != dimensions)
        {
            throw new ArgumentException("Embedding dimension mismatch.");
        }
        foreach (var value in vector)
        {
            if (!float.IsFinite(value))
            {
                throw new ArgumentException("Non-finite embedding.");
            }
        }
        if (vector.All(value => value == 0))
        {
            throw new ArgumentException("Cosine is undefined for zero embeddings.");
        }
    }

    private (int Expected, int Count, bool Complete) ReadState(IndexIdentity identity)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT transcript_hash,model_hash,dimensions,pooling,normalization,chunking,expected,complete,
                (SELECT COUNT(*) FROM excerpts WHERE attempt=$attempt)
            FROM targets WHERE attempt=$attempt
            """;
        command.Parameters.AddWithValue("$attempt", identity.Attempt);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException("No cache for the selected attempt.");
        }
        var stored = new IndexIdentity(identity.Attempt, reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5));
        if (stored != identity)
        {
            throw new InvalidOperationException("Incompatible transcript or embedding configuration.");
        }
        return (reader.GetInt32(6), reader.GetInt32(8), reader.GetBoolean(7));
    }

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        command.ExecuteNonQuery();
    }

    /// <summary>Releases the native connection so checkpoints can be reopened.</summary>
    public void Dispose() => connection.Dispose();
}
