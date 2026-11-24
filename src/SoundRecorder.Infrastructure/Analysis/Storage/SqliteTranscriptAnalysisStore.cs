using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Privacy;

namespace SoundRecorder.Infrastructure.Analysis.Storage;

/// <summary>SQLite-backed exact-attempt storage; each operation owns its connection and transaction.</summary>
public sealed partial class SqliteTranscriptAnalysisStore : ITranscriptAnalysisStore
{
    private readonly string path;
    private readonly ILogger<SqliteTranscriptAnalysisStore> logger;

    private SqliteTranscriptAnalysisStore(string path, ILogger<SqliteTranscriptAnalysisStore>? logger)
    {
        this.path = Path.GetFullPath(path);
        LocalPathPolicy.RejectNetworkDestination(this.path);
        this.logger = logger ?? NullLogger<SqliteTranscriptAnalysisStore>.Instance;
    }

    /// <summary>Creates a new private store without replacing an existing file.</summary>
    public static SqliteTranscriptAnalysisStore Create(string path,
        ILogger<SqliteTranscriptAnalysisStore>? logger = null)
    {
        var store = new SqliteTranscriptAnalysisStore(path, logger);
        using (new FileStream(store.path, FileMode.CreateNew, FileAccess.Write)) { }
        using var connection = store.Connect(checkVersion: false);
        using var transaction = store.Transaction(connection);
        Execute(connection, transaction, """
            CREATE TABLE owners(attempt TEXT PRIMARY KEY, text_hash TEXT NOT NULL,
                subtitle_hash TEXT NOT NULL, language TEXT NOT NULL);
            CREATE TABLE revisions(id TEXT PRIMARY KEY, attempt TEXT NOT NULL REFERENCES owners(attempt) ON DELETE CASCADE,
                identity_key TEXT NOT NULL, identity TEXT NOT NULL, status TEXT NOT NULL,
                next_ordinal INTEGER NOT NULL DEFAULT 0, checkpoint TEXT, checkpoint_digest TEXT, sequence INTEGER NOT NULL);
            CREATE INDEX revision_identity ON revisions(attempt,identity_key,sequence);
            CREATE TABLE chunks(revision TEXT NOT NULL REFERENCES revisions(id) ON DELETE CASCADE,
                ordinal INTEGER NOT NULL, payload TEXT NOT NULL, digest TEXT NOT NULL, PRIMARY KEY(revision,ordinal));
            CREATE TABLE outputs(revision TEXT NOT NULL REFERENCES revisions(id) ON DELETE CASCADE,
                ordinal INTEGER NOT NULL, payload TEXT NOT NULL, digest TEXT NOT NULL, PRIMARY KEY(revision,ordinal));
            PRAGMA user_version=1;
            """);
        store.Commit(transaction, default);
        return store;
    }

    /// <summary>Opens an existing private store; never silently creates missing data.</summary>
    public static SqliteTranscriptAnalysisStore Open(string path,
        ILogger<SqliteTranscriptAnalysisStore>? logger = null)
    {
        var store = new SqliteTranscriptAnalysisStore(path, logger);
        using var connection = store.Connect();
        return store;
    }

    /// <inheritdoc />
    public Task<AnalysisStoreRevision> BeginAsync(AnalysisStoreIdentity identity, CancellationToken cancellationToken)
    {
        using var connection = Connect(cancellationToken);
        identity = AnalysisStoreValidation.Canonical(identity);
        using var transaction = Transaction(connection);
        CheckOwner(connection, transaction, identity, true);
        if (Convert.ToInt32(Scalar(connection, transaction,
            "SELECT COUNT(*) FROM revisions WHERE attempt=$attempt AND status='Processing'",
            ("$attempt", identity.AttemptId.ToString()))) != 0)
            throw new InvalidOperationException("An unfinished writer exists; explicitly stop or resume it.");
        var revision = new AnalysisStoreRevision(Guid.NewGuid(), identity);
        var json = JsonSerializer.Serialize(identity);
        Execute(connection, transaction, """
            INSERT INTO revisions(id,attempt,identity_key,identity,status,sequence)
            VALUES($id,$attempt,$key,$identity,'Processing',(SELECT COALESCE(MAX(sequence),0)+1 FROM revisions))
            """, ("$id", revision.Id.ToString()), ("$attempt", identity.AttemptId.ToString()),
            ("$key", Digest(json)), ("$identity", json));
        Commit(transaction, cancellationToken);
        return Task.FromResult(revision);
    }

