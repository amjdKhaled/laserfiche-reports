using System.Collections.Concurrent;
using LaserficheReports.Domain.Exceptions;

namespace LaserficheReports.Web.Permissions;

// Request-scoped: no decisions survive user/repository changes or leak into another request.
internal sealed class PostSearchAuthorizationFilter(IFullHierarchyAuthorizationService gateway,
    ILogger<PostSearchAuthorizationFilter>? logger = null)
{
    private int incomplete;
    internal bool IsIncomplete => Volatile.Read(ref incomplete) != 0;
    internal const string PartialNotice = "نتيجة جزئية: تعذر التحقق من بعض النتائج فاستُبعدت. جميع الأعداد والنسب أدناه تخص النتائج المتحقق منها فقط، ولا تمثل إجماليًا نهائيًا أو إثباتًا لعدم وجود نتائج أخرى. أعد تنفيذ السؤال بعد معالجة خدمة التحقق.";

    internal async Task<HashSet<int>> FilterAsync(IEnumerable<int> candidates,
        Func<int, CancellationToken, Task> verifyRequestedData, CancellationToken ct)
    {
        var allowed = new ConcurrentDictionary<int, byte>();
        await Parallel.ForEachAsync(candidates.Distinct(),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (id, token) =>
            {
                try
                {
                    if (!await gateway.CanReadAsync(id, token)) return;
                    await verifyRequestedData(id, token);
                    allowed.TryAdd(id, 0);
                }
                catch (PermissionVerificationException error) when (error.Decision.Status is
                    PermissionStatus.AncestorAccessDenied or PermissionStatus.DocumentAccessDenied or
                    PermissionStatus.FolderAccessDenied or PermissionStatus.BrowseOnly)
                {
                    // A definite denial of the requested operation excludes only this candidate.
                }
                catch (LaserficheException error) when (error.StatusCode == 403) { }
                catch (PermissionVerificationException error) when (error.Decision.Status is
                    PermissionStatus.Unknown or PermissionStatus.VerificationUnavailable or PermissionStatus.Ambiguous)
                {
                    MarkIncomplete(error);
                }
                catch (Exception error) when (error is HttpRequestException or IOException or TimeoutException ||
                    error is OperationCanceledException && !ct.IsCancellationRequested ||
                    error is LaserficheException lf && (lf.StatusCode == 404 || lf.StatusCode >= 500))
                {
                    MarkIncomplete(error);
                }
            });
        return allowed.Keys.ToHashSet();
    }

    private void MarkIncomplete(Exception error)
    {
        Interlocked.Exchange(ref incomplete, 1);
        gateway.MarkIncompleteVerification();
        logger?.LogWarning("Stage=AUTHORIZATION Status=Unknown ErrorType={ErrorType}", error.GetType().Name);
    }

    internal ChatResult PrepareReport(ChatResult result)
    {
        if (!IsIncomplete) return result;
        // An unavailable bridge must never turn a failed search into a successful empty report.
        if (gateway.AuthorizedEntryIds.Length == 0)
            throw new PermissionVerificationException(new(PermissionStatus.VerificationUnavailable));
        return result with
        {
            Answer = PartialNotice + "\n\n" + result.Answer,
            Scope = PartialScope(result.Scope),
            Reports = result.Reports.Select(section => section with
            {
                Answer = PartialNotice + "\n\n" + section.Answer,
                Scope = PartialScope(section.Scope)
            }).ToArray()
        };
    }

    private static AnswerScope? PartialScope(AnswerScope? scope) => scope is null ? null :
        scope with { Exhaustive = false, Detail = PartialNotice + " " + scope.Detail };
}
