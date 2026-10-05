using LaserficheReports.Infrastructure.Configuration;
namespace LaserficheReports.Infrastructure.Realtime;
public sealed class RealtimeOptions
{
    public bool Enabled { get; set; } = true;
    public string StateDirectory { get; set; } = Path.Combine(ReportsConfigPaths.ProgramDataDirectory, "realtime");
    public string SdkAssemblyPath { get; set; } = "";
    public string RepositoryServer { get; set; } = "";
    public bool SecureSdkConnection { get; set; } = true;
    public string[] Repositories { get; set; } = [];
    public string[] AdminUsernames { get; set; } = [];
    public int DebounceMilliseconds { get; set; } = 750;
    public int MaxAttempts { get; set; } = 8;
}
public enum EntryChange { Metadata = 1, Content = 2, Reconcile = 3, Rebuild = 4 }
public sealed record SyncWork(string Repository, int EntryId, EntryChange Change, long Version, int Attempts);
public sealed record SyncStatus(string Repository, string Listener, string? ListenerDetail,
    long Checkpoint, long Documents, long Indexed, long Pending, long Failed, string? LastEvent,
    string? LastSuccessfulSync, string Api, string VectorStore, bool Reconciling);
