using System.Collections.Concurrent;
using System.Diagnostics;
using LaserficheReports.Application.DTOs;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Entities;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using Microsoft.Extensions.Logging;

namespace LaserficheReports.Infrastructure.Services;

/// <summary>
/// Aggregates live Laserfiche repository statistics into a <see cref="RepositoryStatsDto"/>
/// by performing a recursive folder-tree scan.
///
/// Data sources:
///   • Laserfiche Repository API — folder children, entry timestamps/template assignment,
///     repository identity, and template definitions.
///   • Portal in-memory audit log — portal search activity only.
/// </summary>
internal sealed class LaserficheAnalyticsService : ILaserficheAnalyticsService
{
    private const int SearchActivityDays = 7;
    private const int TopSearchQueryLimit = 5;

    private readonly ILaserficheRepositoryService   _repositoryService;
    private readonly ILaserficheEntryService         _entryService;
    private readonly ILaserficheTemplateService      _templateService;
    private readonly ISearchAuditLog                 _auditLog;
    private readonly ICredentialProvider             _credentialProvider;
    private readonly IRepositoryContext              _repositoryContext;
    private readonly IHttpContextAccessor            _httpContextAccessor;
    private readonly ILogger<LaserficheAnalyticsService> _logger;

    public LaserficheAnalyticsService(
        ILaserficheRepositoryService   repositoryService,
        ILaserficheEntryService         entryService,
        ILaserficheTemplateService      templateService,
        ISearchAuditLog                 auditLog,
        ICredentialProvider             credentialProvider,
        IRepositoryContext              repositoryContext,
        IHttpContextAccessor            httpContextAccessor,
        ILogger<LaserficheAnalyticsService> logger)
    {
        _repositoryService   = repositoryService;
        _entryService        = entryService;
        _templateService     = templateService;
        _auditLog            = auditLog;
        _credentialProvider  = credentialProvider;
        _repositoryContext   = repositoryContext;
        _httpContextAccessor = httpContextAccessor;
        _logger              = logger;
    }

