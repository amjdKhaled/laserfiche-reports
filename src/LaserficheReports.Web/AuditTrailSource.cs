using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LaserficheReports.Application.Interfaces;

namespace LaserficheReports.Web;

internal sealed class AuditTrailException(string message) : Exception(message);
internal sealed class AuditTrailOptions
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "";
    // Explicit repository bindings: never infer a data source from a display name.
    public Dictionary<string, AuditTrailReportOptions> Repositories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int TimeoutSeconds { get; set; } = 3600;
    public int PollMilliseconds { get; set; } = 1000;
    public int MaxRows { get; set; } = 100000;
    public long MaxBytes { get; set; } = 33554432;
}
internal sealed class AuditTrailReportOptions
{
    public string DataSource { get; set; } = "";
    public string ReportId { get; set; } = "";
    public string ExportDirectory { get; set; } = "";
    public string ScopeDescription { get; set; } = "";
    // Canonical field -> exact CSV header. No assumptions about localized/raw headers.
    public Dictionary<string, string> Columns { get; set; } = new();
    public Dictionary<string, string> EventLabels { get; set; } = new();
    public string? DateFormat { get; set; }
}
internal sealed record AuditEvent(DateTimeOffset EventTime, string EventType, string UserName,
    int? EntryId, string EntryName, string Outcome);
internal sealed record AuditSnapshot(IReadOnlyList<AuditEvent> Events, DateTimeOffset ExportedAt,
    string ScopeDescription, string[] Fields, IReadOnlyDictionary<string, string> EventLabels);

