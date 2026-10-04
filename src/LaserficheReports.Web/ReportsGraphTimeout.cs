namespace LaserficheReports.Web;

internal static class ReportsGraphTimeout
{
    public const int DefaultSeconds = 4 * 60 * 60;

    public static int ResolveSeconds(IConfiguration configuration) =>
        Math.Clamp(configuration.GetValue<int?>("ReportsGraph:TimeoutSeconds") ?? DefaultSeconds,
            60, 24 * 60 * 60);
}
