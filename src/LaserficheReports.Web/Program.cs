using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Entities;
using LaserficheReports.Domain.Exceptions;
using LaserficheReports.Infrastructure.Configuration;
using LaserficheReports.Infrastructure.Extensions;
using LaserficheReports.Web;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Configuration layering (last source wins): shipped defaults, legacy local
// settings, installer settings, runtime-discovered/admin settings, developer
// local settings, then environment variables. ApiVersionDetectionService writes
// DetectedApiVersion to the runtime file, so this file must also be loaded with
// reloadOnChange for the detected v2 API to take effect without another install.
builder.Configuration.AddJsonFile(
    ReportsConfigPaths.GetLegacyRuntimeConfigPath(builder.Environment.ContentRootPath),
    optional: true,
    reloadOnChange: true);

builder.Configuration.AddJsonFile(
    ReportsConfigPaths.InstallerConfigPath,
    optional: true,
    reloadOnChange: true);

builder.Configuration.AddJsonFile(
    ReportsConfigPaths.RuntimeConfigPath,
    optional: true,
    reloadOnChange: true);

var localSettingsPath = Path.Combine(
    builder.Environment.ContentRootPath,
    "appsettings.Local.json");

builder.Configuration.AddJsonFile(
    localSettingsPath,
    optional: true,
    reloadOnChange: true);

// Environment variables are the final override. This also gives local Windows
// development a reliable fallback when an editor or launch profile changes the
// process working directory. Example: Laserfiche__ServerUrl=https://localhost.
builder.Configuration.AddEnvironmentVariables();

builder.Host.UseSerilog((context, log) => log.MinimumLevel.Information().WriteTo.File(
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaserficheReports", "logs", "reports-.log"),
    rollingInterval: RollingInterval.Day, fileSizeLimitBytes: 10 * 1024 * 1024, rollOnFileSizeLimit: true, retainedFileCountLimit: 14));

builder.Services.AddDataProtection();
builder.Services.AddHttpContextAccessor();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.Name = ".LaserficheReports.Session";
    options.IdleTimeout = TimeSpan.FromHours(8);
});
builder.Services.AddLaserficheInfrastructure(builder.Configuration);
builder.Services.AddScoped<ReportsChatService>();
builder.Services.AddScoped<LaserficheToolExecutor>();
builder.Services.AddScoped<LiveAiClient>();
builder.Services.AddSingleton<SessionRequestRegistry>();
builder.Services.AddHttpClient("LiveAI", client =>
{
    var baseUrl = (builder.Configuration["LocalAI:BaseUrl"] ?? "http://localhost:11434").TrimEnd('/');
    if (LiveAiClient.UsesOpenAi(builder.Configuration["LocalAI:Provider"]) && baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) baseUrl = baseUrl[..^3];
    client.BaseAddress = new Uri(baseUrl + "/");
    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(builder.Configuration.GetValue("LocalAI:TimeoutSeconds", 600), 10, 1800));
}).AddHttpMessageHandler<LaserficheReports.Infrastructure.Http.TransientReadHandler>();

var app = builder.Build();
var startedAtUtc = DateTimeOffset.UtcNow;

app.Logger.LogInformation(
    "Local configuration: File={LocalSettingsPath}; Exists={LocalSettingsExists}; " +
    "LaserficheServerConfigured={ServerConfigured}; RepositoryId={RepositoryId}",
    localSettingsPath,
    File.Exists(localSettingsPath),
    !string.IsNullOrWhiteSpace(builder.Configuration["Laserfiche:ServerUrl"]),
    builder.Configuration["Laserfiche:RepositoryId"] ?? "(missing)");

