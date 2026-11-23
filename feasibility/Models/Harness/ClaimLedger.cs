using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Models.Harness;

/// <summary>Explicit presence or absence of a stated owner/deadline.</summary>
public sealed record ClaimField(string State, string? Value);

/// <summary>Semantically UNVERIFIED initial extraction; generated text is not transcript evidence.</summary>
public sealed record StructuredClaim(string Kind, string Statement, ClaimField Owner, ClaimField Deadline,
    string[] Qualifiers);

/// <summary>Stable disk identity and the unchanged structured record.</summary>
public sealed record StoredClaim(long Id, StructuredClaim Record);

/// <summary>Original UTF-16 range and exact text, with original, never interpolated, timestamps.</summary>
public sealed record ClaimEvidence(string SourceId, long Offset, int Length, long? StartMs, long? EndMs, string Quote);

/// <summary>Persisted per-stage native finish and budget evidence, separate from semantic verification.</summary>
public sealed record BatchDiagnostic(int PromptTokens, int OutputBudget, GenerationFinish Finish, int GeneratedTokens, string? Limitation);

/// <summary>Disk-backed exact equality and evidence union; no vector index or in-memory key inventory.</summary>
public sealed class ClaimLedger : IDisposable
{
    private readonly SqliteConnection connection;
    private SqliteTransaction? activeTransaction;

    private ClaimLedger(string path)
    {
        connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
    }

