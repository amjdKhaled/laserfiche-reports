using System.Collections.Concurrent;
using System.Net;

namespace LaserficheReports.Infrastructure.Http;

/// <summary>Bounded retries for idempotent reads only, with host-scoped circuit recovery.</summary>
public sealed class TransientReadHandler : DelegatingHandler
{
    private sealed class Circuit { public int Failures; public long OpenUntil; public int Probe; }
    private static readonly ConcurrentDictionary<string, Circuit> Circuits = new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var key = request.RequestUri!.Authority;
        var state = Circuits.GetOrAdd(key, _ => new());
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var until = Interlocked.Read(ref state.OpenUntil);
        if (until > now) throw new HttpRequestException("External service circuit is open.");
        var probe = until != 0;
        if (probe && Interlocked.CompareExchange(ref state.Probe, 1, 0) != 0)
            throw new HttpRequestException("External service recovery probe is in flight.");
        try
        {
            var attempts = request.Method == HttpMethod.Get && !probe ? 2 : 1;
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                HttpRequestMessage? clone = null;
                try
                {
                    var outgoing = request;
                    if (attempt > 0)
                    {
                        clone = new(request.Method, request.RequestUri);
                        foreach (var header in request.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        outgoing = clone;
                    }
                    var response = await base.SendAsync(outgoing, ct);
                    if (response.StatusCode is not (HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout))
                    {
                        // Authentication/validation failures are not outages.
                        Interlocked.Exchange(ref state.Failures, 0);
                        Interlocked.Exchange(ref state.OpenUntil, 0);
                        return response;
                    }
                    if (attempt + 1 == attempts) { Fail(state); return response; }
                    response.Dispose();
                }
                catch (HttpRequestException) when (!ct.IsCancellationRequested)
                {
                    if (attempt + 1 == attempts) { Fail(state); throw; }
                }
                finally { clone?.Dispose(); }
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (1 << attempt) + Random.Shared.Next(50, 200)), ct);
            }
            throw new HttpRequestException("External service unavailable.");
        }
        finally { if (probe) Interlocked.Exchange(ref state.Probe, 0); }
    }
    private static void Fail(Circuit state)
    {
        if (Interlocked.Increment(ref state.Failures) >= 3)
            Interlocked.Exchange(ref state.OpenUntil, DateTimeOffset.UtcNow.AddSeconds(15).ToUnixTimeMilliseconds());
    }
}
