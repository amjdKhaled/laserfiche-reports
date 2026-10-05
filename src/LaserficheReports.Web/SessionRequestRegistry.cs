using Microsoft.Extensions.Caching.Memory;

namespace LaserficheReports.Web;

/// <summary>Generation-scoped cancellation; never stores repository document data.</summary>
internal sealed class SessionRequestRegistry(IMemoryCache cache)
{
    private sealed record State(string Generation, CancellationTokenSource Source);
    private readonly object gate = new();
    internal CancellationToken Get(string sessionId, string generation)
    {
        lock (gate)
        {
            var key = "reports-requests:" + sessionId;
            if (cache.TryGetValue<State>(key, out var current) && current?.Generation == generation) return current.Source.Token;
            current?.Source.Cancel();
            var source = new CancellationTokenSource();
            cache.Set(key, new State(generation, source), new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(8) }
                .RegisterPostEvictionCallback((_, value, _, _) => { var state = (State)value!; state.Source.Cancel(); state.Source.Dispose(); }));
            return source.Token;
        }
    }
    internal void Cancel(string sessionId)
    {
        lock (gate) if (cache.TryGetValue<State>("reports-requests:" + sessionId, out var state)) state?.Source.Cancel();
    }
}
