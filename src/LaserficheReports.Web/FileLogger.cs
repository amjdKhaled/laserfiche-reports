using System.Text;

namespace LaserficheReports.Web;

/// <summary>
/// Daily log files (web-yyyyMMdd.log) kept for <see cref="RetentionDays"/> days. Under IIS the
/// console log is discarded, so this is the only complete record of a failed request.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    internal const int RetentionDays = 14;
    private readonly string directory;
    private readonly object gate = new();
    private StreamWriter? writer;
    private DateOnly day;

    public FileLoggerProvider(string directory)
    {
        this.directory = directory;
        Directory.CreateDirectory(directory);
        foreach (var old in Directory.EnumerateFiles(directory, "web-*.log"))
            if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-RetentionDays)) TryDelete(old);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(LogLevel level, string category, EventId eventId, string message, Exception? exception)
    {
        var now = DateTimeOffset.Now;
        var line = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")).Append(' ')
            .Append(level switch
            {
                LogLevel.Trace => "TRCE", LogLevel.Debug => "DBUG", LogLevel.Information => "INFO",
                LogLevel.Warning => "WARN", LogLevel.Error => "FAIL", _ => "CRIT"
            })
            .Append(' ').Append(category).Append(": ").Append(message);
        if (exception is not null) line.AppendLine().Append(exception);
        lock (gate)
        {
            try
            {
                var today = DateOnly.FromDateTime(now.DateTime);
                if (writer is null || today != day)
                {
                    writer?.Dispose();
                    day = today;
                    writer = new StreamWriter(new FileStream(Path.Combine(directory, $"web-{today:yyyyMMdd}.log"),
                        FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = true };
                }
                writer.WriteLine(line.ToString());
            }
            catch (IOException) { /* Logging must never fail a request. */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void Dispose()
    {
        lock (gate) { writer?.Dispose(); writer = null; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            provider.Write(logLevel, category, eventId, formatter(state, exception), exception);
        }
    }
}
