using LaserficheReports.Application.DTOs;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Common;
using LaserficheReports.Domain.Entities;
using LaserficheReports.Web;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LaserficheReports.Infrastructure.Tests;

public sealed class ReportsChatScopeTests
{
    [Fact]
    public async Task UserFieldQuestionChecksAll73LiveDocumentsAndReturnsAll60Matches()
    {
        var entries = new Entries();
        var result = await Service(entries).AskAsync(
            "ماهي الوثائق الموجود فيها إجراء الوثيقة يساوي تحت الاجراء", default);
        Assert.Equal(73, entries.FieldsRead.Count);
        Assert.Contains("فحصت 73 وثيقة", result.Answer);
        Assert.Contains("60.", result.Answer);
        Assert.Contains("100158 — ID 560", result.Answer);
        Assert.DoesNotContain("ID 100158", result.Answer);
        for (var id = 501; id <= 560; id++)
            Assert.Contains($"— ID {id}", result.Answer);
    }

    [Fact]
    public async Task ExplicitIdNarrowsFieldQueryToOneDocument()
    {
        var entries = new Entries();
        var result = await Service(entries).AskAsync(
            "وثيقة ID 560 إجراء الوثيقة يساوي تحت الاجراء", default);
        Assert.Equal(new[] { 560 }, entries.FieldsRead);
        Assert.Contains("فحصت 1 وثيقة", result.Answer);
        Assert.Contains("100158 — ID 560", result.Answer);
        Assert.DoesNotContain("ID 501", result.Answer);
    }

    [Fact]
    public async Task ExplicitFolderNarrowsFieldQueryToItsDocuments()
    {
        var entries = new Entries();
        var result = await Service(entries).AskAsync(
            "في مجلد QA ماهي الوثائق الموجود فيها إجراء الوثيقة يساوي تحت الاجراء", default);
        Assert.Equal(50, entries.FieldsRead.Count);
        Assert.Contains("مجلد QA", result.Answer);
        Assert.DoesNotContain("— ID 560", result.Answer);
    }

    [Fact]
    public async Task GeneralInventoryQuestionListsAllDocumentsWithoutModelOrIndex()
    {
        var entries = new Entries();
        var result = await Service(entries).AskAsync("ماهي الوثائق الموجودة؟", default);
        Assert.Contains("73.", result.Answer);
        Assert.Contains("— ID 573", result.Answer);
    }

    private static ReportsChatService Service(Entries entries) =>
        new(new ConfigurationBuilder().Build(), new Repository(), entries, new NoModel());

    private sealed class NoModel : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Field/list queries must not call a model.");
    }

    private sealed class Repository : IRepositoryContext
    {
        private readonly RepositoryDescriptor descriptor = new("test", "http://localhost", "testemployee", "test");
        public Task<RepositoryDescriptor> GetActiveRepositoryAsync(CancellationToken cancellationToken = default) => Task.FromResult(descriptor);
        public Task<IReadOnlyList<RepositoryDescriptor>> GetAllRepositoriesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RepositoryDescriptor>>([descriptor]);
    }

    private sealed class Entries : ILaserficheEntryService
    {
        public List<int> FieldsRead { get; } = [];
        private readonly LFEntry[] documents = Enumerable.Range(1, 73).Select(i =>
            new LFEntry { Id = 500 + i, Name = i == 60 ? "100158" : $"ملف {i}",
                ParentId = i <= 50 ? 10 : 20, EntryType = LFEntryType.Document }).ToArray();
        public Task<LFEntry> GetEntryAsync(int entryId, CancellationToken cancellationToken = default) =>
            Task.FromResult(documents.Single(document => document.Id == entryId));
        public Task<IReadOnlyList<LFFieldValue>> GetEntryFieldsAsync(int entryId, CancellationToken cancellationToken = default)
        {
            FieldsRead.Add(entryId);
            return Task.FromResult<IReadOnlyList<LFFieldValue>>([
                new LFFieldValue { FieldName = "إ جراء   الوثيقة", Value = entryId <= 560 ? "تحت الإجراء" : "منتهي" },
                new LFFieldValue { FieldName = "حالة الوثيقة", Value = "تحت الإجراء" }
            ]);
        }
        public Task<int> GetRootEntryIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(1);
        public Task<IReadOnlyList<LFEntry>> GetAllFolderChildrenAsync(int entryId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LFEntry>>(entryId == 1 ? [
                new LFEntry { Id = 10, Name = "QA", ParentId = 1, EntryType = LFEntryType.Folder },
                new LFEntry { Id = 20, Name = "OTHER", ParentId = 1, EntryType = LFEntryType.Folder }
            ] : documents.Where(document => document.ParentId == entryId).ToArray());
        public Task<LFTemplate?> GetEntryTemplateAsync(int entryId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> GetEntryPathAsync(int entryId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PagedResult<LFEntry>> GetEntryChildrenAsync(int entryId, int page, int pageSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LFEntry>> GetFolderTreeAsync(int rootEntryId, int depth, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