// This single-machine prototype uses the configured Laserfiche credential.
// Never serve its document index to a remote browser.
app.Use(async (context, next) =>
{
    var address = context.Connection.RemoteIpAddress;
    if (address is null || !System.Net.IPAddress.IsLoopback(
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    if (HttpMethods.IsPost(context.Request.Method) &&
        context.Request.Headers.TryGetValue("Origin", out var origin))
    {
        if (!Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var source) ||
            source.Host is not ("127.0.0.1" or "localhost" or "::1") ||
            source.Scheme != context.Request.Scheme ||
            source.Port != context.Request.Host.Port)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
    }
    await next();
});
app.UseSession();
// Serialize only session mutations. Reads snapshot session state while holding
// the gate briefly, then execute independently. Repository changes cancel old work.
var sessionGates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api")) { await next(); return; }
    var started = System.Diagnostics.Stopwatch.StartNew();
    var correlationId = context.TraceIdentifier;
    context.Response.Headers["X-Correlation-ID"] = correlationId;
    var mutation = context.Request.Path == "/api/session/login" || context.Request.Path == "/api/session/logout";
    var cookie = context.Request.Cookies[".LaserficheReports.Session"] ?? "new-session";
    var gate = sessionGates[(StringComparer.Ordinal.GetHashCode(cookie) & int.MaxValue) % sessionGates.Length];
    var held = false;
    var originalToken = context.RequestAborted;
    CancellationTokenSource? deadline = null;
    try
    {
        await gate.WaitAsync(originalToken); held = true;
        await context.Session.LoadAsync(originalToken);
        var registry = context.RequestServices.GetRequiredService<SessionRequestRegistry>();
        var expected = context.Request.Headers["X-Reports-Repository"].ToString();
        if (string.IsNullOrEmpty(expected)) expected = context.Request.Query["repositoryId"].ToString();
        var generation = context.Request.Headers["X-Reports-Session"].ToString();
        if (string.IsNullOrEmpty(generation)) generation = context.Request.Query["sessionGeneration"].ToString();
        var repository = await context.RequestServices.GetRequiredService<IRepositoryContext>().GetActiveRepositoryAsync(originalToken);
        if (!context.Request.Path.StartsWithSegments("/api/session") &&
            ((!string.IsNullOrEmpty(expected) && !string.Equals(expected, repository.RepositoryId, StringComparison.OrdinalIgnoreCase)) ||
             (!string.IsNullOrEmpty(generation) && generation != context.Session.GetString("ReportsGeneration"))))
        {
            await WriteProblem(context, 409, "تغيّر المستودع. أعد تسجيل الدخول إلى المستودع المطلوب."); return;
        }
        if (mutation) registry.Cancel(context.Session.Id);
        var scopeToken = mutation ? CancellationToken.None : registry.Get(context.Session.Id, context.Session.GetString("ReportsGeneration") ?? "");
        deadline = CancellationTokenSource.CreateLinkedTokenSource(originalToken, scopeToken);
        var seconds = context.Request.Path.Value?.EndsWith("/status") == true
            ? builder.Configuration.GetValue("Reports:HealthTimeoutSeconds", 8)
            : context.Request.Path.StartsWithSegments("/api/reports/chat")
                ? builder.Configuration.GetValue("LocalAI:TimeoutSeconds", 600) + builder.Configuration.GetValue("Reports:QueryTimeoutSeconds", 120)
                : builder.Configuration.GetValue("Reports:QueryTimeoutSeconds", 120);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 2000)));
        context.RequestAborted = deadline.Token;
        if (!mutation) { gate.Release(); held = false; }
        await next();
    }
    catch (Exception error)
    {
        if (!originalToken.IsCancellationRequested)
        {
            app.Logger.LogWarning("Request failed. Type={ErrorType} CorrelationId={CorrelationId}", error.GetType().Name, correlationId);
            var (status, message) = error switch
            {
                ArgumentException => (400, error.Message),
                LaserficheException e when e.StatusCode == 401 => (401, "بيانات الدخول غير صالحة أو انتهت الجلسة."),
                LaserficheException e when e.StatusCode == 403 => (403, "لا تملك صلاحية الوصول إلى البيانات المطلوبة."),
                LaserficheException e when e.StatusCode == 404 => (404, "المستودع أو الإدخال غير موجود."),
                TimeoutException or OperationCanceledException => (504, "انتهت مهلة الطلب أو أُلغي بعد تغيير المستودع. يمكنك إعادة المحاولة."),
                InvalidOperationException => (422, error.Message),
                _ => (503, "تعذر الاتصال بالخدمة المطلوبة. تحقق من حالة Laserfiche والذكاء الاصطناعي.")
            };
            if (!context.Response.HasStarted) await WriteProblem(context, status, message);
            else if (context.Response.ContentType?.StartsWith("text/event-stream") == true)
                await context.Response.WriteAsync("event: error\ndata: " + System.Text.Json.JsonSerializer.Serialize(new { message, correlationId }) + "\n\n", originalToken);
        }
    }
    finally
    {
        context.RequestAborted = originalToken;
        deadline?.Dispose();
        if (held)
        {
            try { if (mutation) await context.Session.CommitAsync(CancellationToken.None); }
            finally { gate.Release(); }
        }
        app.Logger.LogInformation("[PERF] Operation=API Path={Path} DurationMs={DurationMs} CorrelationId={CorrelationId}",
            context.Request.Path, started.ElapsedMilliseconds, correlationId);
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/app/status", (IConfiguration config) => Results.Ok(new
{
    application = "Laserfiche Reports",
    mode = "local-only",
    ocrEnabled = false,
    ragEnabled = false, vectorSearchEnabled = false, legacyIndexingEnabled = false, dataSource = "Laserfiche",
    processId = Environment.ProcessId,
    startedAtUtc
}));