    /// <inheritdoc />
    public Task<AnalysisStoreSnapshot> LoadAsync(AnalysisStoreIdentity identity, CancellationToken cancellationToken)
    {
        using var connection = Connect(cancellationToken);
        identity = AnalysisStoreValidation.Canonical(identity);
        using var transaction = Transaction(connection, deferred: true);
        CheckOwner(connection, transaction, identity, false);
        using var command = Command(connection, transaction, """
            SELECT id,status,checkpoint,checkpoint_digest FROM revisions
            WHERE attempt=$attempt AND identity_key=$key ORDER BY sequence DESC LIMIT 1
            """, ("$attempt", identity.AttemptId.ToString()), ("$key", Digest(JsonSerializer.Serialize(identity))));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidDataException("Stored analysis identity is incompatible; explicit rebuild is required.");
        var latest = new AnalysisStoreRevision(Guid.Parse(reader.GetString(0)), identity);
        var status = Enum.Parse<AnalysisStoreStatus>(reader.GetString(1));
        var checkpoint = ReadCheckpoint(reader);
        CheckRevision(connection, transaction, latest, false);
        var complete = CompletedRevision(connection, transaction, identity);
        return Task.FromResult(new AnalysisStoreSnapshot(complete,
            status == AnalysisStoreStatus.Complete ? null : latest, status, checkpoint));
    }

    /// <inheritdoc />
    public async Task SaveCheckpointAsync(AnalysisStoreRevision revision, AnalysisStoreCheckpoint checkpoint,
        IAsyncEnumerable<AnalysisStoreChunk> chunks, CancellationToken cancellationToken)
    {
        using var connection = Connect(cancellationToken);
        AnalysisStoreValidation.Checkpoint(checkpoint);
        using var transaction = Transaction(connection);
        CheckRevision(connection, transaction, revision, true);
        var ordinal = Convert.ToInt32(Scalar(connection, transaction, "SELECT next_ordinal FROM revisions WHERE id=$id",
            ("$id", revision.Id.ToString())));
        await foreach (var chunk in chunks.WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (chunk.Ordinal != ordinal++) throw new InvalidDataException("Checkpoint chunk ordinals must be contiguous.");
            AnalysisStoreValidation.Chunk(chunk, revision.Identity.VectorDimensions);
            InsertPayload(connection, transaction, "chunks", revision.Id, chunk.Ordinal, chunk);
        }
        if (checkpoint.NextOrdinal != ordinal) throw new InvalidDataException("Checkpoint cursor must match committed chunks.");
        var json = JsonSerializer.Serialize(checkpoint);
        Execute(connection, transaction, """
            UPDATE revisions SET next_ordinal=$ordinal,checkpoint=$checkpoint,checkpoint_digest=$digest WHERE id=$id
            """, ("$ordinal", ordinal), ("$checkpoint", json), ("$digest", Digest(json)), ("$id", revision.Id.ToString()));
        Commit(transaction, cancellationToken);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AnalysisStoreChunk> ReadChunksAsync(AnalysisStoreRevision revision,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var json in ReadRows(revision, "chunks", false, cancellationToken))
            yield return JsonSerializer.Deserialize<AnalysisStoreChunk>(json)!;
    }

    /// <inheritdoc />
    public async Task CompleteAsync(AnalysisStoreRevision revision, IAsyncEnumerable<AnalysisStoreOutput> outputs,
        CancellationToken cancellationToken)
    {
        using var connection = Connect(cancellationToken);
        using var transaction = Transaction(connection);
        CheckRevision(connection, transaction, revision, true);
        var ordinal = 0;
        await foreach (var output in outputs.WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (output.Ordinal != ordinal++) throw new InvalidDataException("Output ordinals must be contiguous.");
            AnalysisStoreValidation.Output(output);
            InsertPayload(connection, transaction, "outputs", revision.Id, output.Ordinal, output);
        }
        if (ordinal == 0) throw new InvalidDataException("Completed analysis must contain explicit output.");
        Execute(connection, transaction, "UPDATE revisions SET status='Complete' WHERE id=$id", ("$id", revision.Id.ToString()));
        Commit(transaction, cancellationToken);
        Activated(logger);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AnalysisStoreOutput> ReadOutputsAsync(AnalysisStoreRevision revision,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var json in ReadRows(revision, "outputs", true, cancellationToken))
            yield return JsonSerializer.Deserialize<AnalysisStoreOutput>(json)!;
    }

