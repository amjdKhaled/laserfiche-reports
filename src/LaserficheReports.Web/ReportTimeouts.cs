namespace LaserficheReports.Web;

internal static class ReportTimeouts
{
    internal static bool IsReportRequest(PathString path) =>
        path.StartsWithSegments("/api/reports/chat") ||
        path.StartsWithSegments("/api/reports/recycle-bin") ||
        path.StartsWithSegments("/api/reports/planning-catalog") ||
        path.StartsWithSegments("/api/reports/audit") ||
        path.StartsWithSegments("/api/audit-trail/test");
}
