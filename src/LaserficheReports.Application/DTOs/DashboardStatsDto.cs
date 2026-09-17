using LaserficheReports.Domain.Entities;

namespace LaserficheReports.Application.DTOs;

/// <summary>
/// Aggregated live statistics returned by <see cref="Interfaces.ILaserficheDashboardService"/>.
/// Repository facts come from the active Laserfiche Repository API session; portal search
/// metrics come from the portal's own in-memory search audit log.
/// </summary>
public sealed record DashboardStatsDto
{
    // ── Connectivity ───────────────────────────────────────────────────────
    public bool IsConnected { get; init; }
    public bool IsLive => IsConnected;
    public string? ErrorMessage { get; init; }

    // ── Repository identity ────────────────────────────────────────────────
    public string? RepositoryId { get; init; }
    public string? RepositoryName { get; init; }
    public string? ServerVersion { get; init; }
    public string? ServerUrl { get; init; }
    public string? ConnectedUser { get; init; }

    /// <summary>Effective authentication method used for the active repository session.</summary>
    public string AuthenticationMode { get; init; } = "FallbackCredentials";

    // ── Entry counts ───────────────────────────────────────────────────────
    /// <summary>Total unique document entries found by the complete recursive scan.</summary>
    public int TotalDocuments { get; init; }

    /// <summary>Total folders found below the repository root (the root itself is excluded).</summary>
    public int TotalFolders { get; init; }

    /// <summary>Total unique template definitions returned across all API pages.</summary>
    public int TotalTemplates { get; init; }

    /// <summary>Documents with TemplateId &gt; 0 or a non-empty TemplateName.</summary>
    public int DocsWithTemplate { get; init; }

    /// <summary>Documents without an assigned template.</summary>
    public int DocsWithoutTemplate { get; init; }

    // ── Template / folder distributions ───────────────────────────────────
    public IReadOnlyList<TemplateStatDto> TemplateStats { get; init; } = [];
    public IReadOnlyList<RootFolderStatDto> RootFolders { get; init; } = [];

    /// <summary>All template definitions returned by the repository.</summary>
    public IReadOnlyList<LFTemplateDefinition> TemplateDefinitions { get; init; } = [];

    // ── Document source rows ───────────────────────────────────────────────
    /// <summary>All discovered documents sorted newest-created first; no hidden fixed cap.</summary>
    public IReadOnlyList<LFEntry> RecentDocs { get; init; } = [];

    /// <summary>
    /// All discovered documents whose Repository API LastModifiedTime is later than
    /// CreationTime, sorted by latest modification. This is a document snapshot, not an
    /// Audit Trail event count.
    /// </summary>
    public IReadOnlyList<LFEntry> ModifiedDocs { get; init; } = [];

    /// <summary>All unique documents discovered during the recursive scan.</summary>
    public IReadOnlyList<LFEntry> AllDocs { get; init; } = [];

    /// <summary>All folders discovered below the repository root.</summary>
    public IReadOnlyList<LFEntry> AllFolders { get; init; } = [];

    /// <summary>Compact exact document activity totals used after source entries are released.</summary>
    public IReadOnlyList<DocumentActivityDayDto> DocumentActivityByDay { get; init; } = [];

    /// <summary>Compact exact per-creator totals used after source entries are released.</summary>
    public IReadOnlyList<UserDocumentActivityDto> UserDocumentActivity { get; init; } = [];

    // ── Portal search activity ─────────────────────────────────────────────
    /// <summary>Portal search counts for the last 7 days; not Laserfiche Audit Trail events.</summary>
    public IReadOnlyList<SearchActivityDayDto> SearchActivityByDay { get; init; } = [];

    /// <summary>Top portal search queries.</summary>
    public IReadOnlyList<TopQueryDto> TopSearchedQueries { get; init; } = [];

    public int TotalSearches { get; init; }

    // ── Health / timing ────────────────────────────────────────────────────
    public long ScanDurationMs { get; init; }
    public DateTimeOffset? LastCheckedAt { get; init; }

    // ── Legacy / compatibility fields ─────────────────────────────────────
    public int TotalEntries => TotalDocuments + TotalFolders;
    public TimeSpan? AvgSearchResponseTime { get; init; }

    public IReadOnlyDictionary<string, int> EntryTypeBreakdown { get; init; }
        = new Dictionary<string, int>();

    public IReadOnlyList<DepartmentStatDto> DocumentsByDepartment { get; init; } = [];
    public int DepartmentCount { get; init; }
    public IReadOnlyList<LFEntry> RecentDocuments { get; init; } = [];
    public IReadOnlyList<LFEntry> RecentlyIndexedDocuments { get; init; } = [];
    public IReadOnlyList<LFEntry> RecentEntries { get; init; } = [];
}

public sealed record DocumentActivityDayDto
{
    public DateOnly Date { get; init; }
    public int Created { get; init; }
    public int Modified { get; init; }
}

public sealed record UserDocumentActivityDto
{
    public string Name { get; init; } = string.Empty;
    public int Created { get; init; }
    public DateTimeOffset? LastActivity { get; init; }
}
