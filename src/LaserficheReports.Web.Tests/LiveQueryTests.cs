using LaserficheReports.Application.DTOs;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Common;
using LaserficheReports.Domain.Entities;
using LaserficheReports.Web;
using Microsoft.Extensions.Configuration;
using Xunit;
namespace LaserficheReports.Web.Tests;
public sealed class LiveQueryTests
{
    [Fact] public async Task RepositoryCountWorksWithoutVectorStoreAndIsNotLimitedByDisplayOrTopK()
    {
        var entries=new Entries(1200);var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
          { ["Supabase:PostgresConnectionString"]="Host=127.0.0.1;Port=1;Timeout=1",["Reports:MaxLiveDocuments"]="8" }).Build();
        var live=new LiveQueryService(entries,null!,null!,null!);
        var chat=new ReportsChatService(config,new NoEmbeddings(),new Repository(),entries,new NoClients(),new(entries,config),live);
        var answer=await chat.AskAsync("كم عدد الوثائق الموجودة؟",default);
        Assert.Equal(1200,answer.Scope!.DocumentCount);Assert.True(answer.Scope.Exhaustive);Assert.Equal(500,answer.Sources.Count);
        Assert.Contains("1200",answer.Answer);Assert.Contains("أول 500",answer.Answer);
    }
    [Fact] public async Task ExactFieldQueryChecksEveryLiveDocumentAndDoesNotUseSemanticSearch()
    {
        var entries=new Entries(1200);var live=new LiveQueryService(entries,null!,new Definitions(),null!);
        var intent=QueryRouter.Route("ماهي الوثائق الموجود فيها إجراء الوثيقة يساوي تحت الإجراء؟");
        var count=0;await foreach(var entry in live.MatchingAsync(intent,default)){Assert.Equal(1,entry.Id%2);count++;}
        Assert.Equal(600,count);Assert.Equal(1200,entries.FieldReads);
    }
    [Fact] public async Task DuplicateContinuationRowsAreCountedOnce()
    {
        var entries=new Entries(6,true);var live=new LiveQueryService(entries,null!,null!,null!);
        var answer=await live.AnswerAsync(QueryRouter.Route("كم عدد الوثائق؟"),"repo",default);Assert.Equal(6,answer.Scope!.DocumentCount);
    }
    private sealed class Definitions:ILaserficheFieldDefinitionService
    { public Task<IReadOnlyDictionary<int,LFFieldDefinition>> GetFieldDefinitionsAsync(CancellationToken cancellationToken=default)=>
        Task.FromResult<IReadOnlyDictionary<int,LFFieldDefinition>>(new Dictionary<int,LFFieldDefinition>{{1,new(){Id=1,Name="إجراء الوثيقة"}}}); }
    private sealed class NoEmbeddings:ITextEmbeddingService
    {public Task<IReadOnlyList<float[]>> CreateEmbeddingsAsync(IReadOnlyList<string> texts,CancellationToken cancellationToken=default)=>throw new Exception("Live queries must not request embeddings.");}
    private sealed class NoClients:IHttpClientFactory
    {public HttpClient CreateClient(string name)=>throw new Exception("Live queries must not call AI.");}
    private sealed class Repository:IRepositoryContext
    {
        public Task<RepositoryDescriptor> GetActiveRepositoryAsync(CancellationToken cancellationToken=default)=>Task.FromResult(new RepositoryDescriptor("repo","https://server","repo","repo"));
        public Task<IReadOnlyList<RepositoryDescriptor>> GetAllRepositoriesAsync(CancellationToken cancellationToken=default)=>throw new NotSupportedException();
    }
    private sealed class Entries(int count,bool duplicate=false):ILaserficheEntryService
    {
        public int FieldReads {get;private set;}
        private static LFEntry Doc(int id)=>new(){Id=id,Name="Document "+id,EntryType=LFEntryType.Document};
        public Task<int> GetRootEntryIdAsync(CancellationToken cancellationToken=default)=>Task.FromResult(900000);
        public Task<LFEntry> GetEntryAsync(int entryId,CancellationToken cancellationToken=default)=>Task.FromResult(Doc(entryId));
        public Task<IReadOnlyList<LFEntry>> GetAllFolderChildrenAsync(int entryId,CancellationToken cancellationToken=default)=>
            Task.FromResult<IReadOnlyList<LFEntry>>(Enumerable.Range(1,count).SelectMany(x=>duplicate?new[]{Doc(x),Doc(x)}:new[]{Doc(x)}).ToArray());
        public Task<IReadOnlyList<LFFieldValue>> GetEntryFieldsAsync(int entryId,CancellationToken cancellationToken=default)
        {FieldReads++;return Task.FromResult<IReadOnlyList<LFFieldValue>>([new(){FieldName="إجراء الوثيقة",Value=entryId%2==1?"تحت الإجراء":"تم الرفض"}]);}
        public Task<LFTemplate?> GetEntryTemplateAsync(int entryId,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public Task<string> GetEntryPathAsync(int entryId,CancellationToken cancellationToken=default)=>Task.FromResult("");
        public Task<PagedResult<LFEntry>> GetEntryChildrenAsync(int entryId,int page,int pageSize,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public Task<IReadOnlyList<LFEntry>> GetFolderTreeAsync(int rootEntryId,int depth,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
    }
}
