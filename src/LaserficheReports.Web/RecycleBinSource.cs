using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LaserficheReports.Application.Interfaces;

namespace LaserficheReports.Web;

internal sealed class RecycleBinException(string message) : Exception(message);
internal sealed class RecycleBinBinding
{
    public bool Enabled { get; set; }
    public string ServerName { get; set; } = "";
    public string RepositoryName { get; set; } = "";
    public bool Secure { get; set; } = true;
}
internal sealed record RecycledEntry(int EntryId, string Name, string EntryType, string DeletedBy,
    DateTimeOffset DeletedAt, int? PageCount);
internal sealed record RecycleBinSnapshot(string Protocol, string RepositoryId, string Username, string Scope,
    bool Complete, RecycledEntry[] Entries);

internal sealed class RecycleBinSource(IConfiguration configuration, IRepositoryContext repositories,
    ISessionCredentialStore sessions)
{
    internal string ReaderPath => Path.Combine(AppContext.BaseDirectory, "recycle-bin", "LaserficheReports.RecycleBin.exe");
    internal RecycleBinBinding? Binding(string repositoryId) => configuration.GetSection("RecycleBin:Repositories")
        .GetChildren().FirstOrDefault(c => string.Equals(c.Key, repositoryId, StringComparison.OrdinalIgnoreCase))?.Get<RecycleBinBinding>();
    internal async Task<object> CatalogAsync(CancellationToken ct)
    {
        var repository = await repositories.GetActiveRepositoryAsync(ct);
        var reason = UnavailableReason(repository.RepositoryId);
        return reason == null ? new { status = "available", reason = "", scope = "current_user_visible" }
            : new { status = "unavailable", reason, scope = "current_user_visible" };
    }
    internal string? UnavailableReason(string repositoryId)
    {
        if (!OperatingSystem.IsWindows()) return "عرض سلة المحذوفات يحتاج نسخة Windows مع قارئ Laserfiche SDK؛ هذا المصدر غير متاح على هذه المنصة.";
        if (!File.Exists(ReaderPath)) return "قارئ سلة المحذوفات غير موجود في النسخة المشغلة. حدّث نشر Windows من الإصدار الجديد.";
        var binding = Binding(repositoryId);
        if (binding == null || !binding.Enabled ||
            string.IsNullOrWhiteSpace(binding.ServerName) || string.IsNullOrWhiteSpace(binding.RepositoryName))
            return "ربط سلة المحذوفات غير مهيأ. افتح الوثائق والنظام ← سلة المحذوفات وحدد اسم Laserfiche Server والمستودع.";
        return null;
    }
    internal async Task<RecycleBinSnapshot> ReadAsync(CancellationToken ct)
    {
        var repository = await repositories.GetActiveRepositoryAsync(ct);
        var reason = UnavailableReason(repository.RepositoryId);
        if (reason != null) throw new RecycleBinException(reason);
        var credentials = await sessions.TryGetAsync(ct) ?? throw new UnauthorizedAccessException();
        var binding = Binding(repository.RepositoryId)!;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromHours(1));
        using var process = new Process { StartInfo = new ProcessStartInfo(ReaderPath) {
            WorkingDirectory = Path.GetDirectoryName(ReaderPath)!, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8 } };
        try
        {
            async Task<string> DrainAsync(StreamReader reader, int limit)
            {
                try { return await ReadBoundedAsync(reader, limit, budget.Token); }
                catch { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw; }
            }
            process.Start();
            var output = DrainAsync(process.StandardOutput, 33554432);
            var errors = DrainAsync(process.StandardError, 4096);
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { repositoryId = repository.RepositoryId,
                binding.RepositoryName, binding.ServerName, binding.Secure, username = credentials.Username,
                password = credentials.Password, maxRows = 100000 }).AsMemory(), budget.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(budget.Token);
            var text = await output; await errors;
            if (process.ExitCode == 2) throw new RecycleBinException("تجاوزت السلة حد القراءة الآمنة؛ لم تُعرض أعداد جزئية على أنها الإجمالي.");
            if (process.ExitCode != 0) throw new RecycleBinException("تعذرت قراءة سلة المحذوفات. تحقق من اسم Laserfiche Server وبيانات الحساب وصلاحياته، ومن تثبيت متطلبات SDK: ‎.NET Framework 4.8 وVisual C++ x64.");
            return Parse(text, repository.RepositoryId, credentials.Username);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new RecycleBinException("انتهت مهلة قراءة سلة المحذوفات؛ لم تُعرض نتائج جزئية."); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or JsonException)
        { throw new RecycleBinException("تعذر تشغيل قارئ سلة المحذوفات أو تفسير رده. حدّث نشر Windows وتحقق من متطلبات SDK."); }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
    }
    internal static RecycleBinSnapshot Parse(string json, string repositoryId, string username)
    {
        var data = JsonSerializer.Deserialize<RecycleBinSnapshot>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (data == null || data.Protocol != "recycle-bin-v1" || !data.Complete || data.Scope != "current_user_visible" ||
            data.RepositoryId != repositoryId || data.Username != username || data.Entries == null || data.Entries.Length > 100000 ||
            data.Entries.Any(e => e == null || e.EntryId <= 0 || string.IsNullOrWhiteSpace(e.Name) ||
                e.EntryType is not ("document" or "folder") || e.DeletedBy == null || e.DeletedAt == default || e.PageCount < 0) ||
            data.Entries.Select(e => e.EntryId).Distinct().Count() != data.Entries.Length)
            throw new RecycleBinException("رد سلة المحذوفات غير مكتمل أو لا يطابق المستخدم والمستودع الحاليين.");
        return data;
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken ct)
    {
        var text = new StringBuilder(); var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (count == 0) return text.ToString();
            if (text.Length + count > limit) throw new RecycleBinException("تجاوز رد السلة حد الحجم؛ لم تُعرض بيانات جزئية.");
            text.Append(buffer, 0, count);
        }
    }
}
