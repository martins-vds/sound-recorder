using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Privacy;

namespace SoundRecorder.Infrastructure.Analysis.Storage;

/// <summary>A local scheduling journal; analysis chunks, checkpoints and outputs remain in the canonical store.</summary>
public sealed class AnalysisQueueRepository : IAnalysisQueueRepository
{
    private readonly string path;

    private AnalysisQueueRepository(string path)
    {
        this.path = Path.GetFullPath(path);
        LocalPathPolicy.RejectNetworkDestination(this.path);
    }

    /// <summary>Creates a new journal without replacing any existing private data.</summary>
    public static AnalysisQueueRepository Create(string path)
    {
        var repository = new AnalysisQueueRepository(path);
        using (new FileStream(repository.path, FileMode.CreateNew, FileAccess.Write)) { }
        using var connection = repository.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE analysis_jobs(position INTEGER PRIMARY KEY, payload TEXT NOT NULL, digest TEXT NOT NULL);
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
        return repository;
    }

    /// <summary>Opens only an existing supported journal; never creates or clears missing or incompatible data.</summary>
    public static AnalysisQueueRepository Open(string path)
    {
        var repository = new AnalysisQueueRepository(path);
        using var connection = repository.Connect();
        repository.CheckVersion(connection);
        return repository;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AnalysisJob>> LoadAsync(CancellationToken cancellationToken)
    {
        using var connection = Connect(cancellationToken);
        CheckVersion(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload,digest FROM analysis_jobs ORDER BY position";
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var jobs = new List<AnalysisJob>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var payload = reader.GetString(0);
            if (Digest(payload) != reader.GetString(1))
                throw new InvalidDataException("Scheduling journal integrity verification failed.");
            jobs.Add(JsonSerializer.Deserialize<AnalysisJob>(payload)
                ?? throw new InvalidDataException("Scheduling journal contains a missing job."));
        }
        return jobs;
    }

    /// <inheritdoc />
    public async Task SaveAsync(IReadOnlyList<AnalysisJob> jobs, CancellationToken cancellationToken)
    {
        using var connection = Connect(cancellationToken);
        CheckVersion(connection);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM analysis_jobs";
        await command.ExecuteNonQueryAsync(cancellationToken);
        var position = 0;
        foreach (var job in jobs)
        {
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO analysis_jobs(position,payload,digest) VALUES($position,$payload,$digest)";
            command.Parameters.AddWithValue("$position", position++);
            var payload = JsonSerializer.Serialize(job);
            command.Parameters.AddWithValue("$payload", payload);
            command.Parameters.AddWithValue("$digest", Digest(payload));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private SqliteConnection Connect(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(path)) throw new FileNotFoundException("Local analysis scheduling journal is missing.");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 1
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA synchronous=FULL; PRAGMA secure_delete=ON; PRAGMA temp_store=MEMORY;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void CheckVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        if (Convert.ToInt32(command.ExecuteScalar()) != 1)
            throw new InvalidDataException("Unsupported analysis scheduling journal version.");
    }

    private static string Digest(string payload) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
}
