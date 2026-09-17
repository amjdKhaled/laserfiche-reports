using LaserficheReports.Application.Interfaces;
using LaserficheReports.Infrastructure.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile(
    "appsettings.Local.json",
    optional: true,
    reloadOnChange: true);

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

var app = builder.Build();

app.UseSession();

app.MapGet("/", () => Results.Ok(new
{
    application = "Laserfiche Reports",
    mode = "local-only",
    phase = 1
}));

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

app.MapPost("/api/ingestion/laserfiche/{entryId:int}", async (
    int entryId,
    ILaserficheDocumentIngestionService ingestion,
    CancellationToken cancellationToken) =>
{
    if (entryId <= 0)
    {
        return Results.BadRequest(new { error = "Entry ID must be positive." });
    }

    var result = await ingestion.IngestMetadataAsync(entryId, cancellationToken);
    return Results.Ok(result);
});

app.MapHealthChecks("/health");

app.Run();
