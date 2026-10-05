using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Exceptions;

namespace LaserficheReports.Web;

internal sealed class ReportsSessionMiddleware(RequestDelegate next)
{
    private static readonly SemaphoreSlim[] Gates = Enumerable.Range(0, 64)
        .Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public async Task InvokeAsync(HttpContext context, IRepositoryContext repositories)
    {
        if (!context.Request.Path.StartsWithSegments("/api")) { await next(context); return; }
        var cookie = context.Request.Cookies[".LaserficheReports.Session"] ?? "new-session";
        var gate = Gates[(StringComparer.Ordinal.GetHashCode(cookie) & int.MaxValue) % Gates.Length];
        var writesSession = context.Request.Path == "/api/session/login" ||
            context.Request.Path == "/api/session/logout";
        var held = false;
        try
        {
            await gate.WaitAsync(context.RequestAborted);
            held = true;
            // DistributedSession keeps this request's loaded values independently.
            // Only login/logout write session state; readers must never write a stale snapshot.
            await context.Session.LoadAsync(context.RequestAborted);
            var expected = context.Request.Headers["X-Reports-Repository"].ToString();
            if (string.IsNullOrEmpty(expected)) expected = context.Request.Query["repositoryId"].ToString();
            var generation = context.Request.Headers["X-Reports-Session"].ToString();
            if (string.IsNullOrEmpty(generation)) generation = context.Request.Query["sessionGeneration"].ToString();
            var repository = await repositories.GetActiveRepositoryAsync(context.RequestAborted);
            if (!context.Request.Path.StartsWithSegments("/api/session") &&
                ((!string.IsNullOrEmpty(expected) && !string.Equals(expected, repository.RepositoryId, StringComparison.OrdinalIgnoreCase)) ||
                 (!string.IsNullOrEmpty(generation) && generation != context.Session.GetString("ReportsGeneration"))))
            {
                context.Response.StatusCode = 409;
                await context.Response.WriteAsJsonAsync(new { error = "تغيّر المستودع في جلسة أخرى. أعد تسجيل الدخول إلى المستودع المطلوب." });
                return;
            }
            if (!writesSession) { gate.Release(); held = false; }
            await next(context);
        }
        catch (LaserficheException error) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = error.StatusCode is 401 or 403 or 404 ? error.StatusCode : 502;
            await context.Response.WriteAsJsonAsync(new { error = "تعذر الوصول إلى Laserfiche. تحقق من المستودع وبيانات الدخول وصلاحيات الوثيقة." });
        }
        catch (ArgumentException error) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = error.Message });
        }
        finally
        {
            if (held)
            {
                try { if (writesSession) await context.Session.CommitAsync(CancellationToken.None); }
                finally { gate.Release(); }
            }
        }
    }
}
