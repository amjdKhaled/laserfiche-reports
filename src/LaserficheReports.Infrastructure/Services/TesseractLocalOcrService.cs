using System.ComponentModel;
using System.Diagnostics;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LaserficheReports.Infrastructure.Services;

/// <summary>
/// Runs the local Tesseract command-line engine with image bytes streamed through
/// standard input. No document image is written to disk or sent over the network.
/// </summary>
internal sealed class TesseractLocalOcrService : ILocalOcrService
{
    private readonly OcrOptions _options;
    private readonly ILogger<TesseractLocalOcrService> _logger;

    public TesseractLocalOcrService(
        IOptions<OcrOptions> options,
        ILogger<TesseractLocalOcrService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string?> TryExtractTextAsync(
        Stream imageContent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageContent);

        if (!_options.Enabled)
            return null;

        var executablePath = string.IsNullOrWhiteSpace(_options.ExecutablePath)
            ? "tesseract"
            : _options.ExecutablePath.Trim();

        using var process = new Process
        {
            StartInfo = CreateStartInfo(executablePath, _options),
            EnableRaisingEvents = true
        };

        try
        {
            if (!process.Start())
            {
                _logger.LogWarning("Local OCR could not start Tesseract at {ExecutablePath}.", executablePath);
                return null;
            }
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException)
        {
            _logger.LogWarning(
                "Local OCR is unavailable because Tesseract was not found at {ExecutablePath}. " +
                "Install Tesseract locally or configure Ocr:ExecutablePath.",
                executablePath);
            return null;
        }

        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(_options.EffectiveTimeoutSeconds));
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeout.Token);

        try
        {
            var standardOutput = process.StandardOutput.ReadToEndAsync(linkedCancellation.Token);
            var standardError = process.StandardError.ReadToEndAsync(linkedCancellation.Token);

            await imageContent
                .CopyToAsync(process.StandardInput.BaseStream, linkedCancellation.Token)
                .ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(linkedCancellation.Token).ConfigureAwait(false);
            process.StandardInput.Close();

            await process.WaitForExitAsync(linkedCancellation.Token).ConfigureAwait(false);
            var output = await standardOutput.ConfigureAwait(false);
            var error = await standardError.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                _logger.LogWarning(
                    "Local OCR failed with Tesseract exit code {ExitCode}. Details: {Error}",
                    process.ExitCode,
                    LimitForLog(error));
                return null;
            }

            var normalized = NormalizeText(output);
            if (normalized.Length < _options.EffectiveMinimumTextLength)
                return null;

            return normalized;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            _logger.LogWarning(
                "Local OCR timed out after {TimeoutSeconds} seconds.",
                _options.EffectiveTimeoutSeconds);
            return null;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (IOException exception)
        {
            TryKill(process);
            _logger.LogWarning(exception, "Local OCR could not stream the page to Tesseract.");
            return null;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string executablePath, OcrOptions options)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add("stdin");
        startInfo.ArgumentList.Add("stdout");
        startInfo.ArgumentList.Add("-l");
        startInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(options.Languages) ? "ara+eng" : options.Languages.Trim());
        startInfo.ArgumentList.Add("--psm");
        startInfo.ArgumentList.Add(options.EffectivePageSegmentationMode.ToString(
            System.Globalization.CultureInfo.InvariantCulture));

        return startInfo;
    }

    internal static string NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }

    private static string LimitForLog(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "(none)";
        var trimmed = value.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed[..500];
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Process already exited.
        }
    }
}
