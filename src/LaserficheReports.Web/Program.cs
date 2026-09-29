using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Entities;
using LaserficheReports.Domain.Exceptions;
using LaserficheReports.Infrastructure.Configuration;
using LaserficheReports.Infrastructure.Extensions;
using LaserficheReports.Web;
using Npgsql;

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
builder.Services.AddHttpClient("ReportsGraph", client =>
{
    var baseUrl = builder.Configuration["ReportsGraph:BaseUrl"] ?? "http://127.0.0.1:8766";
    var uri = new Uri(baseUrl);
    if (uri.Scheme != Uri.UriSchemeHttp ||
        uri.Host is not ("127.0.0.1" or "localhost" or "::1"))
        throw new InvalidOperationException("ReportsGraph:BaseUrl must be local HTTP.");
    client.BaseAddress = new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromMinutes(15);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = false,
    UseProxy = false
});

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
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/app/status", (IConfiguration config) => Results.Ok(new
{
    application = "Laserfiche Reports",
    mode = "local-only",
    ocrEnabled = config.GetValue("Ocr:Enabled", false),
    processId = Environment.ProcessId,
    startedAtUtc
}));

app.MapGet("/api/session/status", async (ISessionCredentialStore sessions,
    CancellationToken cancellationToken) =>
{
    var credential = await sessions.TryGetAsync(cancellationToken);
    return Results.Ok(new { authenticated = credential is not null, username = credential?.Username });
});

app.MapPost("/api/session/login", async (LoginRequest request, IRepositoryContext repositories,
    ILaserficheAuthService auth, ISessionCredentialStore sessions,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Username) || request.Username.Length > 256 ||
        request.Password is null)
        return Results.BadRequest(new { error = "Enter a Laserfiche username and password." });
    await auth.InvalidateCurrentSessionTokensAsync();
    await sessions.ClearAsync(cancellationToken);
    httpContext.Session.SetString("AuthenticationScopeMethod", "Reports");
    httpContext.Session.SetString("AuthenticationScopeSubject", httpContext.Session.Id);
    var repository = await repositories.GetActiveRepositoryAsync(cancellationToken);
    if (!await auth.TryAuthenticateAsync(repository, request.Username, request.Password, cancellationToken))
    {
        await auth.InvalidateCurrentSessionTokensAsync();
        httpContext.Session.Remove("AuthenticationScopeMethod");
        httpContext.Session.Remove("AuthenticationScopeSubject");
        return Results.Unauthorized();
    }
    await sessions.StoreAsync(request.Username, request.Password, cancellationToken);
    return Results.Ok(new { authenticated = true, repository = repository.RepositoryId });
});

app.MapPost("/api/session/logout", async (ISessionCredentialStore sessions,
    ILaserficheAuthService auth, HttpContext httpContext, CancellationToken cancellationToken) =>
{
    await auth.InvalidateCurrentSessionTokensAsync();
    await sessions.ClearAsync(cancellationToken);
    httpContext.Session.Remove("AuthenticationScopeMethod");
    httpContext.Session.Remove("AuthenticationScopeSubject");
    return Results.Ok(new { authenticated = false });
});

app.MapGet("/api/graph/status", async (IHttpClientFactory factory, CancellationToken cancellationToken) =>
{
    try
    {
        using var response = await factory.CreateClient("ReportsGraph").GetAsync("health", cancellationToken);
        return response.IsSuccessStatusCode
            ? Results.Ok(new { status = "ready", engine = "LangGraph" })
            : Results.Json(new { status = "unavailable" }, statusCode: 503);
    }
    catch (HttpRequestException)
    {
        return Results.Json(new { status = "unavailable" }, statusCode: 503);
    }
});

