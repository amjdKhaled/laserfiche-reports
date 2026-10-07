namespace LaserficheReports.Web;

/// <summary>Carry the existing request scope into the local graph's timing logs.</summary>
internal sealed class GraphCorrelationHandler(IHttpContextAccessor context) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (context.HttpContext is { } current)
        {
            request.Headers.Remove("X-Request-ID");
            request.Headers.TryAddWithoutValidation("X-Request-ID", current.TraceIdentifier);
        }
        return base.SendAsync(request, ct);
    }
}