    /// <inheritdoc />
    public async Task<RepositoryStatsDto> GetRepositoryStatsAsync(
        CancellationToken cancellationToken = default)
    {
        var totalStart = Stopwatch.GetTimestamp();
        try
        {
            // ── 1. Verify connectivity ───────────────────────────────────────
            var status = await _repositoryService
                .TestConnectionAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!status.IsConnected)
            {
                return new RepositoryStatsDto
                {
                    IsConnected   = false,
                    ErrorMessage  = status.ErrorMessage,
                    LastCheckedAt = status.CheckedAt
                };
            }

            // ── 2. Connected user ────────────────────────────────────────────
            var principal = _httpContextAccessor.HttpContext?.User;
            var authMethod = principal?.FindFirst(ClaimTypes.AuthenticationMethod)?.Value;
            var isUserSession = principal?.Identity?.IsAuthenticated == true &&
                (string.Equals(authMethod, "LFDS", StringComparison.Ordinal) ||
                 string.Equals(authMethod, "RepositoryPassword", StringComparison.Ordinal));
            var authenticationMode = isUserSession ? authMethod! : "FallbackCredentials";

            string? connectedUser = principal?.Identity?.Name;
            string? serverUrl     = null;
            try
            {
                var repoDesc = await _repositoryContext
                    .GetActiveRepositoryAsync(cancellationToken)
                    .ConfigureAwait(false);
                serverUrl = repoDesc.ServerUrl;

                // Interactive API calls already use the per-user bearer token. Never read
                // or display the configured fallback account while such a session exists.
                if (!isUserSession)
                {
                    var creds = await _credentialProvider
                        .GetCredentialsAsync(repoDesc.Key, cancellationToken)
                        .ConfigureAwait(false);
                    connectedUser = creds.Username;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read username from credential store.");
            }

            // ── 3. Dynamic root discovery + template definitions ────────────
            var rootFetchStart = Stopwatch.GetTimestamp();

            var (rootChildren, templateDefs) = await FetchRootAndTemplatesAsync(cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Repository analytics scan starting. RepositoryId={RepositoryId}; AuthenticationMode={AuthenticationMode}; RootChildrenCount={RootChildrenCount}.",
                status.RepositoryId, authenticationMode, rootChildren.Count);
            _logger.LogInformation(
                "Repository statistics loaded for Username={Username}; RepositoryId={RepositoryId}.",
                connectedUser ?? "(not exposed by token)", status.RepositoryId);

            var rootFetchDurationMs = Stopwatch.GetElapsedTime(rootFetchStart).TotalMilliseconds;

            // Separate root-level documents from root-level folders.
            var rootDocEntries    = rootChildren.Where(e => e.EntryType == LFEntryType.Document).ToList();
            var rootFolderEntries = rootChildren.Where(e => e.EntryType == LFEntryType.Folder).ToList();
            var rootOtherEntries  = rootChildren.Where(e => e.EntryType == LFEntryType.Unknown).ToList();

            _logger.LogInformation(
                "DASHBOARD PIPELINE — Root children: total={Total} | folders={Folders} | documents={Docs} | unknown={Other}",
                rootChildren.Count, rootFolderEntries.Count, rootDocEntries.Count, rootOtherEntries.Count);

            if (rootChildren.Count == 0)
            {
                _logger.LogWarning(
                    "DASHBOARD DIAGNOSTIC — RepositoryId={RepositoryId}; API user returned 0 root children. " +
                    "If Laserfiche Web Client shows entries, verify that the API token belongs to the same user/repository and has Browse permission.",
                    status.RepositoryId);
            }

            // ── 4. Recursive folder scan ─────────────────────────────────────
            var scanStart = Stopwatch.GetTimestamp();

            var rootFolderResults = await ScanRootFoldersAsync(
                    rootFolderEntries,
                    (id, ct) => _entryService.GetAllFolderChildrenAsync(id, ct),
                    _logger,
                    cancellationToken)
                .ConfigureAwait(false);

            var scanDurationMs = (long)Stopwatch.GetElapsedTime(scanStart).TotalMilliseconds;

            _logger.LogInformation(
                "DASHBOARD PIPELINE — Recursive scan complete in {ScanMs}ms | root folders={RootFolders} | sub-folders={SubFolders} | sub-documents={SubDocs}",
                scanDurationMs, rootFolderEntries.Count,
                rootFolderResults.Sum(r => r.Folders),
                rootFolderResults.Sum(r => r.Documents));

            // ── 5. Build one authoritative document set ──────────────────────
            // Count and derive every document KPI from the same source list. De-duplicate
            // by Laserfiche Entry ID so an unexpected duplicate listing cannot inflate totals.
            var allDocs = rootDocEntries
                .Concat(rootFolderResults.SelectMany(r => r.AllDocs))
                .GroupBy(d => d.Id)
                .Select(g => g
                    .OrderByDescending(d => d.LastModifiedTime ?? d.CreationTime ?? DateTimeOffset.MinValue)
                    .First())
                .ToList()
                .AsReadOnly();

            var totalDocuments = allDocs.Count;
            var allFolders = rootFolderEntries
                .Concat(rootFolderResults.SelectMany(r => r.AllFolders))
                .GroupBy(f => f.Id)
                .Select(g => g.First())
                .ToList()
                .AsReadOnly();
            var totalFolders = allFolders.Count;

            // Template KPIs are derived directly from the authoritative document list.
            // TemplateId=0 is not an assigned template.
            var docsWithTemplate = allDocs.Count(HasTemplate);
            var docsWithoutTemplate = totalDocuments - docsWithTemplate;

            var templateStats = allDocs
                .Where(HasTemplate)
                .GroupBy(GetTemplateKey, StringComparer.OrdinalIgnoreCase)
                .Select(g => new TemplateStatDto { Name = g.Key, Count = g.Count() })
                .OrderByDescending(t => t.Count)
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()
                .AsReadOnly();

            _logger.LogInformation(
                "DASHBOARD PIPELINE — Final counts: totalFolders={TotalFolders} | totalDocuments={TotalDocs} | " +
                "docsWithTemplate={WithTemplate} | docsWithoutTemplate={WithoutTemplate} | templateNames={Templates}",
                totalFolders, totalDocuments, docsWithTemplate, docsWithoutTemplate,
                string.Join(", ", templateStats.Take(5).Select(t => $"{t.Name}({t.Count})")));

            // Root-folder distribution (for bar chart).
            var rootFolderStats = rootFolderResults
                .Zip(rootFolderEntries, (r, folder) => new RootFolderStatDto
                {
                    EntryId   = folder.Id,
                    Name      = r.Name,
                    Documents = r.Documents,
                    Folders   = r.Folders,
                    DocumentIds = r.AllDocs.Select(d => d.Id).Distinct().ToList().AsReadOnly()
                })
                .ToList()
                .AsReadOnly();

            // No arbitrary document cap: the scan already retrieved these entries, so
            // silently dropping rows would make reporting results disagree with totals.
            var allRecentDocs = allDocs
                .OrderByDescending(d => d.CreationTime ?? DateTimeOffset.MinValue)
                .ToList()
                .AsReadOnly();

            // Repository API exposes the latest-modified timestamp, not an event history.
            // Include a document only when the source timestamp is strictly later than its
            // creation timestamp (or creation time is unavailable). No guessed time threshold.
            var allModifiedDocs = allDocs
                .Where(HasPostCreationModification)
                .OrderByDescending(d => d.LastModifiedTime ?? DateTimeOffset.MinValue)
                .ToList()
                .AsReadOnly();

            var activityToday = DateTime.Today;
            var documentActivity = Enumerable.Range(0, 7)
                .Select(offset => activityToday.AddDays(offset - 6))
                .Select(day => new DocumentActivityDayDto
                {
                    Date = DateOnly.FromDateTime(day),
                    Created = allDocs.Count(entry => entry.CreationTime?.LocalDateTime.Date == day),
                    Modified = allModifiedDocs.Count(entry => entry.LastModifiedTime?.LocalDateTime.Date == day)
                })
                .ToList()
                .AsReadOnly();

            var userDocumentActivity = allDocs
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Creator))
                .GroupBy(entry => entry.Creator!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => new UserDocumentActivityDto
                {
                    Name = group.Key,
                    Created = group.Count(),
                    LastActivity = group.Max(entry => entry.LastModifiedTime ?? entry.CreationTime)
                })
                .OrderByDescending(item => item.Created)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()
                .AsReadOnly();

