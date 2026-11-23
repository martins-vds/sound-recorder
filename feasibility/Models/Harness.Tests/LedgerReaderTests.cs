using Microsoft.Data.Sqlite;
using System.Text.Json;
using Models.Harness;
using Xunit;

namespace Models.Harness.Tests;

public sealed class LedgerReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadEvidence_GoldenArchivedOrCurrentArtifact_ResolvesExactRangesWithoutJoiningOtherLengths(bool current)
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "ledger-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "golden.sqlite");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var setup = connection.CreateCommand();
                setup.CommandText = current
                    ? "CREATE TABLE source(id TEXT,offset INTEGER,length INTEGER,start INTEGER,end INTEGER,text TEXT); CREATE TABLE evidence(claim INTEGER,source TEXT,offset INTEGER,length INTEGER);"
                    : "CREATE TABLE source(id TEXT,offset INTEGER,length INTEGER,start INTEGER,end INTEGER,text TEXT); CREATE TABLE evidence(claim INTEGER,source TEXT,offset INTEGER);";
                setup.ExecuteNonQuery();
                setup.CommandText = """
                    CREATE TABLE claims(id INTEGER,record TEXT);
                    CREATE TABLE run(identity TEXT,status TEXT,detail TEXT);
                    INSERT INTO source VALUES('s000001',0,3,10,20,'abc');
                    INSERT INTO run VALUES($identity,'Completed',NULL);
                    INSERT INTO claims VALUES(1,$record);
                    """;
                setup.Parameters.AddWithValue("$identity", JsonSerializer.Serialize(new TranscriptIdentity("golden", new string('a', 64))));
                setup.Parameters.AddWithValue("$record", JsonSerializer.Serialize(new StructuredClaim("Report", "golden",
                    new("Absent", null), new("Absent", null), ["unverified"])));
                setup.ExecuteNonQuery();
                setup.Parameters.Clear();
                setup.CommandText = current
                    ? """
                        INSERT INTO source VALUES('s000001',0,6,10,20,'abcdef');
                        INSERT INTO evidence VALUES(1,'s000001',0,3);
                        INSERT INTO claims VALUES(2,$record);
                        INSERT INTO evidence VALUES(2,'s000001',0,3);
                        INSERT INTO evidence VALUES(2,'s000001',0,6);
                        """
                    : "INSERT INTO evidence VALUES(1,'s000001',0);";
                setup.Parameters.AddWithValue("$record", JsonSerializer.Serialize(new StructuredClaim("Report", "union",
                    new("Absent", null), new("Absent", null), ["unverified"])));
                setup.ExecuteNonQuery();
            }
            using var ledger = ClaimLedger.Open(path);
            Assert.Equal("golden", ledger.ReadRun().Identity.SuccessfulTranscriptionAttemptId);
            var evidence = Assert.Single(ledger.ReadEvidence(1));
            Assert.Equal("abc", evidence.Quote);
            Assert.Equal(3, evidence.Length);
            Assert.Equal(0, evidence.Offset);
            Assert.Equal(10, evidence.StartMs);
            Assert.Equal(20, evidence.EndMs);
            if (current)
            {
                Assert.Equal(["abc", "abcdef"], ledger.ReadEvidence(2).Select(e => e.Quote));
                Assert.Equal(2, ledger.ReadClaims().Count());
            }
            else Assert.Single(ledger.ReadClaims());
        }
        finally { Directory.Delete(directory, true); }
    }
}