    internal static ClaimLedger Create(string path, TranscriptIdentity identity)
    {
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write)) { }
        var ledger = new ClaimLedger(path);
        ledger.Execute("""
            PRAGMA cache_size=-512;
            PRAGMA temp_store=FILE;
            CREATE TABLE run(identity TEXT NOT NULL, status TEXT NOT NULL, detail TEXT);
            CREATE TABLE claims(id INTEGER PRIMARY KEY, record TEXT NOT NULL UNIQUE);
            CREATE TABLE source(id TEXT NOT NULL, offset INTEGER NOT NULL, length INTEGER NOT NULL,
                start INTEGER, end INTEGER, text TEXT NOT NULL, PRIMARY KEY(id,offset,length));
            CREATE TABLE original(id TEXT NOT NULL, offset INTEGER NOT NULL, length INTEGER NOT NULL,
                start INTEGER, end INTEGER, text TEXT NOT NULL, PRIMARY KEY(id,offset));
            CREATE TABLE evidence(claim INTEGER NOT NULL REFERENCES claims(id), source TEXT NOT NULL,
                offset INTEGER NOT NULL, length INTEGER NOT NULL, PRIMARY KEY(claim,source,offset,length));
            CREATE TABLE source_length(id TEXT PRIMARY KEY, length INTEGER NOT NULL);
            CREATE TABLE batches(id INTEGER PRIMARY KEY, prompt_tokens INTEGER, output_budget INTEGER,
                finish TEXT, generated_tokens INTEGER, limitation TEXT);
            """);
        ledger.Execute("INSERT INTO run VALUES($identity,'Running',NULL)", ("$identity", JsonSerializer.Serialize(identity)));
        return ledger;
    }

    /// <summary>Opens a persisted artifact without creating or replacing it.</summary>
    public static ClaimLedger Open(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Claim ledger does not exist.", path);
        return new(path);
    }

    /// <summary>Streams records in initial insertion order; evidence is read separately so unions remain bounded.</summary>
    public IEnumerable<StoredClaim> ReadClaims()
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,record FROM claims ORDER BY id";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return new(reader.GetInt64(0), JsonSerializer.Deserialize<StructuredClaim>(reader.GetString(1))!);
        }
    }

    /// <summary>Streams resolved original quotes, never generated statements.</summary>
    public IEnumerable<ClaimEvidence> ReadEvidence(long claimId)
    {
        using var command = connection.CreateCommand();
        using var schema = connection.CreateCommand();
        schema.CommandText = "SELECT count(*) FROM pragma_table_info('evidence') WHERE name='length'";
        var rangeJoin = (long)schema.ExecuteScalar()! == 1 ? " AND s.length=e.length" : "";
        command.CommandText = """
            SELECT s.id,s.offset,s.length,s.start,s.end,s.text FROM evidence e
            JOIN source s ON s.id=e.source AND s.offset=e.offset
            """ + rangeJoin + " WHERE e.claim=$claim ORDER BY s.id,s.offset,s.length";
        command.Parameters.AddWithValue("$claim", claimId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return new(reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.GetString(5));
        }
    }

    /// <summary>Reads exact artifact ownership and honest persisted terminal state.</summary>
    public (TranscriptIdentity Identity, string Status, string? Detail) ReadRun()
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT identity,status,detail FROM run";
        using var reader = command.ExecuteReader();
        reader.Read();
        return (JsonSerializer.Deserialize<TranscriptIdentity>(reader.GetString(0))!, reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    internal void AddSource(ClaimEvidence source)
    {
        AddAndValidateSource("source", source, " AND length=$length");
    }

    internal void AddOriginal(ClaimEvidence source) => AddAndValidateSource("original", source, "");

    private void AddAndValidateSource(string table, ClaimEvidence source, string lengthPredicate)
    {
        Execute($"INSERT OR IGNORE INTO {table} VALUES($id,$offset,$length,$start,$end,$text)",
            ("$id", source.SourceId), ("$offset", source.Offset), ("$length", source.Length),
            ("$start", source.StartMs), ("$end", source.EndMs), ("$text", source.Quote));
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT length,start,end,text FROM {table} WHERE id=$id AND offset=$offset{lengthPredicate}";
        command.Parameters.AddWithValue("$id", source.SourceId);
        command.Parameters.AddWithValue("$offset", source.Offset);
        command.Parameters.AddWithValue("$length", source.Length);
        using var reader = command.ExecuteReader();
        reader.Read();
        if (reader.GetInt32(0) != source.Length || !Equals(reader.GetValue(1), (object?)source.StartMs ?? DBNull.Value) ||
            !Equals(reader.GetValue(2), (object?)source.EndMs ?? DBNull.Value) || reader.GetString(3) != source.Quote)
        {
            throw new InvalidDataException("Overlapping source identity changed text or timestamps.");
        }
    }

    internal void AddBatch(IEnumerable<ExtractedClaim> claims, IReadOnlyList<ClaimEvidence> evidence, int promptTokens,
        int budget, GenerationResult generated, string? limitation)
    {
        using var transaction = connection.BeginTransaction();
        activeTransaction = transaction;
        try
        {
            foreach (var claim in claims)
                foreach (var reference in claim.References)
                    AddClaim(claim.Record, evidence[reference]);
            RecordBatch(promptTokens, budget, generated, limitation);
            transaction.Commit();
        }
        finally { activeTransaction = null; }
    }

    private void AddClaim(StructuredClaim record, ClaimEvidence evidence)
    {
        var json = JsonSerializer.Serialize(record);
        Execute("INSERT OR IGNORE INTO claims(record) VALUES($record)", ("$record", json));
        Execute("""
            INSERT OR IGNORE INTO evidence SELECT id,$source,$offset,$length FROM claims WHERE record=$record
            """, ("$source", evidence.SourceId), ("$offset", evidence.Offset), ("$length", evidence.Length), ("$record", json));
    }

    internal void RecordBatch(int promptTokens, int budget, GenerationResult generated, string? limitation) =>
        Execute("INSERT INTO batches(prompt_tokens,output_budget,finish,generated_tokens,limitation) VALUES($prompt,$budget,$finish,$tokens,$limit)",
            ("$prompt", promptTokens), ("$budget", budget), ("$finish", generated.Finish.ToString()),
            ("$tokens", generated.GeneratedTokens), ("$limit", limitation));

    /// <summary>Streams generation diagnostics including capped output that was never committed as claims.</summary>
    public IEnumerable<BatchDiagnostic> ReadBatches()
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT prompt_tokens,output_budget,finish,generated_tokens,limitation FROM batches ORDER BY id";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return new(reader.GetInt32(0), reader.GetInt32(1), Enum.Parse<GenerationFinish>(reader.GetString(2)),
                reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetString(4));
        }
    }

    internal void CompleteSource(string id, long length)
    {
        Execute("INSERT OR IGNORE INTO source_length VALUES($id,$length)", ("$id", id), ("$length", length));
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT length FROM source_length WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        if ((long)command.ExecuteScalar()! != length)
            throw new InvalidDataException("Overlapping source identity changed its total length.");
    }

    internal void Finish(AnalysisStatus status, string? detail) =>
        Execute("UPDATE run SET status=$status,detail=$detail", ("$status", status.ToString()), ("$detail", detail));

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = activeTransaction;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public void Dispose() => connection.Dispose();
}
