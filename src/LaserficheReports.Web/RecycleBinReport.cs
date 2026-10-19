using System.Text;

namespace LaserficheReports.Web;

internal static class RecycleBinReport
{
    internal static void Validate(QueryPlan plan)
    {
        if (plan.Audit != null || plan.RequiresFilter || plan.Filters != null || plan.Field != null || plan.Value != null ||
            plan.Template != null || plan.FolderId != null || plan.FolderName != null || plan.Name != null ||
            plan.EntryIds?.Length > 0 || plan.From != null || plan.To != null || plan.Content || plan.Sort != null ||
            plan.GroupBy != null || plan.GroupFields?.Length > 0 || plan.Metrics?.Length > 0 || plan.SortField != null ||
            plan.AggregateSort != null || plan.Having != null || plan.Rollup != null || plan.RequireUnique || plan.IncludeSubfolders ||
            plan.EntryType is not ("document" or "folder" or "all") || plan.Page is < 1 or > 1000000 || plan.Limit is < 1 or > 200 ||
            plan.ResultType is not (null or "documents" or "count") || plan.ResultType == "count" && !plan.CountOnly ||
            plan.ResultType == "documents" && plan.CountOnly)
            throw new ArgumentException("تقرير السلة يدعم عرض العناصر الحالية أو عددها ونوعها وصفحات النتائج فقط؛ لا يستبدل شروطًا غير مدعومة ببحث عام.");
    }
    internal static ChatResult Build(RecycleBinSnapshot snapshot, string repositoryId, QueryPlan plan)
    {
        Validate(plan);
        if (snapshot.RepositoryId != repositoryId || !snapshot.Complete)
            throw new RecycleBinException("قراءة السلة لا تطابق المستودع الحالي أو لم تكتمل.");
        var selected = snapshot.Entries.Where(e => plan.EntryType == "all" || e.EntryType == plan.EntryType)
            .OrderByDescending(e => e.DeletedAt).ThenBy(e => e.EntryId).ToArray();
        var report = new StringBuilder("## العناصر الموجودة حاليًا في سلة المحذوفات\n\n");
        if (plan.CountOnly)
            report.AppendLine($"| عدد العناصر المطابقة في السلة |\n| --- |\n| {selected.Length} |");
        else
        {
            report.AppendLine("| رقم الإدخال | اسم العنصر | النوع | حذفه | تاريخ الحذف (الرياض) | عدد الصفحات |\n| --- | --- | --- | --- | --- | --- |");
            foreach (var row in selected.Skip((plan.Page - 1) * plan.Limit).Take(plan.Limit))
            {
                var deletedAt = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(row.DeletedAt, "Asia/Riyadh").ToString("yyyy-MM-dd HH:mm:ss");
                report.AppendLine($"| {row.EntryId} | {ReportSupport.Cell(row.Name)} | {(row.EntryType == "document" ? "وثيقة" : "مجلد")} | {ReportSupport.Cell(row.DeletedBy)} | {deletedAt} | {row.PageCount?.ToString() ?? "غير متوفر"} |");
            }
            report.AppendLine($"\n| إجمالي العناصر المطابقة | الصفحة | عدد الصفحات |\n| --- | --- | --- |\n| {selected.Length} | {plan.Page} | {Math.Max(1, (selected.Length + plan.Limit - 1) / plan.Limit)} |");
        }
        const string detail = "قراءة مباشرة للسلة بصلاحيات حساب Laserfiche الحالي. لا تشمل المحذوف نهائيًا، ولا تعني أن كل المستخدمين يرون القائمة نفسها. تعرض القائمة صفحة من نطاق القراءة الكامل، وقد تتغير السلة أثناء القراءة.";
        report.AppendLine("\n| نطاق التقرير |\n| --- |\n| " + detail + " |");
        return new ChatResult(report.ToString(), [], new AnswerScope("live-recycle-bin", repositoryId,
            selected.Count(e => e.EntryType == "document"), 0, true, detail, []));
    }
}
