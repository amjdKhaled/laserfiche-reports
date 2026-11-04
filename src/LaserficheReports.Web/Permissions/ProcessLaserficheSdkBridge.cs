using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LaserficheReports.Web.Permissions;

// An inherited anonymous pipe is the service-authenticated channel: no network listener,
// URL, port, browser endpoint, command-line secret or executable path supplied by a caller.
internal sealed class ProcessLaserficheSdkBridge : ILaserficheSdkBridge
{
    internal static string ExecutablePath => Path.Combine(AppContext.BaseDirectory, "permission-bridge", "LaserficheReports.PermissionBridge.exe");
    public async Task<PermissionDecision> VerifyAsync(PermissionTarget target, AuthenticatedPermissionPrincipal principal,
        string repositoryId, PermissionRepositoryBinding binding, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(ExecutablePath)) return new(PermissionStatus.VerificationUnavailable);
        var requestId = Guid.NewGuid().ToString("N");
        using var process = new Process { StartInfo = new ProcessStartInfo(ExecutablePath) {
            WorkingDirectory = Path.GetDirectoryName(ExecutablePath)!, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8 } };
        Task<string>? output = null;
        Task<string>? errors = null;
        try
        {
            process.Start();
            output = ReadBoundedAsync(process.StandardOutput, 8192, ct);
            errors = ReadBoundedAsync(process.StandardError, 1024, ct);
            // Fail/kill as soon as either stream overflows, even if the other stream blocks.
            _ = output.ContinueWith(_ => Kill(process), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            _ = errors.ContinueWith(_ => Kill(process), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            var request = JsonSerializer.Serialize(new { protocol = "permission-v1", requestId, repositoryId,
                principalScope = principal.Scope, target, binding,
                username = principal.Credential?.Username, password = principal.Credential?.Password,
                trusteeSid = principal.TrusteeSid }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await process.StandardInput.WriteAsync(request.AsMemory(), ct);
            process.StandardInput.Close();
            await process.WaitForExitAsync(ct);
            var result = await output;
            await errors;
            return process.ExitCode == 0 ? Parse(result, requestId, repositoryId, principal.Scope) : new(PermissionStatus.VerificationUnavailable);
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or JsonException or InvalidDataException)
        { return new(PermissionStatus.VerificationUnavailable); }
        finally
        {
            Kill(process);
            // Observe drain tasks after termination, including cancellation/overflow paths.
            foreach (var task in new[] { output, errors }) if (task != null) try { await task; } catch { }
        }
    }

    private sealed record Reply(string Protocol, string RequestId, string RepositoryId, string PrincipalScope,
        string Status, string? EntryKind, int[]? VerifiedChain);
    internal static PermissionDecision Parse(string json, string requestId, string repositoryId, string scope)
    {
        try
        {
            var reply = JsonSerializer.Deserialize<Reply>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 });
            if (reply == null || reply.Protocol != "permission-v1" || reply.RequestId != requestId ||
                reply.RepositoryId != repositoryId || reply.PrincipalScope != scope ||
                !Enum.TryParse<PermissionStatus>(reply.Status, out var status) || !Enum.IsDefined(status) || reply.Status != status.ToString() ||
                reply.EntryKind is not (null or "document" or "folder") ||
                status == PermissionStatus.DocumentAccessDenied && reply.EntryKind != "document" ||
                status == PermissionStatus.FolderAccessDenied && reply.EntryKind != "folder" ||
                status == PermissionStatus.NotFound && reply.EntryKind == null)
                return new(PermissionStatus.VerificationUnavailable);
            if (reply.VerifiedChain != null && (reply.VerifiedChain.Length is < 1 or > 256 ||
                reply.VerifiedChain.Any(id => id <= 0) || reply.VerifiedChain.Distinct().Count() != reply.VerifiedChain.Length ||
                status != PermissionStatus.Accessible)) return new(PermissionStatus.VerificationUnavailable);
            return new(status, reply.EntryKind, reply.VerifiedChain);
        }
        catch (JsonException) { return new(PermissionStatus.VerificationUnavailable); }
    }
    private static void Kill(Process process)
    { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken ct)
    {
        var text = new StringBuilder(); var buffer = new char[1024];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (count == 0) return text.ToString();
            if (text.Length + count > limit) throw new InvalidDataException("Bridge reply exceeds protocol limit.");
            text.Append(buffer, 0, count);
        }
    }
}