app.MapReportSessions();
app.MapReportLinks();

app.MapGet("/api/reports/documents", async (int? page, string? search, ReportsChatService chat,
    ISessionCredentialStore sessions, CancellationToken cancellationToken) =>
{
    if (await sessions.TryGetAsync(cancellationToken) is null) return Results.Unauthorized();
    if (page is < 1 or > 1_000_000 || search?.Length > 200)
        return Results.BadRequest(new { error = "Invalid page or search query." });
    return Results.Ok(await chat.ListAsync(page ?? 1, search, cancellationToken));
});

app.MapGet("/api/reports/repository/folders/{folderId:int}/children", async (
    int folderId, int? page, ILaserficheEntryService entries, IRepositoryContext repositories,
    ISessionCredentialStore sessions, CancellationToken ct) =>
{
    if (await sessions.TryGetAsync(ct) is null) return Results.Unauthorized();
    if (folderId < 0 || page is < 1 or > 1000000) return Results.BadRequest(new { error = "رقم المجلد أو الصفحة غير صالح." });
    var root = folderId == 0 ? await entries.GetRootEntryIdAsync(ct) : folderId;
    var children = await entries.GetEntryChildrenAsync(root, page ?? 1, 50, ct);
    return Results.Ok(new { items = children.Items, page = page ?? 1, pageSize = 50,
        totalCount = children.TotalCountIsExact ? (int?)children.TotalCount : null, hasMore = children.HasNextPage });
});

app.MapPost("/api/reports/chat", async (ChatQuestion request, ReportsChatService chat,
    ISessionCredentialStore sessions, LiveAiClient ai, CancellationToken ct) =>
{
    if (await sessions.TryGetAsync(ct) is null) return Results.Unauthorized();
    var result = await chat.AskAsync(request.Question, ct, request.PreviousQuery);
    try
    {
        var analysis = new System.Text.StringBuilder();
        await foreach (var text in ai.AnalyzeAsync(request.Question, result.Answer, ct)) analysis.Append(text);
        if (analysis.Length > 0) result = result with { Answer = analysis.ToString() };
    }
    catch (Exception error) when (error is not OperationCanceledException)
    {
        result = result with { Answer = result.Answer + "\n\nتعذر توليد جواب الذكاء الاصطناعي. البيانات الموثقة أعلاه متاحة؛ تحقق من حالة خدمة الذكاء الاصطناعي." };
    }
    return Results.Ok(result);
});