/// <summary>Uses the exact export/status protocol in the installed Export-AuditReport.ps1.
/// Request-scoped snapshot and HTTP handler: no shared Windows identity, cookies, files or cached audit rows.</summary>
internal sealed class AuditTrailSource(IConfiguration configuration, IRepositoryContext repositories,
    ISessionCredentialStore sessions, ILogger<AuditTrailSource> logger, AuditSessionCredentials? auditCredentials = null)
{
    private Task<AuditSnapshot>? snapshot;
    private string? snapshotRepository;
    internal AuditTrailOptions Options => configuration.GetSection("AuditTrail").Get<AuditTrailOptions>() ?? new();
    // Test seam also verifies that no LF bearer token is sent to the separate Windows-auth service.
    internal Func<NetworkCredential, HttpMessageHandler>? HandlerFactory { get; set; }

    internal async Task<object> CatalogAsync(CancellationToken ct)
    {
        if (!Options.Enabled) return new { status = "disabled", reason = "ربط التقارير بـ Audit Trail غير مهيأ. افتح الوثائق والنظام ثم إعدادات سجل التدقيق؛ هذه الحالة لا تعني أن تسجيل الأحداث معطّل في Laserfiche." };
        try
        {
            var data = await ReadAsync(ct);
            if (data.Events.Select(e => e.EventType).Concat(data.EventLabels.Keys).Distinct().Count() > 200)
                throw new AuditTrailException("أنواع أحداث التقرير أكثر من حد التخطيط؛ اختر تقريرًا محفوظًا بنطاق أضيق.");
            return new { status = "available", fields = data.Fields,
                eventTypes = data.Events.Select(e => e.EventType).Concat(data.EventLabels.Keys).Distinct().Order().Select(code => new {
                    value = code, label = data.EventLabels.GetValueOrDefault(code, code) }).ToArray(),
                outcomes = data.Events.Select(e => e.Outcome).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().Order().ToArray(),
                exportedAt = data.ExportedAt, scope = data.ScopeDescription,
                limitation = "Saved-report events only, not a current recycle-bin inventory. Missing events do not prove absence outside this report's filters/retention/auditing configuration." };
        }
        catch (AuditTrailException error)
        {
            logger.LogWarning("Stage=AUDIT_CATALOG Status=unavailable Reason={Reason}", error.Message);
            return new { status = "unavailable", reason = error.Message };
        }
    }

    internal async Task<AuditSnapshot> ReadAsync(CancellationToken ct)
    {
        var repository = await repositories.GetActiveRepositoryAsync(ct);
        if (snapshotRepository != null && !string.Equals(snapshotRepository, repository.RepositoryId, StringComparison.OrdinalIgnoreCase))
            throw new AuditTrailException("تغيّر المستودع أثناء قراءة سجل التدقيق؛ أعد الطلب.");
        snapshotRepository = repository.RepositoryId;
        return await (snapshot ??= ExportAsync(repository.RepositoryId, ct));
    }

    private async Task<AuditSnapshot> ExportAsync(string repositoryId, CancellationToken ct)
    {
        var options = Options;
        if (!options.Enabled) throw new AuditTrailException("فعّل ربط Audit Trail وحدد التقرير المحفوظ أولًا.");
        var report = options.Repositories.FirstOrDefault(p => string.Equals(p.Key, repositoryId, StringComparison.OrdinalIgnoreCase)).Value;
        if (report is null || string.IsNullOrWhiteSpace(report.DataSource) || string.IsNullOrWhiteSpace(report.ReportId) ||
            string.IsNullOrWhiteSpace(report.ExportDirectory) || string.IsNullOrWhiteSpace(report.ScopeDescription) ||
            !Path.IsPathFullyQualified(report.ExportDirectory))
            throw new AuditTrailException("إعداد مصدر Audit Trail للمستودع الحالي ناقص؛ يلزم التقرير ومسار التصدير ووصف نطاقه.");
        if (!Uri.TryCreate(options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != "https" && !(baseUri.Scheme == "http" && baseUri.IsLoopback) ||
            !string.IsNullOrEmpty(baseUri.UserInfo) || !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
            throw new AuditTrailException("عنوان Audit Trail يجب أن يكون HTTPS، أو HTTP محليًا فقط.");
        if (options.TimeoutSeconds is < 1 or > 3600 || options.PollMilliseconds is < 10 or > 10000 ||
            options.MaxRows is < 1 or > 1000000 || options.MaxBytes is < 1 or > 268435456)
            throw new AuditTrailException("حدود قراءة Audit Trail غير صالحة.");
        var credentials = auditCredentials?.Read() ?? await sessions.TryGetAsync(ct);
        if (credentials is null || string.IsNullOrEmpty(credentials.Password) ||
            !(credentials.Username.Contains('\\') || credentials.Username.Contains('@')))
            throw new AuditTrailException("قراءة Audit Trail تحتاج حساب Windows له صلاحية التقرير. أدخله في الوثائق والنظام ← إعدادات سجل التدقيق، مع إبقاء حساب Laserfiche الحالي.");
        var user = credentials.Username;
        var slash = user.IndexOf('\\');
        var network = slash >= 0 ? new NetworkCredential(user[(slash + 1)..], credentials.Password, user[..slash])
            : new NetworkCredential(user, credentials.Password);
        using var handler = HandlerFactory?.Invoke(network) ?? new HttpClientHandler {
            Credentials = network, UseDefaultCredentials = false, UseCookies = false,
            AllowAutoRedirect = false, UseProxy = false };
        using var client = new HttpClient(handler) { BaseAddress = baseUri, Timeout = Timeout.InfiniteTimeSpan };
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var filename = $"LaserficheReports-{Guid.NewGuid():N}.csv";
        var path = Path.Combine(report.ExportDirectory, filename);
        // Never use a caller-selected path or overwrite a saved report/previous export.
        if (File.Exists(path)) throw new AuditTrailException("تعارض اسم ملف التصدير؛ أعد المحاولة.");
        string? token = null;
        var completed = false;
        try
        {
            static string E(string value) => Uri.EscapeDataString(value);
            using var response = await client.PostAsync($"api/SavedReport/{E(report.ReportId)}/raw?lang=en&tz=UTC&destFile={E(filename)}&dataSourceId={E(report.DataSource)}", null, budget.Token);
            await EnsureAsync(response);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(budget.Token);
            token = json.ValueKind == JsonValueKind.String ? json.GetString() : null;
            if (string.IsNullOrWhiteSpace(token) || token.Length > 500)
                throw new AuditTrailException("Audit Trail لم يرجع رمز تصدير صالحًا.");
            while (true)
            {
                using var statusResponse = await client.GetAsync($"api/SavedReport/raw/{E(token)}/status", budget.Token);
                await EnsureAsync(statusResponse);
                var status = await statusResponse.Content.ReadFromJsonAsync<JsonElement>(budget.Token);
                if (status.ValueKind != JsonValueKind.Object ||
                    !(status.TryGetProperty("State", out var state) || status.TryGetProperty("state", out state)) ||
                    state.ValueKind != JsonValueKind.Number || !state.TryGetInt32(out var code))
                    throw new AuditTrailException("تعذر تفسير حالة تصدير Audit Trail.");
                if (code == 2) { completed = true; break; }
                if (code is 5 or 6) {
                    logger.LogWarning("Stage=AUDIT_EXPORT State={State}", code);
                    throw new AuditTrailException("فشل تصدير تقرير التدقيق أو أُلغي. افحص التقرير المحفوظ وصلاحية AuditTrailAppPool في مجلد التصدير؛ لم تُعتبر النتائج فارغة.");
                }
                if (code is not (0 or 1)) throw new AuditTrailException("حالة تصدير Audit Trail غير معروفة.");
                await Task.Delay(options.PollMilliseconds, budget.Token);
            }
            var events = await ReadExportAsync(path, report, options, budget.Token);
            return new AuditSnapshot(events, DateTimeOffset.UtcNow, report.ScopeDescription,
                report.Columns.Keys.ToArray(), report.EventLabels);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new AuditTrailException("انتهت مهلة تصدير التدقيق؛ لم تُعرض أعداد أو نتائج جزئية."); }
        catch (UnauthorizedAccessException)
        { throw new AuditTrailException("حساب تشغيل Reports لا يملك صلاحية قراءة مجلد تصدير Audit Trail. عند استخدام جهاز ثانٍ، اضبط مسار المشاركة وصلاحيات القراءة."); }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException)
        { logger.LogWarning("Stage=AUDIT_EXPORT Failure={ErrorType}", error.GetType().Name);
          throw new AuditTrailException("تعذرت قراءة تقرير التدقيق؛ افحص الاتصال ومسار التصدير على جهاز Audit Trail وصلاحية قراءته من جهاز Reports."); }
        finally
        {
            if (token != null && !completed)
            {
                // This cancels ONLY our own export job. Never DELETE a report, entry or file.
                try { using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    using var response = await client.DeleteAsync($"api/SavedReport/raw/{Uri.EscapeDataString(token)}/cancel", cancel.Token); }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
            }
        }
    }
    private static async Task<IReadOnlyList<AuditEvent>> ReadExportAsync(string path, AuditTrailReportOptions report,
        AuditTrailOptions options, CancellationToken ct)
    {
        var visibilityDeadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return await AuditCsv.ReadAsync(path, report, options, ct); }
            catch (IOException) when (DateTimeOffset.UtcNow < visibilityDeadline)
            { await Task.Delay(Math.Max(10, options.PollMilliseconds), ct); }
        }
    }
    private static Task EnsureAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new AuditTrailException(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "Audit Trail رفض حساب Windows أو صلاحية التقرير؛ جلسة Laserfiche مستقلة عنه."
                : response.StatusCode == HttpStatusCode.NotFound
                    ? "عنوان Audit Trail أو معرّف التقرير المحفوظ غير صحيح (404). راجع إعدادات سجل التدقيق."
                    : $"خدمة Audit Trail لم تُكمل طلب التصدير (HTTP {(int)response.StatusCode}).");
        return Task.CompletedTask;
    }
}
