namespace LaserficheReports.Infrastructure.Adapters;

/// <summary>
/// Identifies which Laserfiche search API endpoint to invoke.
/// Passed to <see cref="ILaserficheApiAdapter.BuildSearchUrl"/> to centralise URL construction.
/// </summary>
public enum SearchType
{
    /// <summary>
    /// Keyword/phrase search via <c>POST /SimpleSearches</c>.
    /// Matches entry names and full-text content where FTI is enabled.
    /// </summary>
    Simple,

    /// <summary>
    /// Expression-based search via <c>POST /Entries/Search</c>.
    /// Accepts full Laserfiche search expression syntax.
    /// </summary>
    Advanced
}
