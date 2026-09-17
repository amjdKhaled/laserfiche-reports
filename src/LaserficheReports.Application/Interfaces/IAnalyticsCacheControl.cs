namespace LaserficheReports.Application.Interfaces;

/// <summary>Allows an explicit user refresh to invalidate only that user's repository snapshot.</summary>
public interface IAnalyticsCacheControl
{
    Task InvalidateAsync(CancellationToken cancellationToken = default);
}
