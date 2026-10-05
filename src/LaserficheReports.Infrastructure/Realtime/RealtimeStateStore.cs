using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
namespace LaserficheReports.Infrastructure.Realtime;

/// <summary>Durable operational state only, never used to answer repository metadata queries.</summary>
public sealed class RealtimeStateStore
{
    private readonly string _connectionString;
    public RealtimeStateStore(IOptions<RealtimeOptions> options)
    {
        Directory.CreateDirectory(options.Value.StateDirectory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(options.Value.StateDirectory, "state.db") }.ToString();
        using var db = Open();
        Execute(db, """
          pragma journal_mode=WAL;
          create table if not exists repositories(key text primary key, cursor integer not null default 0,
            listener text not null default 'Disconnected', detail text, last_event text, last_success text,
            api text not null default 'Unknown', vector text not null default 'Unknown', reconciling integer not null default 0);
          create table if not exists work(repository text not null, entry integer not null, change integer not null,
            version integer not null default 1, attempts integer not null default 0, due integer not null,
            failed integer not null default 0, error text, primary key(repository, entry));
          create table if not exists manifest(repository text not null, entry integer not null,
            metadata_hash text not null, modified text, content_hash text, indexed integer not null default 0,
            generation text, primary key(repository,entry));
          """);
    }
    private SqliteConnection Open() { var db = new SqliteConnection(_connectionString); db.Open(); return db; }
    private static void Execute(SqliteConnection db, string sql, params (string,object?)[] args)
    { using var c = Command(db,sql,args); c.ExecuteNonQuery(); }
    private static SqliteCommand Command(SqliteConnection db, string sql, params (string,object?)[] args)
    { var c=db.CreateCommand();c.CommandText=sql;foreach(var (name,value) in args)c.Parameters.AddWithValue(name,value??DBNull.Value);return c; }
    public static string Key(string server,string repository) => server.TrimEnd('/').ToLowerInvariant()+"|"+repository.ToLowerInvariant();
    public void Ensure(string key) { using var db=Open();Execute(db,"insert or ignore into repositories(key) values($r)",("$r",key)); }
    public long Checkpoint(string key) { Ensure(key);using var db=Open();using var c=Command(db,"select cursor from repositories where key=$r",("$r",key));return (long)c.ExecuteScalar()!; }
    // The work item and cursor commit together. The bridge ACK follows this transaction, never precedes it.
    public void Accept(string key,int entry,EntryChange change,long sequence,int debounceMs=0,string? label=null,bool resetCheckpoint=false)
    {
        using var db=Open();Execute(db,"BEGIN IMMEDIATE");
        Execute(db,"insert or ignore into repositories(key) values($r)",("$r",key));
        if(entry>=0) Execute(db,"""
            insert into work(repository,entry,change,due) values($r,$id,$c,$due)
            on conflict(repository,entry) do update set change=max(work.change,excluded.change),
              version=version+1, due=excluded.due, failed=0, attempts=0, error=null;
            """,("$r",key),("$id",entry),("$c",(int)change),("$due",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+debounceMs));
        Execute(db,resetCheckpoint?"update repositories set cursor=$s,last_event=coalesce($label,last_event) where key=$r":"update repositories set cursor=max(cursor,$s), last_event=coalesce($label,last_event) where key=$r",
            ("$r",key),("$s",sequence),("$label",label));Execute(db,"COMMIT");
    }
    public SyncWork? Next(string key)
    {
        using var db=Open();using var c=Command(db,"select entry,change,version,attempts from work where repository=$r and failed=0 and due<=$now order by case when entry=0 then 1 else 0 end,due limit 1",("$r",key),("$now",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        using var reader=c.ExecuteReader();return reader.Read()?new SyncWork(key,reader.GetInt32(0),(EntryChange)reader.GetInt32(1),reader.GetInt64(2),reader.GetInt32(3)):null;
    }
    // Compare-and-delete prevents a newer event from being lost while the old version is executing.
    public void Complete(SyncWork work)
    { using var db=Open();Execute(db,"BEGIN IMMEDIATE");Execute(db,"delete from work where repository=$r and entry=$id and version=$v",("$r",work.Repository),("$id",work.EntryId),("$v",work.Version));Execute(db,"update repositories set last_success=$t where key=$r",("$r",work.Repository),("$t",DateTimeOffset.UtcNow.ToString("O")));Execute(db,"COMMIT"); }
    public void Fail(SyncWork work,int maxAttempts,string error)
    {
        var attempt=work.Attempts+1;var delay=Math.Min(300000,1000L*(1L<<Math.Min(attempt,18)));
        using var db=Open();Execute(db,"update work set attempts=$a,due=$due,failed=$failed,error=$error where repository=$r and entry=$id and version=$v",
            ("$a",attempt),("$due",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+delay),("$failed",attempt>=maxAttempts?1:0),("$error",error),("$r",work.Repository),("$id",work.EntryId),("$v",work.Version));
    }
    public void RetryFailures(string key) {using var db=Open();Execute(db,"update work set attempts=0,failed=0,due=0,version=version+1 where repository=$r",("$r",key));}
    public void SetListener(string key,string state,string? detail=null) {Ensure(key);using var db=Open();Execute(db,"update repositories set listener=$s,detail=$d where key=$r",("$r",key),("$s",state),("$d",detail));}
    public void SetHealth(string key,string component,string status) { if(component is not ("api" or "vector")) throw new ArgumentException(nameof(component));using var db=Open();Execute(db,$"update repositories set {component}=$s where key=$r",("$r",key),("$s",status)); }
    public void SetReconciling(string key,bool value) {using var db=Open();Execute(db,"update repositories set reconciling=$s where key=$r",("$r",key),("$s",value?1:0));}
    public (string Hash,string? Modified,bool Indexed)? Manifest(string key,int id)
    { using var db=Open();using var c=Command(db,"select metadata_hash,modified,indexed from manifest where repository=$r and entry=$id",("$r",key),("$id",id));using var r=c.ExecuteReader();return r.Read()?(r.GetString(0),r.IsDBNull(1)?null:r.GetString(1),r.GetInt32(2)!=0):null; }
    public void Seen(string key,int id,string hash,string? modified,string generation,bool indexed)
    {using var db=Open();Execute(db,"""
       insert into manifest(repository,entry,metadata_hash,modified,generation,indexed) values($r,$id,$h,$m,$g,$i)
       on conflict(repository,entry) do update set metadata_hash=$h,modified=$m,generation=$g,indexed=$i;
       """,("$r",key),("$id",id),("$h",hash),("$m",modified),("$g",generation),("$i",indexed?1:0));}
    public void MarkIndexed(string key,int id) {using var db=Open();Execute(db,"update manifest set indexed=1 where repository=$r and entry=$id",("$r",key),("$id",id));}
    public void Remove(string key,int id) {using var db=Open();Execute(db,"delete from manifest where repository=$r and entry=$id",("$r",key),("$id",id));}
    public IEnumerable<int> Unseen(string key,string generation)
    {using var db=Open();using var c=Command(db,"select entry from manifest where repository=$r and generation<>$g",("$r",key),("$g",generation));using var r=c.ExecuteReader();while(r.Read())yield return r.GetInt32(0);}
    public SyncStatus Status(string key)
    {
        Ensure(key);using var db=Open();using var c=Command(db,"""
          select listener,detail,cursor,last_event,last_success,api,vector,reconciling,
          (select count(*) from manifest where repository=$r),
          (select count(*) from manifest where repository=$r and indexed=1),
          (select count(*) from work where repository=$r and failed=0),
          (select count(*) from work where repository=$r and failed=1) from repositories where key=$r;
          """,("$r",key));using var r=c.ExecuteReader();r.Read();
        return new SyncStatus(key,r.GetString(0),r.IsDBNull(1)?null:r.GetString(1),r.GetInt64(2),r.GetInt64(8),r.GetInt64(9),r.GetInt64(10),r.GetInt64(11),r.IsDBNull(3)?null:r.GetString(3),r.IsDBNull(4)?null:r.GetString(4),r.GetString(5),r.GetString(6),r.GetInt32(7)!=0);
    }
}