            // ── 6. Portal search audit log ───────────────────────────────────
            var (activityByDay, topQueries, totalSearches) = await FetchAuditDataAsync(cancellationToken)
                .ConfigureAwait(false);

            // ── 7. Build DTO ─────────────────────────────────────────────────
            var entryBreakdown = new Dictionary<string, int>
            {
                ["Document"] = totalDocuments,
                ["Folder"]   = totalFolders
            };

            var deptStats = rootFolderStats
                .Select(r => new DepartmentStatDto { Name = r.Name, DocumentCount = r.Documents })
                .ToList()
                .AsReadOnly();

            var totalLoadMs = (long)Stopwatch.GetElapsedTime(totalStart).TotalMilliseconds;

            _logger.LogInformation(
                "DASHBOARD LOAD — total={TotalMs}ms | root+templates={RootMs}ms | scan={ScanMs}ms | " +
                "docs={TotalDocs} | folders={TotalFolders} | templates={Templates} | authMode={AuthenticationMode}",
                totalLoadMs,
                (long)rootFetchDurationMs,
                scanDurationMs,
                totalDocuments,
                totalFolders,
                templateDefs.Count,
                authenticationMode);

            return new RepositoryStatsDto
            {
                IsConnected              = true,
                RepositoryId             = status.RepositoryId,
                RepositoryName           = status.RepositoryName,
                ServerVersion            = status.ServerVersion,
                ServerUrl                = serverUrl,
                ConnectedUser            = connectedUser,
                AuthenticationMode       = authenticationMode,
                TotalDocuments           = totalDocuments,
                TotalFolders             = totalFolders,
                TotalTemplates           = templateDefs.Count,
                DocsWithTemplate         = docsWithTemplate,
                DocsWithoutTemplate      = docsWithoutTemplate,
                TemplateStats            = templateStats,
                RootFolders              = rootFolderStats,
                TemplateDefinitions      = templateDefs,
                RecentDocs               = allRecentDocs,
                ModifiedDocs             = allModifiedDocs,
                AllDocs                  = allDocs,
                AllFolders               = allFolders,
                DocumentActivityByDay    = documentActivity,
                UserDocumentActivity     = userDocumentActivity,
                SearchActivityByDay      = activityByDay,
                TopSearchedQueries       = topQueries,
                TotalSearches            = totalSearches,
                ScanDurationMs           = scanDurationMs,
                LastCheckedAt            = DateTimeOffset.UtcNow,
                // Legacy compat fields
                EntryTypeBreakdown       = entryBreakdown,
                DepartmentCount          = rootFolderStats.Count,
                DocumentsByDepartment    = deptStats,
                RecentDocuments          = allRecentDocs,
                RecentlyIndexedDocuments = allRecentDocs,
                RecentEntries            = allDocs
                    .OrderByDescending(d => d.LastModifiedTime ?? d.CreationTime ?? DateTimeOffset.MinValue)
                    .Take(10)
                    .ToList()
                    .AsReadOnly()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Repository statistics aggregation failed.");
            return new RepositoryStatsDto
            {
                IsConnected   = false,
                ErrorMessage  = $"Failed to retrieve repository data: {ex.Message}",
                LastCheckedAt = DateTimeOffset.UtcNow
            };
        }
    }

    // ── Private: dynamic root + templates ─────────────────────────────────

    private async Task<(IReadOnlyList<LFEntry> rootChildren, IReadOnlyList<LFTemplateDefinition> templateDefs)>
        FetchRootAndTemplatesAsync(CancellationToken ct)
    {
        // Template definitions can be fetched while the authoritative repository root
        // is discovered. Never assume that the root entry ID is 1.
        var templateTask = _templateService.GetTemplateDefinitionsAsync(ct);
        var rootId = await _entryService.GetRootEntryIdAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Discovered repository root entry ID={RootId}. Fetching root children.", rootId);

        var rootTask = SafeGetAllFolderChildrenAsync(rootId, ct);
        await Task.WhenAll(rootTask, templateTask).ConfigureAwait(false);

        var root  = await rootTask.ConfigureAwait(false);
        var tmpls = await templateTask.ConfigureAwait(false);

        _logger.LogInformation(
            "Root children (ID={RootId}): {RootCount} entries (docs={Docs}, folders={Folders}, other={Other}). Templates defined: {TmplCount}.",
            rootId, root.Count,
            root.Count(e => e.EntryType == LFEntryType.Document),
            root.Count(e => e.EntryType == LFEntryType.Folder),
            root.Count(e => e.EntryType == LFEntryType.Unknown),
            tmpls.Count);

        return (root, tmpls);
    }

    private async Task<IReadOnlyList<LFEntry>> SafeGetAllFolderChildrenAsync(int entryId, CancellationToken ct)
    {
        try
        {
            return await _entryService.GetAllFolderChildrenAsync(entryId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SafeGetAllFolderChildrenAsync: unhandled exception for entry {EntryId}.", entryId);
            throw;
        }
    }

    // ── Private: parallel scan of each root-level folder ─────────────────

    internal static async Task<IReadOnlyList<ScanResult>> ScanRootFoldersAsync(
        IEnumerable<LFEntry> rootFolders,
        Func<int, CancellationToken, Task<IReadOnlyList<LFEntry>>> loadChildren,
        ILogger logger,
        CancellationToken    ct)
    {
        var folderList = rootFolders.ToList();
        // Limit actual HTTP work, not entire recursive branches (which deadlocks
        // when parents hold permits while awaiting their children).
        using var requests = new SemaphoreSlim(4, 4);
        async Task<IReadOnlyList<LFEntry>> LoadBounded(int id, CancellationToken token)
        {
            await requests.WaitAsync(token).ConfigureAwait(false);
            try { return await loadChildren(id, token).ConfigureAwait(false); }
            finally { requests.Release(); }
        }
        logger.LogInformation("Starting recursive scan of {Count} root-level folders.", folderList.Count);

        var tasks = folderList.Select(async folder =>
        {
            try
            {
                var result = await ScanFolderAsync(
                        folder.Id,
                        folder.Name,
                        new ConcurrentDictionary<int, byte>(),
                        LoadBounded,
                        logger,
                        ct)
                    .ConfigureAwait(false);
                return result with { Name = folder.Name };
            }
            catch (Exception ex)
            {
                // Do not silently convert a failed subtree into zero documents: that would
                // make the reports look valid while reporting incomplete data.
                logger.LogError(ex,
                    "Root folder scan failed for folder {FolderId} '{Name}'.",
                    folder.Id, folder.Name);
                throw;
            }
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        logger.LogInformation(
            "Root folder scan complete: {Docs} docs, {Folders} folders across {Count} root folders.",
            results.Sum(r => r.Documents),
            results.Sum(r => r.Folders),
            results.Length);

        return results;
    }

    // ── Private: recursive folder scanner ────────────────────────────────

    private static async Task<ScanResult> ScanFolderAsync(
        int                              folderId,
        string                           folderName,
        ConcurrentDictionary<int, byte> visited,
        Func<int, CancellationToken, Task<IReadOnlyList<LFEntry>>> loadChildren,
        ILogger                           logger,
        CancellationToken                 ct)
    {
        if (!visited.TryAdd(folderId, 0))
        {
            logger.LogDebug("Cycle detected — skipping already-visited folder {FolderId}.", folderId);
            return new ScanResult(folderName, 0, 0, [], [], []);
        }

        IReadOnlyList<LFEntry> children;
        try
        {
            children = await loadChildren(folderId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Missing one folder means all repository totals would be incomplete. Surface
            // the error instead of pretending the inaccessible folder contains zero items.
            logger.LogError(ex,
                "Cannot list children of folder {FolderId} '{Name}'. Repository analytics scan is incomplete.",
                folderId, folderName);
            throw;
        }

        var docEntries       = children.Where(e => e.EntryType == LFEntryType.Document).ToList();
        var subFolderEntries = children.Where(e => e.EntryType == LFEntryType.Folder).ToList();

        // Keep template counts in ScanResult for compatibility/tests. Analytics-level
        // template KPIs are calculated from the authoritative de-duplicated document list.
        var localTmpl = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in docEntries.Where(HasTemplate))
        {
            var templateKey = GetTemplateKey(doc);
            localTmpl[templateKey] = localTmpl.GetValueOrDefault(templateKey) + 1;
        }

        logger.LogInformation(
            "Repository folder scanned. FolderId={FolderId}; FolderName={FolderName}; DirectDocuments={Documents}; DirectFolders={Folders}.",
            folderId, folderName, docEntries.Count, subFolderEntries.Count);

        var subTasks = subFolderEntries.Select(f =>
            ScanFolderAsync(f.Id, f.Name, visited, loadChildren, logger, ct));

        var subResults = await Task.WhenAll(subTasks).ConfigureAwait(false);

        foreach (var sub in subResults)
        {
            foreach (var (tmpl, cnt) in sub.TemplateCounts)
                localTmpl[tmpl] = localTmpl.GetValueOrDefault(tmpl) + cnt;
        }

        var allSubDocs = subResults.SelectMany(r => r.AllDocs).ToList();
        var allDocs = docEntries
            .Concat(allSubDocs)
            .GroupBy(d => d.Id)
            .Select(g => g.First())
            .ToList();

        var documents = allDocs.Count;
        var allFolders = subFolderEntries
            .Concat(subResults.SelectMany(r => r.AllFolders))
            .GroupBy(f => f.Id)
            .Select(g => g.First())
            .ToList();
        var folders = allFolders.Count;

        return new ScanResult(folderName, documents, folders, localTmpl, allDocs, allFolders);
    }

    private static bool HasTemplate(LFEntry entry) =>
        entry.EntryType == LFEntryType.Document &&
        (entry.TemplateId is > 0 || !string.IsNullOrWhiteSpace(entry.TemplateName));

    private static string GetTemplateKey(LFEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.TemplateName)
            ? entry.TemplateName.Trim()
            : $"Template #{entry.TemplateId}";

    /// <summary>
    /// Repository API exposes the current last-modified timestamp, not the number of
    /// modification events. This helper therefore identifies documents whose source
    /// LastModifiedTime is strictly later than CreationTime without inventing a grace period.
    /// </summary>
    private static bool HasPostCreationModification(LFEntry entry)
    {
        if (!entry.LastModifiedTime.HasValue)
            return false;

        if (!entry.CreationTime.HasValue)
            return true;

        return entry.LastModifiedTime.Value > entry.CreationTime.Value;
    }

    // ── Private: portal search audit log data ─────────────────────────────

    private async Task<(IReadOnlyList<SearchActivityDayDto>, IReadOnlyList<TopQueryDto>, int)>
        FetchAuditDataAsync(CancellationToken ct)
    {
        var repo = await _repositoryContext.GetActiveRepositoryAsync(ct).ConfigureAwait(false);

        var actTask = _auditLog.GetSearchesByDayAsync(repo.RepositoryId, SearchActivityDays, ct);
        var topTask = _auditLog.GetTopQueriesAsync(repo.RepositoryId, TopSearchQueryLimit, ct);
        var cntTask = _auditLog.GetTotalSearchCountAsync(repo.RepositoryId, ct);
        await Task.WhenAll(actTask, topTask, cntTask).ConfigureAwait(false);
        return (await actTask, await topTask, await cntTask);
    }

    internal sealed record ScanResult(
        string                     Name,
        int                        Documents,
        int                        Folders,
        Dictionary<string, int>    TemplateCounts,
        List<LFEntry>              AllDocs,
        List<LFEntry>              AllFolders);
}
