using LaserficheReports.Application.Interfaces;
using LaserficheReports.Infrastructure.Realtime;
using Microsoft.Extensions.Options;
namespace LaserficheReports.Web;
internal static class RealtimeEndpoints
{
    internal static void MapRealtime(this WebApplication app)
    {
        app.MapGet("/api/reports/sync/status",async(ISessionCredentialStore sessions,IRepositoryContext repositories,
            RealtimeStateStore state,LaserficheRealtimeSyncService service,IOptions<RealtimeOptions> options,CancellationToken ct)=>
        {
            var credential=await sessions.TryGetAsync(ct);if(credential is null)return Results.Unauthorized();
            var repo=await repositories.GetActiveRepositoryAsync(ct);service.Register(repo);
            var status=state.Status(RealtimeStateStore.Key(repo.ServerUrl,repo.RepositoryId));
            var admin=options.Value.AdminUsernames.Contains(credential.Username,StringComparer.OrdinalIgnoreCase);
            return Results.Ok(new {repository=repo.RepositoryId,status.Listener,status.ListenerDetail,status.Documents,status.Indexed,
                status.Pending,status.Failed,status.LastEvent,status.LastSuccessfulSync,status.Api,status.VectorStore,status.Reconciling,
                index=status.Failed>0?"Error":status.Pending>0||status.Reconciling?"Updating":"Ready",admin});
        });
        app.MapPost("/api/reports/sync/{action}",async(string action,SyncRequest request,ISessionCredentialStore sessions,
            IRepositoryContext repositories,RealtimeStateStore state,LaserficheRealtimeSyncService service,IOptions<RealtimeOptions> options,CancellationToken ct)=>
        {
            var user=await sessions.TryGetAsync(ct);if(user is null)return Results.Unauthorized();
            if(!options.Value.AdminUsernames.Contains(user.Username,StringComparer.OrdinalIgnoreCase))return Results.StatusCode(403);
            if(action is not ("recover" or "rebuild")||action=="rebuild"&&request.Confirmation!="REBUILD")return Results.BadRequest(new {error="تأكيد إعادة البناء مطلوب."});
            var repo=await repositories.GetActiveRepositoryAsync(ct);var key=RealtimeStateStore.Key(repo.ServerUrl,repo.RepositoryId);service.Register(repo);
            state.RetryFailures(key);state.Accept(key,0,action=="rebuild"?EntryChange.Rebuild:EntryChange.Reconcile,0,label:action);
            return Results.Accepted(value:new {message="تم حفظ طلب المزامنة في طابور الخدمة؛ يستمر عند إغلاق المتصفح."});
        });
        app.MapGet("/api/reports/files/{token}",async(string token,ISessionCredentialStore sessions,IRepositoryContext repositories,
            LiveReportFiles files,HttpContext http,CancellationToken ct)=>
        {
            if(await sessions.TryGetAsync(ct) is null)return Results.Unauthorized();
            var repo=await repositories.GetActiveRepositoryAsync(ct);var path=files.Resolve(token,repo.RepositoryId,http.Session.Id);
            return path is null||!File.Exists(path)?Results.NotFound():Results.File(path,"text/markdown; charset=utf-8","laserfiche-report.md");
        });
    }
    internal sealed record SyncRequest(string? Confirmation);
}