app.MapPost("/api/reports/chat/stream", async (ChatQuestion request, ReportsChatService chat,
    ISessionCredentialStore sessions, LiveAiClient ai, HttpContext http, CancellationToken ct) =>
{
    if (await sessions.TryGetAsync(ct) is null) { http.Response.StatusCode = 401; return; }
    http.Response.ContentType = "text/event-stream; charset=utf-8";
    http.Response.Headers.CacheControl = "no-cache";
    async Task Send(string kind, object data)
    {
        await http.Response.WriteAsync("event: " + kind + "\ndata: " + System.Text.Json.JsonSerializer.Serialize(data,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) + "\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }
    var result = await chat.AskAsync(request.Question, ct, request.PreviousQuery,
        status => Send("status", new { message = status }));
    await Send("result", result with { Answer = "" });
    var hasAiText = false;
    {
        try
        {
            await Send("status", new { message = "جارٍ توليد إجابة الذكاء الاصطناعي..." });
            await foreach (var text in ai.AnalyzeAsync(request.Question, result.Answer, ct))
            {
                if (!string.IsNullOrWhiteSpace(text)) hasAiText = true;
                await Send("delta", new { text });
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            app.Logger.LogWarning("AI analysis failed. Type={Type}", error.GetType().Name);
            await Send("delta", new { text = hasAiText
                ? "\n\nتعذر إكمال إجابة الذكاء الاصطناعي. الإجابة أعلاه جزئية؛ حاول مجددًا."
                : "تعذر توليد إجابة الذكاء الاصطناعي. هذه بيانات Laserfiche الموثقة مؤقتًا:\n\n" + result.Answer });
        }
    }
});

app.MapGet("/api/ai/status", async (LiveAiClient ai, CancellationToken ct) =>
{
    var model = await ai.ResolveModelAsync(ct);
    return Results.Ok(new { status = "ready", model, modelConfigured = true });
});

app.MapGet("/api/laserfiche/status", async (
    ILaserficheRepositoryService repositories,
    CancellationToken cancellationToken) =>
{
    var status = await repositories.TestConnectionAsync(cancellationToken);
    return Results.Ok(status);
});

app.MapGet("/api/laserfiche/repository", async (
    ILaserficheRepositoryService repositories,
    CancellationToken cancellationToken) =>
{
    var repository = await repositories.GetRepositoryInfoAsync(cancellationToken);
    return Results.Ok(repository);
});

app.MapGet("/api/laserfiche/documents/{entryId:int}/pages/{pageNumber:int}/image", async (
    int entryId,
    int pageNumber,
    ILaserficheDocumentService documents,
    ISessionCredentialStore sessions,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    if (await sessions.TryGetAsync(cancellationToken) is null) return Results.Unauthorized();
    if (entryId <= 0 || pageNumber <= 0)
    {
        return Results.BadRequest(new { error = "Entry ID and page number must be positive." });
    }

    var page = await documents
        .GetPageImageAsync(entryId, pageNumber, cancellationToken);

    // The upstream Laserfiche response must stay alive until ASP.NET finishes
    // copying the streamed page to the caller.
    httpContext.Response.RegisterForDispose(page);

    var extension = string.IsNullOrWhiteSpace(page.Extension) ? ".bin" : page.Extension;
    var fileName = string.IsNullOrWhiteSpace(page.FileName)
        ? $"laserfiche-{entryId}-page-{pageNumber}{extension}"
        : page.FileName;

    return Results.Stream(
        page.Content,
        contentType: page.ContentType,
        fileDownloadName: fileName,
        enableRangeProcessing: false);
});

app.MapGet("/health", () => Results.Ok(new { status = "ready" }));

try { app.Run(); }
catch (IOException error) when (error.InnerException is Microsoft.AspNetCore.Connections.AddressInUseException)
{
    Console.Error.WriteLine("تعذر التشغيل: المنفذ مستخدم بواسطة برنامج شغال. استخدم scripts/start-reports.ps1 أو أوقف النسخة السابقة ثم أعد التشغيل.");
    Environment.ExitCode = 1;
}

static Task WriteProblem(HttpContext context, int status, string message)
{
    context.Response.StatusCode = status;
    return context.Response.WriteAsJsonAsync(new { type = "about:blank", title = message, status,
        detail = message, message, correlationId = context.TraceIdentifier }, options: (System.Text.Json.JsonSerializerOptions?)null, contentType: "application/problem+json", cancellationToken: CancellationToken.None);
}

internal sealed record ChatQuestion(string Question, RepositoryQuery? PreviousQuery = null);
internal sealed record LoginRequest(string Username, string Password, string? RepositoryId = null);
