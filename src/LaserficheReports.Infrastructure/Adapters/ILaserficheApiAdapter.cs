namespace LaserficheReports.Infrastructure.Adapters;

/// <summary>
/// Builds absolute URLs for every Laserfiche Repository API endpoint used by the portal.
/// URL construction is centralised here so server, virtual directory, repository ID,
/// and API-version handling remain consistent.
/// </summary>
public interface ILaserficheApiAdapter
{
    /// <summary>The effective API version this adapter targets, e.g. <c>v1</c> or <c>v2</c>.</summary>
    string ApiVersion { get; }

    /// <summary>Builds the repository discovery endpoint.</summary>
    string BuildRepositoriesUrl();

    /// <summary>Builds the repository discovery endpoint for an explicitly supplied server URL.</summary>
    string BuildRepositoriesUrlFor(string serverUrl);

    /// <summary>Builds the active-version password token endpoint.</summary>
    string BuildTokenUrl(string repositoryId);

    /// <summary>Builds a URL for an entry resource.</summary>
    string BuildEntryUrl(string repositoryId, int entryId, EntryResource resource);

    /// <summary>Builds the URL for a single document page image.</summary>
    string BuildPageImageUrl(string repositoryId, int entryId, int pageNumber);

    /// <summary>Builds the V2 URL for the searchable text of a single document page.</summary>
    string BuildPageTextUrl(string repositoryId, int entryId, int pageNumber);

    /// <summary>Builds the V2 simple-export URL for one or more document pages.</summary>
    string BuildDocumentExportUrl(string repositoryId, int entryId, string? pageRange = null);

    /// <summary>Builds a simple or advanced search endpoint URL.</summary>
    string BuildSearchUrl(string repositoryId, SearchType searchType);

    /// <summary>Builds the task-status URL for an async operation token.</summary>
    string BuildTaskStatusUrl(string repositoryId, string operationToken);

    /// <summary>Builds the search-results URL for an async operation token.</summary>
    string BuildSearchResultsUrl(string repositoryId, string operationToken);

    /// <summary>
    /// Builds a token URL using an explicitly supplied server URL. Repository identifiers
    /// are encoded as path segments just like every other repository-scoped URL.
    /// </summary>
    string BuildTokenUrlFor(string serverUrl, string repositoryId);

    /// <summary>
    /// Builds the version-aware folder-children URL.
    /// V2 uses <c>/Entries/{id}/Folder/Children</c>; V1 uses the OData typed-cast path.
    /// Pagination is consumed by the entry service from server-provided continuation links.
    /// </summary>
    string BuildFolderChildrenUrl(string repositoryId, int entryId);

    /// <summary>Builds the template-definitions endpoint URL.</summary>
    string BuildTemplateDefinitionsUrl(string repositoryId);

    /// <summary>Builds the repository-wide field-definitions endpoint URL.</summary>
    string BuildFieldDefinitionsUrl(string repositoryId);

    /// <summary>
    /// Builds the ByPath lookup endpoint. Passing <c>\</c> resolves the repository root.
    /// </summary>
    string BuildEntryByPathUrl(string repositoryId, string fullPath);

    /// <summary>
    /// Returns the optional administrator-configured root Entry ID fallback.
    /// Zero means no configured fallback; callers should dynamically discover the root first.
    /// </summary>
    int GetConfiguredRootEntryId();

    /// <summary>
    /// Builds the V2 token URL used for LFDS authorization-code exchange regardless of
    /// the resource API version used elsewhere.
    /// </summary>
    string BuildTokenUrlV2(string repositoryId);
}
