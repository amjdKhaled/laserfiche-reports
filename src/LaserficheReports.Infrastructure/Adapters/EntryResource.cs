namespace LaserficheReports.Infrastructure.Adapters;

/// <summary>
/// Identifies a sub-resource of a Laserfiche entry used to construct API endpoint URLs.
/// Passed to <see cref="ILaserficheApiAdapter.BuildEntryUrl"/> so URL construction is
/// centralised in the adapter rather than scattered across services.
/// </summary>
public enum EntryResource
{
    /// <summary>Entry detail resource: <c>GET /Entries/{id}</c></summary>
    Details,

    /// <summary>
    /// Entry metadata fields:
    /// <c>GET /Entries/{id}/fields?formatValue=false</c>
    /// </summary>
    Fields,

    /// <summary>Entry tags: <c>GET /Entries/{id}/tags</c></summary>
    Tags,

    /// <summary>Direct children of a folder entry: <c>GET /Entries/{id}/children</c></summary>
    Children,

    /// <summary>
    /// OData-typed folder-children path confirmed in Swagger:
    /// Version-aware folder-children endpoint.
    /// V2: <c>GET /Entries/{id}/Folder/Children</c>.
    /// V1: <c>GET /Entries/{id}/Laserfiche.Repository.Folder/children</c>.
    /// </summary>
    FolderChildren,

    /// <summary>
    /// Confirmed electronic document download:
    /// <c>GET /Entries/{id}/Laserfiche.Repository.Document/edoc</c>
    /// </summary>
    Edoc,

    /// <summary>Document page metadata: <c>GET /Entries/{id}/pages</c></summary>
    Pages
}