app.MapGet("/api/embeddings/status", async (ITextEmbeddingService embeddings,
    IConfiguration configuration, CancellationToken cancellationToken) =>
{
    try
    {
        var prefix = configuration["LocalAI:QueryEmbeddingPrefix"] ?? "search_query: ";
        var result = await embeddings.CreateEmbeddingsAsync([prefix + "health"], cancellationToken);
        return Results.Ok(new { status = "ready", model = configuration["LocalAI:EmbeddingModel"],
            dimensions = result[0].Length });
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
    catch (Exception exception)
    {
        app.Logger.LogWarning(exception, "Local embedding readiness check failed.");
        return Results.Json(new { status = "unavailable", error = "ollama_embedding_unavailable" }, statusCode: 503);
    }
});

app.MapGet("/api/reports/documents", async (int? page, string? search, ReportsChatService chat,
    ISessionCredentialStore sessions, CancellationToken cancellationToken) =>
{
    if (await sessions.TryGetAsync(cancellationToken) is null) return Results.Unauthorized();
    if (page is < 1 or > 1_000_000 || search?.Length > 200)
        return Results.BadRequest(new { error = "Invalid page or search query." });
    return Results.Ok(await chat.ListAsync(page ?? 1, search, cancellationToken));
});

// One complete folder at a time keeps the user-specific Laserfiche session
// attached to every request and lets a client checkpoint a large repository scan.
app.MapGet("/api/reports/repository/folders/{folderId:int}/children", async (
    int folderId, ILaserficheEntryService entries, IRepositoryContext repositories,
    ISessionCredentialStore sessions, CancellationToken cancellationToken) =>
{
    if (await sessions.TryGetAsync(cancellationToken) is null) return Results.Unauthorized();
    if (folderId < 0) return Results.BadRequest(new { error = "Folder ID cannot be negative." });
    var rootId = folderId == 0
        ? await entries.GetRootEntryIdAsync(cancellationToken)
        : folderId;
    var children = await entries.GetAllFolderChildrenAsync(rootId, cancellationToken);
    var repository = await repositories.GetActiveRepositoryAsync(cancellationToken);
    return Results.Ok(new
    {
        repositoryId = repository.RepositoryId,
        folderId = rootId,
        folders = children.Where(entry => entry.EntryType == LFEntryType.Folder)
            .Select(entry => new { id = entry.Id, name = entry.Name }),
        documents = children.Where(entry => entry.EntryType == LFEntryType.Document)
            .Select(entry => new { id = entry.Id, name = entry.Name,
                modified = entry.LastModifiedTime })
    });
});

app.MapPost("/api/reports/chat", async (ChatQuestion request, ReportsChatService chat,
    ISessionCredentialStore sessions, CancellationToken cancellationToken) =>
{
    if (await sessions.TryGetAsync(cancellationToken) is null) return Results.Unauthorized();
    try
    {
        return Results.Ok(await chat.AskAsync(request.Question, cancellationToken));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (LaserficheException exception)
    {
        app.Logger.LogWarning(exception, "Laserfiche access check failed during chat.");
        return Results.Json(new { error = "laserfiche_unavailable",
            message = "تعذر التحقق من صلاحية قراءة الوثائق في Laserfiche. تحقق من الاتصال ثم أعد المحاولة." },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (PostgresException exception)
    {
        app.Logger.LogError(exception, "Document search failed in PostgreSQL.");
        return Results.Json(new { error = "document_search_failed", message = exception.MessageText,
            sqlState = exception.SqlState }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (NpgsqlException exception)
    {
        app.Logger.LogError(exception, "Document database unavailable during chat.");
        return Results.Json(new { error = "supabase_database_unavailable",
            message = "قاعدة البيانات غير متاحة. تحقق من اتصال Supabase/PostgreSQL." },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
    {
        app.Logger.LogWarning(exception, "LangGraph unavailable during chat.");
        return Results.Json(new { error = "Local LangGraph or Ollama is unavailable.",
            detail = exception.Message }, statusCode: 503);
    }
    catch (InvalidOperationException exception)
    {
        app.Logger.LogWarning(exception, "Embedding or LangGraph failed during chat.");
        return Results.Json(new { error = "local_ai_unavailable", message = exception.Message },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        var diagnosticId = Guid.NewGuid().ToString("N")[..8];
        app.Logger.LogError(exception, "Chat failed. DiagnosticId={DiagnosticId}", diagnosticId);
        return Results.Json(new { error = "chat_failed", message = "تعذرت معالجة السؤال. راجع سجل التطبيق.", diagnosticId },
            statusCode: StatusCodes.Status500InternalServerError);
    }
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

app.MapPost("/api/ingestion/laserfiche/{entryId:int}", async (
    int entryId,
    ILaserficheDocumentIngestionService ingestion,
    ISessionCredentialStore sessions,
    CancellationToken cancellationToken) =>
{
    if (await sessions.TryGetAsync(cancellationToken) is null) return Results.Unauthorized();
    if (entryId <= 0)
    {
        return Results.BadRequest(new { error = "Entry ID must be positive." });
    }

    try
    {
        var result = await ingestion.IngestMetadataAsync(entryId, cancellationToken);
        return Results.Ok(result);
    }
    catch (LocalOcrException exception)
    {
        return Results.Json(new
        {
            error = "local_ocr_unavailable",
            message = exception.Message,
            preservedExistingIndex = true
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (PostgresException exception) when (
        exception.SqlState == "XX000" &&
        exception.MessageText.Contains("ENOIDENTIFIER", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Json(new
        {
            error = "supabase_tenant_identifier_missing",
            message = "The Supabase pooler username must include its tenant identifier, for example Username=postgres.YOUR_POOLER_TENANT_ID.",
            preservedExistingIndex = true
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (PostgresException exception)
    {
        return Results.Json(new
        {
            error = "supabase_write_failed",
            message = exception.MessageText,
            sqlState = exception.SqlState,
            preservedExistingIndex = true
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (NpgsqlException exception)
    {
        return Results.Json(new
        {
            error = "supabase_database_unavailable",
            message = "The local Supabase/PostgreSQL database is unavailable. Check Supabase:PostgresConnectionString and the database service.",
            databaseError = exception.Message,
            preservedExistingIndex = true
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (LaserficheException exception)
    {
        app.Logger.LogWarning(exception, "Laserfiche ingestion failed for Entry {EntryId}.", entryId);
        var operation = exception.Message.Contains("entry fields", StringComparison.OrdinalIgnoreCase)
            ? "entry_fields"
            : exception.Message.Contains("Document pages", StringComparison.OrdinalIgnoreCase)
                ? "document_pages"
                : exception.Message.Contains("Page text", StringComparison.OrdinalIgnoreCase)
                    ? "page_text"
                    : "entry";
        var operationLabel = operation switch
        {
            "entry_fields" => "حقول الوثيقة",
            "document_pages" => "قائمة صفحات الوثيقة",
            "page_text" => "نص الصفحة",
            _ => "الوثيقة"
        };
        var status = exception.StatusCode switch
        {
            401 or 429 or >= 500 => StatusCodes.Status503ServiceUnavailable,
            403 => StatusCodes.Status403Forbidden,
            404 => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status422UnprocessableEntity
        };
        return Results.Json(new { error = "laserfiche_entry_failed",
            message = $"تعذرت قراءة {operationLabel} {entryId} من Laserfiche (HTTP {exception.StatusCode}).",
            operation, upstreamStatus = exception.StatusCode, preservedExistingIndex = true }, statusCode: status);
    }
    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
    {
        app.Logger.LogWarning(exception, "Local dependency failed during ingestion of Entry {EntryId}.", entryId);
        return Results.Json(new { error = "ingestion_dependency_unavailable", message = exception.Message,
            preservedExistingIndex = true }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        var diagnosticId = Guid.NewGuid().ToString("N")[..8];
        app.Logger.LogError(exception, "Ingestion failed for Entry {EntryId}. DiagnosticId={DiagnosticId}", entryId, diagnosticId);
        return Results.Json(new { error = "ingestion_failed", message = "تعذرت فهرسة الوثيقة. راجع سجل التطبيق.",
            diagnosticId, preservedExistingIndex = true }, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/api/ocr/status", async (
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    try
    {
        var client = httpClientFactory.CreateClient("PaddleOcr");
        using var response = await client.GetAsync("health", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return Results.Json(new
            {
                isReady = false,
                statusCode = (int)response.StatusCode,
                error = "PaddleOCR worker returned an unsuccessful health response."
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Content(body, "application/json; charset=utf-8", statusCode: StatusCodes.Status200OK);
    }
    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
    {
        return Results.Json(new
        {
            isReady = false,
            error = "PaddleOCR worker is unavailable. Start tools/paddleocr-vl/start.ps1."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/database/status", async (
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var connectionString = configuration["Supabase:PostgresConnectionString"];
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Json(new
        {
            status = "unavailable",
            error = "supabase_connection_string_missing"
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    try
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT 1;", connection);
        await command.ExecuteScalarAsync(cancellationToken);

        return Results.Ok(new
        {
            status = "ready",
            host = settings.Host,
            port = settings.Port,
            database = settings.Database,
            username = settings.Username
        });
    }
    catch (PostgresException exception) when (
        exception.SqlState == "XX000" &&
        exception.MessageText.Contains("ENOIDENTIFIER", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Json(new
        {
            status = "unavailable",
            error = "supabase_tenant_identifier_missing",
            message = "Use Username=postgres.YOUR_POOLER_TENANT_ID in appsettings.Local.json."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (PostgresException exception)
    {
        return Results.Json(new
        {
            status = "unavailable",
            error = "supabase_connection_failed",
            message = exception.MessageText,
            sqlState = exception.SqlState
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception exception) when (exception is NpgsqlException or ArgumentException)
    {
        return Results.Json(new
        {
            status = "unavailable",
            error = "supabase_connection_failed",
            message = exception.Message
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapHealthChecks("/health");

app.Run();

internal sealed record ChatQuestion(string Question);
internal sealed record LoginRequest(string Username, string Password);
