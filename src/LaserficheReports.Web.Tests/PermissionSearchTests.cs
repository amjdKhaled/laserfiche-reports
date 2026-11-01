using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Common;
using LaserficheReports.Domain.Entities;
using LaserficheReports.Domain.Exceptions;
using LaserficheReports.Web.Permissions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LaserficheReports.Web.Tests;

public class PermissionSearchTests
{
    [Fact]
    public async Task EmptyGeneralSearchDoesNotPerformPrivilegedInspection()
    {
        var verifier = new Verifier();
        var result = await Service(verifier).CreateAsync("A", new("search"), [], default);
        Assert.Equal(0, verifier.Calls); Assert.Empty(result.Sources);
    }
    [Fact]
    public async Task ExactNamedLookupFallsBackButCombinedCriteriaDoNotProveAbsence()
    {
        var verifier = new Verifier(); var service = Service(verifier);
        var error = await Assert.ThrowsAsync<PermissionVerificationException>(() =>
            service.CreateAsync("A", new("search", Name: "hidden"), [], default));
        Assert.Equal(PermissionStatus.DocumentAccessDenied, error.Decision.Status);
        Assert.Equal("hidden", verifier.Target!.Name);
        verifier.Calls = 0;
        var result = await service.CreateAsync("A", new("search", Name: "different-name"), [19], default);
        Assert.Equal(0, verifier.Calls); Assert.Empty(result.Sources);
    }
    [Fact]
    public async Task FolderFailureUsesFolderTargetAndTechnicalFailureIsNeutral()
    {
        var verifier = new Verifier { Decision = new(PermissionStatus.VerificationUnavailable) };
        var error = await Assert.ThrowsAsync<PermissionVerificationException>(() =>
            Service(verifier).CreateAsync("A", new("folder_information", FolderId: 19), [], default));
        Assert.Equal("folder", verifier.Target!.Kind); Assert.Equal(19, verifier.Target.Id);
        Assert.Equal(PermissionDecision.Uncertain, error.Message); Assert.Equal(503, error.Decision.HttpStatusCode);
    }
    [Fact]
    public async Task MixedAvailabilityReportKeepsVisibleEntryAndNoDeniedLinksOrNames()
    {
        var verifier = new Verifier();
        var result = await EntryAvailabilityReport.CreateAsync(new Entries(), "A", new("availability", EntryIds: [1, 19]), [1,19], default, verifier);
        Assert.Contains("visible", result.Answer); Assert.Contains("لا توجد لديك صلاحية على هذه الوثيقة.", result.Answer);
        Assert.DoesNotContain("secret-native-diagnostic", result.Answer);
        Assert.Equal(new[] { 1 }, result.RelatedEntryIds); Assert.Empty(result.Sources);
    }
    private static LiveRepositoryReportService Service(Verifier verifier) =>
        new(new Entries(), new Searches(), new Definitions(), new Templates(), NullLogger<LiveRepositoryReportService>.Instance, permissions: verifier);
    private sealed class Verifier : ILaserfichePermissionVerifier
    {
        public int Calls; public PermissionTarget? Target;
        public PermissionDecision Decision = new(PermissionStatus.DocumentAccessDenied, "document");
        public Task<PermissionDecision> VerifyAsync(PermissionTarget target, CancellationToken ct)
        { Calls++; Target = target; return Task.FromResult(Decision); }
    }
    private sealed class Entries : ILaserficheEntryService
    {
        public Task<LFEntry> GetEntryAsync(int id, CancellationToken ct = default) => id == 1
            ? Task.FromResult(new LFEntry { Id = id, Name = "visible", EntryType = LFEntryType.Document })
            : throw new LaserficheException("secret-native-diagnostic", 404);
        public Task<IReadOnlyList<LFFieldValue>> GetEntryFieldsAsync(int id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LFFieldValue>>([]);
        public Task<LFTemplate?> GetEntryTemplateAsync(int id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetEntryPathAsync(int id, CancellationToken ct = default) => Task.FromResult("\\visible");
        public Task<int> GetRootEntryIdAsync(CancellationToken ct = default) => Task.FromResult(1);
        public Task<PagedResult<LFEntry>> GetEntryChildrenAsync(int id, int p, int size, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LFEntry>> GetAllFolderChildrenAsync(int id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LFEntry>> GetFolderTreeAsync(int id, int depth, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class Definitions : ILaserficheFieldDefinitionService
    { public Task<IReadOnlyDictionary<int, LFFieldDefinition>> GetFieldDefinitionsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyDictionary<int, LFFieldDefinition>>(new Dictionary<int, LFFieldDefinition>()); }
    private sealed class Templates : ILaserficheTemplateService
    { public Task<IReadOnlyList<LFTemplateDefinition>> GetTemplateDefinitionsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LFTemplateDefinition>>([]); }
    private sealed class Searches : ILaserficheSearchService
    {
        public Task<PagedResult<LFSearchResult>> QueryAsync(string e, int p, int size, string sort = "creationTime desc", string? field = null, bool readAll = false, CancellationToken cancellationToken = default, IReadOnlyList<string>? projectedFields = null) =>
            Task.FromResult(new PagedResult<LFSearchResult> { Items = [], TotalCount = 0, IsTotalCountExact = true, PageNumber = p, PageSize = size });
        public Task<PagedResult<LFSearchResult>> SimpleSearchAsync(string q, int p, int s, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PagedResult<LFSearchResult>> AdvancedSearchAsync(string q, int p, int s, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PagedResult<LFSearchResult>> SearchByTemplateAsync(string q, int p, int s, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PagedResult<LFSearchResult>> SearchByFieldAsync(string q, string v, int p, int s, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