    /// <inheritdoc />
    public Task SetUnfinishedAsync(AnalysisStoreRevision revision, AnalysisStoreStatus status,
        CancellationToken cancellationToken)
    {
        if (status is not (AnalysisStoreStatus.Cancelled or AnalysisStoreStatus.Interrupted or AnalysisStoreStatus.Failed))
            throw new ArgumentOutOfRangeException(nameof(status), "An explicit unfinished state is required.");
        return UpdateStatusAsync(revision, status, cancellationToken);
    }
    /// <inheritdoc />
    public Task ResumeAsync(AnalysisStoreRevision revision, CancellationToken cancellationToken) =>
        UpdateStatusAsync(revision, AnalysisStoreStatus.Processing, cancellationToken);
    /// <inheritdoc />
    public Task DeleteOwnedAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        using var connection = Connect(cancellationToken);
        if (attemptId == Guid.Empty) throw new ArgumentException("An explicit transcript attempt is required.");
        using var transaction = Transaction(connection);
        Execute(connection, transaction, "DELETE FROM owners WHERE attempt=$attempt", ("$attempt", attemptId.ToString()));
        Commit(transaction, cancellationToken);
        Deleted(logger);
        return Task.CompletedTask;
    }

    private Task UpdateStatusAsync(AnalysisStoreRevision revision, AnalysisStoreStatus status, CancellationToken cancellationToken)
    {
        using var connection = Connect(cancellationToken);
        using var transaction = Transaction(connection);
        CheckRevision(connection, transaction, revision, false);
        CheckCurrentRevision(connection, transaction, revision);
        using var command = Command(connection, transaction,
            "SELECT status,checkpoint,checkpoint_digest FROM revisions WHERE id=$id", ("$id", revision.Id.ToString()));
        using var reader = command.ExecuteReader();
        reader.Read();
        if (reader.GetString(0) == "Complete")
            throw new InvalidOperationException("A completed revision cannot become unfinished.");
        if (status == AnalysisStoreStatus.Processing) ReadCheckpoint(reader, 1);
        Execute(connection, transaction, "UPDATE revisions SET status=$status WHERE id=$id",
            ("$status", status.ToString()), ("$id", revision.Id.ToString()));
        Commit(transaction, cancellationToken);
        return Task.CompletedTask;
    }

    private SqliteConnection Connect(CancellationToken cancellationToken = default, bool checkVersion = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path)) throw new FileNotFoundException("Local analysis store is missing.");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 1
        }.ToString());
        try
        {
            connection.Open();
            Execute(connection, null, "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL; PRAGMA secure_delete=ON; PRAGMA cache_size=-512; PRAGMA temp_store=FILE;");
            if (checkVersion && Convert.ToInt32(Scalar(connection, null, "PRAGMA user_version")) != 1)
                throw new InvalidDataException("Unsupported local analysis store version.");
            return connection;
        }
        catch (SqliteException error)
        {
            connection.Dispose();
            throw StorageError(error);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void CheckOwner(SqliteConnection connection, SqliteTransaction transaction,
        AnalysisStoreIdentity identity, bool create)
    {
        if (create)
            Execute(connection, transaction, "INSERT OR IGNORE INTO owners VALUES($id,$text,$subtitle,$language)",
                ("$id", identity.AttemptId.ToString()), ("$text", identity.TextSha256),
                ("$subtitle", identity.SubtitleSha256), ("$language", identity.Language));
        using var command = Command(connection, transaction,
            "SELECT text_hash,subtitle_hash,language FROM owners WHERE attempt=$id", ("$id", identity.AttemptId.ToString()));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("No analysis exists for the requested transcript attempt.");
        if (reader.GetString(0) != identity.TextSha256 || reader.GetString(1) != identity.SubtitleSha256 ||
            reader.GetString(2) != identity.Language)
            throw new InvalidDataException("Immutable transcript attempt source identity changed.");
    }

    private static void CheckRevision(SqliteConnection connection, SqliteTransaction transaction,
        AnalysisStoreRevision revision, bool writing)
    {
        using var command = Command(connection, transaction,
            "SELECT identity,status FROM revisions WHERE id=$id", ("$id", revision.Id.ToString()));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("Analysis revision is missing.");
        if (reader.GetString(0) != JsonSerializer.Serialize(revision.Identity))
            throw new InvalidDataException("Analysis revision identity is incompatible.");
        if (writing && reader.GetString(1) != "Processing")
            throw new InvalidOperationException("Only a processing revision accepts writes.");
    }

    private static void CheckCurrentRevision(SqliteConnection connection, SqliteTransaction transaction,
        AnalysisStoreRevision revision)
    {
        var latest = (string)Scalar(connection, transaction,
            "SELECT id FROM revisions WHERE attempt=$attempt ORDER BY sequence DESC LIMIT 1",
            ("$attempt", revision.Identity.AttemptId.ToString()))!;
        if (latest != revision.Id.ToString())
            throw new InvalidOperationException("A newer explicit replacement owns the writer token.");
    }

    private static AnalysisStoreRevision? CompletedRevision(SqliteConnection connection, SqliteTransaction transaction,
        AnalysisStoreIdentity identity)
    {
        var id = Scalar(connection, transaction, """
            SELECT id FROM revisions WHERE attempt=$attempt AND identity_key=$key AND status='Complete'
            ORDER BY sequence DESC LIMIT 1
            """, ("$attempt", identity.AttemptId.ToString()), ("$key", Digest(JsonSerializer.Serialize(identity)))) as string;
        return id is null ? null : new(Guid.Parse(id), identity);
    }

    private static AnalysisStoreCheckpoint? ReadCheckpoint(SqliteDataReader reader, int column = 2)
    {
        if (reader.IsDBNull(column)) return null;
        var json = reader.GetString(column);
        if (reader.IsDBNull(column + 1) || Digest(json) != reader.GetString(column + 1))
            throw new InvalidDataException("Corrupt local analysis checkpoint.");
        return JsonSerializer.Deserialize<AnalysisStoreCheckpoint>(json)
            ?? throw new InvalidDataException("Missing local analysis checkpoint.");
    }

    private static void InsertPayload<T>(SqliteConnection connection, SqliteTransaction transaction,
        string table, Guid revision, int ordinal, T value)
    {
        var json = JsonSerializer.Serialize(value);
        Execute(connection, transaction, $"INSERT INTO {table} VALUES($id,$ordinal,$payload,$digest)",
            ("$id", revision.ToString()), ("$ordinal", ordinal), ("$payload", json), ("$digest", Digest(json)));
    }

    private async IAsyncEnumerable<string> ReadRows(AnalysisStoreRevision revision, string table, bool completedOnly,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var connection = Connect(cancellationToken);
        using var transaction = Transaction(connection, deferred: true);
        CheckRevision(connection, transaction, revision, false);
        if (completedOnly && (string)Scalar(connection, transaction, "SELECT status FROM revisions WHERE id=$id",
            ("$id", revision.Id.ToString()))! != "Complete")
            throw new InvalidOperationException("Unfinished analysis has no completed output.");
        using var command = Command(connection, transaction,
            $"SELECT payload,digest FROM {table} WHERE revision=$id ORDER BY ordinal", ("$id", revision.Id.ToString()));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = reader.GetString(0);
            if (Digest(json) != reader.GetString(1)) throw new InvalidDataException("Corrupt local analysis payload.");
            yield return json;
        }
        await Task.CompletedTask;
    }

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private SqliteTransaction Transaction(SqliteConnection connection, bool deferred = false)
    {
        try { return connection.BeginTransaction(deferred); }
        catch (SqliteException error) { throw StorageError(error); }
    }

    private void Commit(SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { transaction.Commit(); }
        catch (SqliteException error) { throw StorageError(error); }
    }

    private IOException StorageError(SqliteException error)
    {
        StorageFailed(logger, error.SqliteErrorCode);
        return new IOException("Local analysis storage failed; check disk space, access and database integrity.", error);
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        command.ExecuteNonQuery();
    }

    [LoggerMessage(4100, LogLevel.Information, "Local analysis revision activated.")]
    private static partial void Activated(ILogger logger);

    [LoggerMessage(4101, LogLevel.Information, "Owned local analysis data deleted.")]
    private static partial void Deleted(ILogger logger);

    [LoggerMessage(4102, LogLevel.Error, "Local analysis storage failed (SQLite code {Code}).")]
    private static partial void StorageFailed(ILogger logger, int code);
}
