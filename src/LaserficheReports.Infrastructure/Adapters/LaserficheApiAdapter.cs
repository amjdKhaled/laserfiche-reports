using LaserficheReports.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace LaserficheReports.Infrastructure.Adapters;

/// <summary>
/// Builds Laserfiche Repository API endpoint URLs from live configuration.
/// </summary>
public sealed class LaserficheApiAdapter : ILaserficheApiAdapter
{
    private readonly IOptionsMonitor<LaserficheOptions> _optionsMonitor;

    public LaserficheApiAdapter(IOptionsMonitor<LaserficheOptions> optionsMonitor)
    {
        _optionsMonitor = optionsMonitor;
    }

    public string ApiVersion => _optionsMonitor.CurrentValue.EffectiveApiVersion;

    private string RepoBase(string repositoryId) =>
        $"{BuildApiBase(_optionsMonitor.CurrentValue.ServerUrl)}/Repositories/{EncodeRepositoryId(repositoryId)}";

    private string ApiBase() => BuildApiBase(_optionsMonitor.CurrentValue.ServerUrl);

    public string BuildRepositoriesUrl() => $"{ApiBase()}/Repositories";

    public string BuildRepositoriesUrlFor(string serverUrl) =>
        $"{BuildApiBase(serverUrl)}/Repositories";

    public string BuildTokenUrl(string repositoryId) =>
        $"{RepoBase(repositoryId)}/Token";

    public string BuildEntryUrl(string repositoryId, int entryId, EntryResource resource) =>
        resource switch
        {
            EntryResource.Details  => $"{RepoBase(repositoryId)}/Entries/{entryId}",
            EntryResource.Fields   => $"{RepoBase(repositoryId)}/Entries/{entryId}/fields?formatValue=false",
            EntryResource.Tags     => $"{RepoBase(repositoryId)}/Entries/{entryId}/tags",
            EntryResource.Children => $"{RepoBase(repositoryId)}/Entries/{entryId}/children",
            EntryResource.FolderChildren => BuildFolderChildrenUrl(repositoryId, entryId),
            EntryResource.Edoc     => ApiVersion.Equals("v2", StringComparison.OrdinalIgnoreCase)
                ? $"{RepoBase(repositoryId)}/Entries/{entryId}/Document/Edoc"
                : $"{RepoBase(repositoryId)}/Entries/{entryId}/Laserfiche.Repository.Document/edoc",
            EntryResource.Pages    => ApiVersion.Equals("v2", StringComparison.OrdinalIgnoreCase)
                ? $"{RepoBase(repositoryId)}/Entries/{entryId}/Document/Pages"
                : $"{RepoBase(repositoryId)}/Entries/{entryId}/pages",
            _ => throw new ArgumentOutOfRangeException(nameof(resource), resource, "Unknown entry resource.")
        };

    public string BuildPageImageUrl(string repositoryId, int entryId, int pageNumber) =>
        ApiVersion.Equals("v2", StringComparison.OrdinalIgnoreCase)
            ? $"{RepoBase(repositoryId)}/Entries/{entryId}/Document/Pages/{pageNumber}/Image"
            : $"{RepoBase(repositoryId)}/Entries/{entryId}/pages/{pageNumber}/image";

    public string BuildPageTextUrl(string repositoryId, int entryId, int pageNumber) =>
        $"{RepoBase(repositoryId)}/Entries/{entryId}/Document/Pages/{pageNumber}/Text";

    public string BuildDocumentExportUrl(string repositoryId, int entryId, string? pageRange = null)
    {
        var url = $"{RepoBase(repositoryId)}/Entries/{entryId}/Export";
        return string.IsNullOrWhiteSpace(pageRange)
            ? url
            : $"{url}?pageRange={Uri.EscapeDataString(pageRange)}";
    }

    public string BuildSearchUrl(string repositoryId, SearchType searchType) =>
        searchType switch
        {
            SearchType.Simple   => $"{RepoBase(repositoryId)}/SimpleSearches",
            SearchType.Advanced => ApiVersion.Equals("v2", StringComparison.OrdinalIgnoreCase)
                ? $"{RepoBase(repositoryId)}/Searches/SearchAsync"
                : $"{RepoBase(repositoryId)}/Searches",
            _ => throw new ArgumentOutOfRangeException(nameof(searchType), searchType, "Unknown search type.")
        };

    public string BuildTaskStatusUrl(string repositoryId, string operationToken) =>
        ApiVersion.Equals("v2", StringComparison.OrdinalIgnoreCase)
            ? $"{RepoBase(repositoryId)}/Tasks?taskIds={Uri.EscapeDataString(operationToken)}"
            : $"{RepoBase(repositoryId)}/Searches/{Uri.EscapeDataString(operationToken)}";

    public string BuildSearchResultsUrl(string repositoryId, string operationToken) =>
        $"{RepoBase(repositoryId)}/Searches/{Uri.EscapeDataString(operationToken)}/Results";

    public string BuildTokenUrlFor(string serverUrl, string repositoryId) =>
        $"{BuildApiBase(serverUrl)}/Repositories/{EncodeRepositoryId(repositoryId)}/Token";

    /// <summary>
    /// Version-aware folder children URL. V2 and V1 use different route shapes.
    /// </summary>
    public string BuildFolderChildrenUrl(string repositoryId, int entryId)
    {
        var repoBase = RepoBase(repositoryId);

        if (ApiVersion.Equals("v2", StringComparison.OrdinalIgnoreCase))
            return $"{repoBase}/Entries/{entryId}/Folder/Children?groupByEntryType=false&formatFieldValues=false";

        return $"{repoBase}/Entries/{entryId}/Laserfiche.Repository.Folder/children";
    }

    public string BuildTemplateDefinitionsUrl(string repositoryId) =>
        $"{RepoBase(repositoryId)}/TemplateDefinitions";

    public string BuildFieldDefinitionsUrl(string repositoryId) =>
        $"{RepoBase(repositoryId)}/FieldDefinitions";

    public string BuildEntryByPathUrl(string repositoryId, string fullPath) =>
        $"{RepoBase(repositoryId)}/Entries/ByPath?fullPath={Uri.EscapeDataString(fullPath)}";

    public int GetConfiguredRootEntryId() => _optionsMonitor.CurrentValue.RootEntryId;

    public string BuildTokenUrlV2(string repositoryId)
    {
        var options = _optionsMonitor.CurrentValue;
        var root = options.ServerUrl.TrimEnd('/');
        var basePath = "/" + options.ApiBasePath.Trim('/');

        if (root.EndsWith(basePath, StringComparison.OrdinalIgnoreCase))
            root = root[..^basePath.Length].TrimEnd('/');

        return $"{root}{basePath}/v2/Repositories/{EncodeRepositoryId(repositoryId)}/Token";
    }

    private string BuildApiBase(string serverUrl)
    {
        var options = _optionsMonitor.CurrentValue;
        var root = serverUrl.TrimEnd('/');
        var basePath = "/" + options.ApiBasePath.Trim('/');

        if (root.EndsWith(basePath, StringComparison.OrdinalIgnoreCase))
            root = root[..^basePath.Length].TrimEnd('/');

        return $"{root}{basePath}/{options.EffectiveApiVersion.Trim('/')}";
    }

    private static string EncodeRepositoryId(string repositoryId)
    {
        if (string.IsNullOrWhiteSpace(repositoryId))
            throw new ArgumentException("Repository ID cannot be empty.", nameof(repositoryId));

        return Uri.EscapeDataString(repositoryId.Trim());
    }
}
