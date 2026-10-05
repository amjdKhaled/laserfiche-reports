using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using LaserficheReports.Application.DTOs;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Entities;
using LaserficheReports.Domain.Exceptions;
using LaserficheReports.Infrastructure.Options;
using LaserficheReports.Infrastructure.Repository;
using LaserficheReports.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
namespace LaserficheReports.Infrastructure.Realtime;

public sealed class LaserficheRealtimeSyncService(IServiceScopeFactory scopes,RealtimeStateStore state,
    RepositoryExecutionContext execution,IOptions<RealtimeOptions> options,IOptionsMonitor<LaserficheOptions> laserfiche,
    IHostEnvironment environment,ILogger<LaserficheRealtimeSyncService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string,RepositoryDescriptor> _repositories=new();
    private readonly ConcurrentDictionary<string,Task> _tasks=new();
    public void Register(RepositoryDescriptor repository)
    {
        if(!Uri.TryCreate(repository.ServerUrl,UriKind.Absolute,out _)||string.IsNullOrWhiteSpace(repository.RepositoryId))return;
        var key=RealtimeStateStore.Key(repository.ServerUrl,repository.RepositoryId);
        _repositories.TryAdd(key,repository);state.Ensure(key);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opt=laserfiche.CurrentValue;
        foreach(var id in options.Value.Repositories.Append(opt.RepositoryId).Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            Register(new(id,opt.ServerUrl,id,id));
        try
        {
            while(!stoppingToken.IsCancellationRequested)
            {
                foreach(var pair in _repositories)
                {
                    if(_tasks.ContainsKey(pair.Key))continue;
                    // No browser ExecutionContext or session credential enters a background worker.
                    using(ExecutionContext.SuppressFlow())
                        _tasks[pair.Key]=Task.Run(()=>RunRepositoryAsync(pair.Key,pair.Value,stoppingToken),stoppingToken);
                }
                await Task.Delay(250,stoppingToken); // Operational queue/registration wake-up, no repository polling.
            }
        }
        catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { }
        finally {await Task.WhenAll(_tasks.Values);}
    }
    private async Task RunRepositoryAsync(string key,RepositoryDescriptor repository,CancellationToken ct)
    {
        if(!options.Value.Enabled){state.SetListener(key,"Disabled");return;}
        using var context=execution.Enter(repository);
        // Initial/recovery work is durable even when the SDK is absent. Live status remains Failed.
        if(state.Status(key).Documents==0)state.Accept(key,0,EntryChange.Reconcile,0,label:"Initial repository synchronization");
        await Task.WhenAll(ListenAsync(key,repository,ct),ProcessAsync(key,ct));
    }
    private async Task ListenAsync(string key,RepositoryDescriptor repository,CancellationToken ct)
    {
        if(!OperatingSystem.IsWindows()){state.SetListener(key,"Failed","إشعارات Laserfiche تحتاج Windows وRepositoryAccess SDK المثبت.");return;}
        var attempt=0;
        while(!ct.IsCancellationRequested)
        {
            Process? process=null;
            try
            {
                state.SetListener(key,"Reconnecting");
                using var scope=scopes.CreateScope();
                var credentials=await scope.ServiceProvider.GetRequiredService<ICredentialProvider>().GetCredentialsAsync(repository.Key,ct);
                var script=Path.Combine(AppContext.BaseDirectory,"integrations","listener.ps1");
                if(!File.Exists(script))script=Path.Combine(environment.ContentRootPath,"..","..","integrations","laserfiche-notifications","listener.ps1");
                if(!File.Exists(script))throw new FileNotFoundException("SDK notification bridge was not published.");
                var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe"))
                {UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true,StandardOutputEncoding=System.Text.Encoding.UTF8};
                foreach(var arg in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","RemoteSigned","-File",script})start.ArgumentList.Add(arg);
                start.Environment["LF_SYNC_SDK"]=options.Value.SdkAssemblyPath;
                start.Environment["LF_SYNC_SERVER"]=string.IsNullOrWhiteSpace(options.Value.RepositoryServer)?new Uri(repository.ServerUrl).Host:options.Value.RepositoryServer;
                start.Environment["LF_SYNC_REPOSITORY"]=repository.RepositoryId;
                start.Environment["LF_SYNC_USERNAME"]=credentials.Username;start.Environment["LF_SYNC_PASSWORD"]=credentials.Password;
                start.Environment["LF_SYNC_CURSOR"]=state.Checkpoint(key).ToString(System.Globalization.CultureInfo.InvariantCulture);
                start.Environment["LF_SYNC_SECURE"]=options.Value.SecureSdkConnection?"true":"false";
                process=Process.Start(start)??throw new InvalidOperationException("SDK bridge could not start.");
                // Drain stderr to prevent a full pipe from blocking notifications; never return raw SDK errors to users.
                var errors=process.StandardError.ReadToEndAsync(ct);
                try
                {
                    while(!ct.IsCancellationRequested)
                    {
                        var line=await process.StandardOutput.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(70),ct);
                        if(line is null)throw new IOException("SDK bridge exited.");
                        using var json=JsonDocument.Parse(line);var message=json.RootElement;var type=message.GetProperty("type").GetString();
                        if(type=="ready") {state.SetListener(key,"Connected","SDK "+message.GetProperty("sdkVersion").GetString());attempt=0;}
                        else if(type=="event")
                        {
                            var entry=message.GetProperty("entryId").GetInt32();var sequence=message.GetProperty("sequence").GetInt64();
                            if(!Enum.TryParse<EntryChange>(message.GetProperty("change").GetString(),out var change))throw new JsonException("Unknown change kind.");
                            var reset=message.TryGetProperty("reset",out var resetValue)&&resetValue.GetBoolean();
                            state.Accept(key,entry,change,sequence,options.Value.DebounceMilliseconds,message.GetProperty("label").GetString(),reset);
                            await process.StandardInput.WriteLineAsync("ACK");await process.StandardInput.FlushAsync(ct);
                        }
                    }
                }
                finally
                {
                    if(!process.HasExited)process.Kill(true);
                    await process.WaitForExitAsync(CancellationToken.None);
                    var detail=await errors;
                    if(!string.IsNullOrWhiteSpace(detail))logger.LogWarning("SDK bridge for {Repository} failed: {Detail}",key,detail);
                }
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
            catch(Exception ex)
            {
                logger.LogError(ex,"Realtime listener failed for {Repository}",key);
                state.SetListener(key,"Failed","تعذر تشغيل مستمع SDK. تحقق من تثبيت SDK وحساب الخدمة والاتصال بخادم المستودع؛ التفاصيل في سجل الخدمة.");
                try {await Task.Delay(TimeSpan.FromSeconds(Math.Min(60,Math.Pow(2,Math.Min(++attempt,6)))),ct);}catch(OperationCanceledException){break;}
            }
            finally {process?.Dispose();}
        }
        state.SetListener(key,"Disconnected");
    }
    private async Task ProcessAsync(string key,CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            var work=state.Next(key);
            if(work is null){try{await Task.Delay(250,ct);}catch(OperationCanceledException){break;}continue;}
            try
            {
                using var scope=scopes.CreateScope();var entries=scope.ServiceProvider.GetRequiredService<ILaserficheEntryService>();
                var ingestion=scope.ServiceProvider.GetRequiredService<ILaserficheDocumentIngestionService>();
                if(work.EntryId==0)await ReconcileAsync(key,work.Change==EntryChange.Rebuild,entries,ct);
                else
                {
                    LFEntry entry;
                    try {entry=await entries.GetEntryAsync(work.EntryId,ct);state.SetHealth(key,"api","Connected");}
                    catch(LaserficheException ex) when(ex.StatusCode==404)
                    {
                        var known=state.Manifest(key,work.EntryId);
                        await ingestion.DeleteAsync(work.EntryId,ct);state.Remove(key,work.EntryId);
                        if(known is null)state.Accept(key,0,EntryChange.Reconcile,0,label:"Deleted hierarchy: verify affected descendants");
                        state.SetHealth(key,"vector","Connected");state.Complete(work);continue;
                    }
                    if(entry.EntryType is LFEntryType.Folder or LFEntryType.RecordSeries)
                        state.Accept(key,0,EntryChange.Reconcile,0,label:"Hierarchy changed: reconcile current paths");
                    else if(entry.EntryType==LFEntryType.Document)
                    {
                        var fields=await entries.GetEntryFieldsAsync(entry.Id,ct);
                        var previous=state.Manifest(key,entry.Id);
                        var result=work.Change==EntryChange.Metadata&&previous?.Indexed==true
                            ?await ingestion.RefreshMetadataAsync(entry.Id,ct):await ingestion.ReindexContentAsync(entry.Id,ct);
                        state.SetHealth(key,"vector","Connected");
                        state.Seen(key,entry.Id,IndexFingerprint.Metadata(entry,fields),entry.LastModifiedTime?.ToString("O"),"event",
                            result.IngestionStatus=="content-indexed"&&string.IsNullOrEmpty(result.ContentDiagnostic));
                        if(result.IngestionStatus!="content-indexed"&&entry.PageCount is >0)
                            throw new InvalidOperationException("Document content is not yet available for indexing.");
                        if(!string.IsNullOrEmpty(result.ContentDiagnostic)&&result.ContentDiagnostic.Contains("failed",StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Partial content extraction must be retried.");
                    }
                }
                state.Complete(work);
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
            catch(Exception ex)
            {
                logger.LogError(ex,"Index operation failed: {Repository}/{Entry}",key,work.EntryId);
                if(ex is LaserficheException or HttpRequestException)state.SetHealth(key,"api","Disconnected");
                if(ex is Npgsql.NpgsqlException)state.SetHealth(key,"vector","Disconnected");
                state.Fail(work,options.Value.MaxAttempts,ex.GetType().Name); // Protected logs contain technical details; queue contains no credentials.
            }
        }
    }
    private async Task ReconcileAsync(string key,bool rebuild,ILaserficheEntryService entries,CancellationToken ct)
    {
        state.SetReconciling(key,true);var generation=Guid.NewGuid().ToString("N");
        try
        {
            await foreach(var entry in LiveRepositoryTraversal.EnumerateAsync(entries,cancellationToken:ct))
            {
                if(entry.EntryType!=LFEntryType.Document)continue;
                var fields=await entries.GetEntryFieldsAsync(entry.Id,ct);var hash=IndexFingerprint.Metadata(entry,fields);
                var modified=entry.LastModifiedTime?.ToString("O");var previous=state.Manifest(key,entry.Id);
                var contentChanged=rebuild||previous is null||!previous.Value.Indexed||previous.Value.Modified!=modified;
                var metadataChanged=previous?.Hash!=hash;
                state.Seen(key,entry.Id,hash,modified,generation,previous?.Indexed==true&&!contentChanged);
                if(contentChanged||metadataChanged)state.Accept(key,entry.Id,contentChanged?EntryChange.Content:EntryChange.Metadata,0,
                    options.Value.DebounceMilliseconds,label:$"Reconcile entry {entry.Id}");
            }
            // Only after a complete successful traversal. A failed/denied folder never causes bulk deletion.
            foreach(var id in state.Unseen(key,generation))state.Accept(key,id,EntryChange.Content,0,label:$"Verify missing entry {id}");
            state.SetHealth(key,"api","Connected");
        }
        finally {state.SetReconciling(key,false);}
    }
}
