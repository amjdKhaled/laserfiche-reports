using LaserficheReports.Infrastructure.Realtime;
using LaserficheReports.Infrastructure.Repository;
using LaserficheReports.Application.DTOs;
using LaserficheReports.Domain.Entities;
using Microsoft.Extensions.Options;
using Xunit;
namespace LaserficheReports.Infrastructure.Tests;
public sealed class RealtimeStateTests : IDisposable
{
    private readonly string _path=Path.Combine(Path.GetTempPath(),"lf-tests-"+Guid.NewGuid().ToString("N"));
    private RealtimeStateStore Create()=>new(Options.Create(new RealtimeOptions {StateDirectory=_path}));
    [Fact] public void DeduplicateUpgradeAndDoNotDropEventArrivingDuringProcessing()
    {
        var state=Create();state.Accept("a",602,EntryChange.Metadata,10);state.Accept("a",602,EntryChange.Content,11);
        var work=state.Next("a")!;Assert.Equal(EntryChange.Content,work.Change);Assert.Equal(1,state.Status("a").Pending);
        state.Accept("a",602,EntryChange.Metadata,12);state.Complete(work);
        Assert.NotNull(state.Next("a"));Assert.Equal(12,state.Checkpoint("a"));
        state.Complete(state.Next("a")!);Assert.Null(state.Next("a"));
    }
    [Fact] public void CursorAndPendingWorkSurviveRestartAndRepositoriesRemainIsolated()
    {
        var state=Create();state.Accept("server|a",602,EntryChange.Content,30);
        var restarted=Create();Assert.Equal(30,restarted.Checkpoint("server|a"));Assert.NotNull(restarted.Next("server|a"));Assert.Null(restarted.Next("server|b"));
        restarted.Accept("server|a",0,EntryChange.Reconcile,2,resetCheckpoint:true);Assert.Equal(2,restarted.Checkpoint("server|a"));
    }
    [Fact] public void FailedOperationIsRetainedAndCanBeRetried()
    {
        var state=Create();state.Accept("a",1,EntryChange.Content,1);var work=state.Next("a")!;
        state.Fail(work,1,"Unavailable");Assert.Null(state.Next("a"));Assert.Equal(1,state.Status("a").Failed);
        state.RetryFailures("a");Assert.NotNull(state.Next("a"));Assert.Equal(0,state.Status("a").Failed);
    }
    [Fact] public void ManifestDeleteOnlyAffectsSelectedRepository()
    {
        var state=Create();state.Ensure("a");state.Ensure("b");state.Seen("a",1,"h",null,"g",true);state.Seen("b",1,"h",null,"g",true);
        state.Remove("a",1);Assert.Null(state.Manifest("a",1));Assert.NotNull(state.Manifest("b",1));
    }
    [Fact] public void MetadataFingerprintIgnoresFieldOrderButDetectsRename()
    {
        var entry=new LFEntry {Id=1,Name="Old",EntryType=LFEntryType.Document};
        LFFieldValue[] fields=[new(){FieldDefinitionId=1,FieldName="A",Value="x"},new(){FieldDefinitionId=2,FieldName="B",Value="y"}];
        Assert.Equal(IndexFingerprint.Metadata(entry,fields),IndexFingerprint.Metadata(entry,fields.Reverse().ToArray()));
        Assert.NotEqual(IndexFingerprint.Metadata(entry,fields),IndexFingerprint.Metadata(entry with {Name="New"},fields));
        Assert.Equal(IndexFingerprint.Hash("content"),IndexFingerprint.Hash("content"));Assert.NotEqual(IndexFingerprint.Hash("content"),IndexFingerprint.Hash("new content"));
    }
    [Fact] public async Task BackgroundRepositoryOverrideIsScopedToAsyncFlow()
    {
        var execution=new RepositoryExecutionContext();var repo=new RepositoryDescriptor("a","https://server","A","A");
        using(execution.Enter(repo))
        {
            Assert.Equal(repo,execution.Current);await Task.Yield();Assert.Equal(repo,execution.Current);
            using(execution.Enter(repo with {RepositoryId="B"}))Assert.Equal("B",execution.Current!.RepositoryId);
            Assert.Equal("A",execution.Current!.RepositoryId);
        }
        Assert.Null(execution.Current);
    }
    public void Dispose(){Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(_path))Directory.Delete(_path,true);}
}
