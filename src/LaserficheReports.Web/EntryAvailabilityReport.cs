using System.Text;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Exceptions;
using LaserficheReports.Web.Permissions;

namespace LaserficheReports.Web;

internal sealed record AvailabilityRequest(int[]? EntryIds);

internal static class EntryAvailabilityReport
{
    internal static async Task<ChatResult> CreateAsync(ILaserficheEntryService entries, string repositoryId,
        QueryPlan plan, IReadOnlyList<int> ids, CancellationToken ct, ILaserfichePermissionVerifier? permissions = null)
    {
        plan.ValidateIntent();
        if (ids.Count is < 1 or > 50 || ids.Any(id => id <= 0) || !ids.SequenceEqual(plan.EntryIds ?? []))
            throw new ArgumentException("حدد حتى 50 رقم إدخال تريد فحص إتاحتها.");
        var requested = ids.Distinct().ToArray();
        var rows = new string[requested.Length];
        var visible = new bool[requested.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, requested.Length),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (index, token) =>
        {
            var id = requested[index];
            try
            {
                var entry = await entries.GetEntryAsync(id, token);
                visible[index] = true;
                rows[index] = $"| {id} | {ReportSupport.Cell(entry.Name)} | متاح | أمكن قراءة بيانات الإدخال بالحساب الحالي؛ لم يُفحص الملف الفعلي في وحدة التخزين |";
            }
            catch (LaserficheException error) when (error.StatusCode == 403)
            {
                if (permissions != null)
                {
                    var decision = await permissions.VerifyAsync(new(Id: id, OperationForbidden: true), token);
                    rows[index] = $"| {id} | — | {decision.Message} | — |";
                    return;
                }
                rows[index] = $"| {id} | غير متاح | رفض الصلاحية (403) | الخادم رفض القراءة؛ لا يثبت أن الإدخال محذوف أو مفقود |";
            }
            catch (LaserficheException error) when (error.StatusCode == 404)
            {
                if (permissions != null)
                {
                    var decision = await permissions.VerifyAsync(new(Id: id), token);
                    rows[index] = $"| {id} | — | {decision.Message} | — |";
                    return;
                }
                rows[index] = $"| {id} | غير متاح | تعذر العثور عليه أو إظهاره (404) | لا يمكن تمييز الحذف من إخفاء الصلاحيات بهذا الرد؛ يلزم فحص السلة أو التدقيق |";
            }
            // Authentication, network and server failures invalidate the report;
            // they must never be converted into rows asserting missing entries.
        });
        var report = new StringBuilder("| رقم الإدخال | الاسم | الحالة الحالية | حدود النتيجة |\n| --- | --- | --- | --- |\n");
        foreach (var row in rows) report.AppendLine(row);
        return new ChatResult(report.ToString(), [], new AnswerScope("live-availability", repositoryId,
            visible.Count(value => value), 0, false, "فحص الأرقام المطلوبة بالحساب الحالي فقط؛ ليس حصرًا للمحذوفات أو فحصًا لوحدات التخزين.", requested))
        { RelatedEntryIds = requested.Where((_, index) => visible[index]).ToArray() };
    }
}
