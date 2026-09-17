namespace LaserficheReports.Infrastructure.Options;

internal sealed class SupabaseOptions
{
    public const string SectionName = "Supabase";

    public string PostgresConnectionString { get; init; } = string.Empty;
}
