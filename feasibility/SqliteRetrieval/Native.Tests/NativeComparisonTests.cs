using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace SqliteRetrieval.Native.Tests;

[Trait("type", "IntegrationTests")]
public sealed class NativeComparisonTests(ITestOutputHelper output)
{
    [Fact]
    public void LoadVector_PackagedExtension_ReturnsSameControlledCosineRankingAsPortable()
    {
        output.WriteLine($"Process={RuntimeInformation.ProcessArchitecture}; RID={RuntimeInformation.RuntimeIdentifier}");
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var version = connection.CreateCommand();
        version.CommandText = "SELECT sqlite_version()";
        output.WriteLine($"SQLite={version.ExecuteScalar()}");
        connection.LoadVector();
        version.CommandText = "SELECT vec_version()";
        output.WriteLine($"sqlite-vec={version.ExecuteScalar()}");
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE VIRTUAL TABLE vectors USING vec0(embedding float[3] distance_metric=cosine);
            INSERT INTO vectors(rowid,embedding) VALUES(0,'[2,0,0]'),(1,'[1,1,0]'),(2,'[-1,0,0]');
            CREATE TABLE metadata(ordinal INTEGER PRIMARY KEY, reference TEXT NOT NULL, timestamp_ms INTEGER);
            INSERT INTO metadata VALUES(0,'excerpt-a',1250),(1,'excerpt-b',NULL),(2,'excerpt-c',4000);
            """;
        command.ExecuteNonQuery();
        command.CommandText = """
            SELECT v.rowid,m.reference,m.timestamp_ms,v.distance FROM vectors v
            JOIN metadata m ON m.ordinal=v.rowid
            WHERE v.embedding MATCH '[1,0,0]' AND k=3 ORDER BY v.distance
            """;
        var actual = new List<Match>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                actual.Add(new(reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    1 - reader.GetDouble(3)));
            }
        }
        using var portable = new TranscriptIndex(":memory:");
        var identity = new IndexIdentity("attempt-a", "sha256-source-a", "sha256-model-a", 3, "mean", "none", "chunks-v1");
        portable.Begin(identity, 3);
        portable.Append(identity, new(0, "excerpt-a", 1250, [2, 0, 0]));
        portable.Append(identity, new(1, "excerpt-b", null, [1, 1, 0]));
        portable.Append(identity, new(2, "excerpt-c", 4000, [-1, 0, 0]));
        portable.Complete(identity);
        var expected = portable.Search(identity, [1, 0, 0], 3);
        Assert.Equal([0, 1, 2], actual.Select(match => match.Ordinal));
        Assert.Equal(expected.Select(match => match.Ordinal), actual.Select(match => match.Ordinal));
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(expected[i].Reference, actual[i].Reference);
            Assert.Equal(expected[i].TimestampMilliseconds, actual[i].TimestampMilliseconds);
            Assert.Equal(expected[i].Similarity, actual[i].Similarity, 6);
        }
    }
}
