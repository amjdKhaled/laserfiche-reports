using System.Runtime.CompilerServices;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Entities;
using Microsoft.Data.Sqlite;
namespace LaserficheReports.Infrastructure.Services;

/// <summary>Request-scoped live traversal, disk-backed deduplication; no million-entry in-memory tree.</summary>
public static class LiveRepositoryTraversal
{
    public static async IAsyncEnumerable<LFEntry> EnumerateAsync(ILaserficheEntryService entries,
        int? root = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var path=Path.Combine(Path.GetTempPath(), "lf-query-"+Guid.NewGuid().ToString("N")+".db");
        try
        {
            await using var db=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=path,Pooling=false}.ToString());
            await db.OpenAsync(cancellationToken);
            await Run("create table seen(id integer primary key); create table folders(id integer primary key,done integer not null default 0);");
            await Run("insert into folders(id) values($id)",root??await entries.GetRootEntryIdAsync(cancellationToken));
            while(true)
            {
                using var next=db.CreateCommand();next.CommandText="select id from folders where done=0 limit 1";
                var value=await next.ExecuteScalarAsync(cancellationToken);if(value is null)break;
                var folder=Convert.ToInt32(value);
                await foreach(var entry in entries.StreamFolderChildrenAsync(folder,cancellationToken))
                {
                    var added=await Run("insert or ignore into seen(id) values($id)",entry.Id);
                    if(added==0)continue;
                    if(entry.EntryType is LFEntryType.Folder or LFEntryType.RecordSeries)
                        await Run("insert or ignore into folders(id) values($id)",entry.Id);
                    yield return entry;
                }
                await Run("update folders set done=1 where id=$id",folder);
            }
            async Task<int> Run(string sql,int? id=null)
            {using var c=db.CreateCommand();c.CommandText=sql;if(id.HasValue)c.Parameters.AddWithValue("$id",id.Value);return await c.ExecuteNonQueryAsync(cancellationToken);}
        }
        finally {if(File.Exists(path))File.Delete(path);}
    }
}
