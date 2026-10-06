using LaserficheReports.Web;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace LaserficheReports.Web.Tests;

public class HybridRetrievalTests
{
    [Fact]
    public void ArabicSearchKeysPreserveWordsAndNormalizeHamzaAndDigits()
    {
        Assert.Equal("اجراء الوثيقة 123", HybridRetrieval.Normalize("إِجـراء الوثيقة ١۲٣"));
        var query = HybridRetrieval.KeywordQuery("ما موعد القرار ٩٧٧؟");
        Assert.Contains("'977'", query);
        Assert.Contains("'موعد'", query);
        Assert.DoesNotContain("'ما'", query);
    }

    [Fact]
    public void KeywordQueryCannotIntroduceTsQueryOperatorsOrSql()
    {
        var query = HybridRetrieval.KeywordQuery("'); DROP TABLE documents; -- :* & ! | ١٢٣");
        Assert.DoesNotContain(";", query);
        Assert.DoesNotContain(":", query);
        Assert.DoesNotContain("!", query);
        Assert.Contains("'123'", query);
        Assert.True(query.Split(" | ").Length <= 20);
    }
}

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("REPORTS_TEST_POSTGRES")))
            Skip = "Requires the isolated reports_test PostgreSQL/pgvector CI database.";
    }
}

public class HybridRetrievalDatabaseTests
{
    [PostgresFact]
    public async Task HybridQueryFindsExactArabicTermsAndHonorsEveryScopeFilter()
    {
        var builder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("REPORTS_TEST_POSTGRES"));
        Assert.Equal("reports_test", builder.Database);
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        // A fresh table in an isolated test DB, all changes rolled back, never the user's documents.
        await using (var setup = new NpgsqlCommand("""
            create schema if not exists extensions;
            create extension if not exists vector with schema extensions;
            create table public.documents(id bigint primary key, content text, metadata jsonb, embedding extensions.vector(3));
            insert into public.documents values
              (1, 'وصف عام', '{"source":"laserfiche-reports","repository_id":"repo","record_type":"document-chunk","entry_id":1}', '[1,0,0]'),
              (2, 'موعد قرار ٩٧٧: الأحد', '{"source":"laserfiche-reports","repository_id":"repo","record_type":"document-chunk","entry_id":2}', '[0,1,0]'),
              (3, 'قرار ٩٧٧ سري', '{"source":"laserfiche-reports","repository_id":"other","record_type":"document-chunk","entry_id":3}', '[1,0,0]'),
              (4, 'قرار ٩٧٧ ليس للمشروع', '{"source":"n8n","repository_id":"repo","record_type":"document-chunk","entry_id":4}', '[1,0,0]'),
              (5, 'قرار ٩٧٧ بلا متجه', '{"source":"laserfiche-reports","repository_id":"repo","record_type":"document-chunk","entry_id":5}', null),
              (6, 'قرار ٩٧٧ بيانات أصلية', '{"source":"laserfiche-reports","repository_id":"repo","record_type":"document-metadata","entry_id":6}', '[1,0,0]'),
              (7, 'قرار ٩٧٧ بيانات أصلية قديمة', '{"source":"laserfiche-reports","repository_id":"repo","record_type":"document-chunk","text_source":"laserfiche-metadata","entry_id":7}', '[1,0,0]');
            """, connection, transaction)) await setup.ExecuteNonQueryAsync();

        async Task<List<string>> Query(bool hasVector, string[] ids, string question)
        {
            await using var command = new NpgsqlCommand(HybridRetrieval.Sql, connection, transaction);
            command.Parameters.AddWithValue("repository", "REPO");
            command.Parameters.AddWithValue("hasEntryFilter", ids.Length > 0);
            command.Parameters.AddWithValue("entryIds", ids);
            command.Parameters.AddWithValue("hasVector", hasVector);
            command.Parameters.Add(new NpgsqlParameter("embedding", NpgsqlDbType.Text) { Value = hasVector ? "[1,0,0]" : DBNull.Value });
            command.Parameters.AddWithValue("keywords", HybridRetrieval.KeywordQuery(question));
            command.Parameters.AddWithValue("candidateLimit", 24);
            var found = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) found.Add(reader.GetString(0));
            return found;
        }

        var hybrid = await Query(true, [], "قرار 977");
        Assert.Contains("موعد قرار ٩٧٧: الأحد", hybrid); // vector similarity zero: lexical rescues it
        Assert.Contains("قرار ٩٧٧ بلا متجه", hybrid);
        Assert.DoesNotContain(hybrid, text => text.Contains("سري") || text.Contains("للمشروع") || text.Contains("بيانات أصلية"));
        var selected = await Query(true, ["2"], "قرار 977");
        Assert.Equal(new[] { "موعد قرار ٩٧٧: الأحد" }, selected);
        var lexicalOnly = await Query(false, [], "977");
        Assert.Equal(2, lexicalOnly.Count);
        Assert.Empty(await Query(false, [], "ما هي الوثائق"));
        await transaction.RollbackAsync();
    }
}
