using System.Diagnostics;
using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "LaserficheReports");
builder.Services.AddHostedService<ReportsWorker>();
await builder.Build().RunAsync();

sealed class ReportsWorker(ILogger<ReportsWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var root = AppContext.BaseDirectory;
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LaserficheReports");
        Directory.CreateDirectory(Path.Combine(data, "logs"));
        var settingsPath = Path.Combine(data, "hosting.config.json");
        using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath, stoppingToken));
        var config = settings.RootElement;
        var port = config.GetProperty("Port").GetInt32();
        var graphPort = config.GetProperty("GraphPort").GetInt32();
        var model = config.GetProperty("Model").GetString()!;
        var ollama = config.GetProperty("OllamaUrl").GetString()!;
        if (port is < 1024 or > 65535 || graphPort is < 1024 or > 65535 || port == graphPort)
            throw new InvalidOperationException("Invalid Reports port configuration.");
        var uri = new Uri(ollama);
        if (uri.Scheme != "http" || !uri.IsLoopback || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Ollama must use a local HTTP endpoint.");
        // Never let a manually running planner or another web app impersonate
        // this installation. Refuse busy ports instead of killing their owner.
        foreach (var listenerPort in new[] { port, graphPort })
        {
            var listener = new TcpListener(IPAddress.Loopback, listenerPort);
            try { listener.Start(); }
            catch (SocketException error)
            { throw new InvalidOperationException($"Reports port {listenerPort} is already in use. Stop the other copy before starting the Reports service.", error); }
            finally { listener.Stop(); }
        }
        await Task.WhenAll(
            Supervise("web", Path.Combine(root, "web", "LaserficheReports.Web.exe"), [],
                new Dictionary<string, string> { ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}",
                    ["ReportsGraph__BaseUrl"] = $"http://127.0.0.1:{graphPort}",
                    ["LocalAI__ChatModel"] = model, ["LocalAI__BaseUrl"] = ollama }, data, stoppingToken),
            Supervise("planner", Path.Combine(root, "planner", "LaserficheReports.Planner.exe"),
                ["--model", model, "--port", graphPort.ToString(), "--ollama-url", ollama,
                 "--model-timeout-seconds", "0", "--planner-timeout-seconds", "0",
                 "--planner-trace-path", Path.Combine(data, "logs", "planner-trace.jsonl")], [], data, stoppingToken));
    }

    private async Task Supervise(string name, string executable, string[] arguments,
        Dictionary<string, string> variables, string data, CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo(executable)
            {
                WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            foreach (var (key, value) in variables) process.StartInfo.Environment[key] = value;
            var started = DateTimeOffset.UtcNow;
            try
            {
                process.Start();
                logger.LogInformation("Started Reports {Name} PID={Pid}", name, process.Id);
                var stdout = Drain(process.StandardOutput, name, data);
                var stderr = Drain(process.StandardError, name, data);
                await process.WaitForExitAsync(ct);
                await Task.WhenAll(stdout, stderr);
                logger.LogWarning("Reports {Name} exited with {ExitCode}", name, process.ExitCode);
                failures = DateTimeOffset.UtcNow - started > TimeSpan.FromMinutes(1) ? 1 : failures + 1;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception error) { failures++; logger.LogError(error, "Reports {Name} failed", name); }
            finally
            {
                // Only terminate child processes launched by this service.
                try { if (process.Id > 0 && !process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
                catch (InvalidOperationException) { }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(failures, 6)))), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    private static readonly object LogGate = new();
    private static async Task Drain(StreamReader reader, string name, string data)
    {
        var path = Path.Combine(data, "logs", name + ".log");
        while (await reader.ReadLineAsync() is { } line)
        {
            lock (LogGate)
            {
                if (File.Exists(path) && new FileInfo(path).Length > 5_000_000)
                    File.Move(path, path + ".previous", overwrite: true);
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {line}{Environment.NewLine}");
            }
        }
    }
}
