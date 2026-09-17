using System.Net;
using System.Net.Http.Headers;
using LaserficheReports.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace LaserficheReports.Infrastructure.Http;

/// <summary>
/// A <see cref="DelegatingHandler"/> that automatically attaches a Laserfiche Bearer token
/// to every outgoing HTTP request and handles 401 responses by invalidating the cached token
/// and retrying the request exactly once with a freshly acquired token.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a transient service via <c>AddHttpMessageHandler&lt;BearerTokenHandler&gt;()</c>.
/// The handler resolves the active repository from <see cref="IRepositoryContext"/> on each
/// request, which keeps it safe for singleton-scoped HttpClient message handler pipelines.
/// </para>
/// <para>
/// Token requests to the <c>/Token</c> endpoint itself are passed through without modification
/// to prevent authentication recursion.
/// </para>
/// </remarks>
internal sealed class BearerTokenHandler : DelegatingHandler
{
    private readonly ILaserficheAuthService _authService;
    private readonly IRepositoryContext _repositoryContext;
    private readonly ILogger<BearerTokenHandler> _logger;

    /// <summary>Initialises the handler with the auth service and repository context.</summary>
    public BearerTokenHandler(
        ILaserficheAuthService authService,
        IRepositoryContext repositoryContext,
        ILogger<BearerTokenHandler> logger)
    {
        _authService       = authService;
        _repositoryContext = repositoryContext;
        _logger            = logger;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Token endpoint calls must not go through this handler to avoid recursion.
        if (request.RequestUri?.AbsolutePath.EndsWith("/Token", StringComparison.OrdinalIgnoreCase) == true)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var repo  = await _repositoryContext.GetActiveRepositoryAsync(cancellationToken).ConfigureAwait(false);
        var token = await _authService.GetTokenAsync(repo, cancellationToken).ConfigureAwait(false);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _logger.LogWarning(
                "Received 401 from Laserfiche API for {Method} {Uri}. " +
                "Invalidating cached token and retrying once.",
                request.Method,
                request.RequestUri);

            await _authService.InvalidateTokenAsync(repo).ConfigureAwait(false);

            var freshToken = await _authService
                .GetTokenAsync(repo, cancellationToken)
                .ConfigureAwait(false);

            var retryRequest = await CloneRequestAsync(request, cancellationToken).ConfigureAwait(false);
            retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", freshToken);

            response.Dispose();
            response = await base.SendAsync(retryRequest, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    /// <summary>
    /// Creates a shallow clone of an <see cref="HttpRequestMessage"/> for the retry attempt.
    /// An <see cref="HttpRequestMessage"/> cannot be sent more than once, so a clone is required.
    /// </summary>
    private static async Task<HttpRequestMessage> CloneRequestAsync(
        HttpRequestMessage original,
        CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);

        foreach (var header in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (original.Content is not null)
        {
            var contentBytes = await original.Content
                .ReadAsByteArrayAsync(cancellationToken)
                .ConfigureAwait(false);

            clone.Content = new ByteArrayContent(contentBytes);

            foreach (var header in original.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }
}
