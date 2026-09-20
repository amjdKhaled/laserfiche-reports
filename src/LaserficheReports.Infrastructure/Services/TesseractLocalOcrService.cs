using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LaserficheReports.Infrastructure.Services;

/// <summary>
/// Runs local Tesseract OCR with page-layout fallbacks. Page images are normalized
/// in memory and are never written to disk or sent outside the local machine.
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
        if (!_options.Enabled) return null;

        using var source = new MemoryStream();
        await imageContent.CopyToAsync(source, cancellationToken).ConfigureAwait(false);
        var imageBytes = PrepareImage(source.ToArray(), _options.EffectiveImageScaleFactor, _options.EffectiveDpi);

        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(_options.EffectiveTimeoutSeconds));
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeout.Token);

        var candidates = new List<OcrCandidate>();
        try
        {
            foreach (var pageSegmentationMode in _options.EffectivePageSegmentationModes)
            {
                var text = await RunTesseractAsync(
                    imageBytes,
                    pageSegmentationMode,
                    linkedCancellation.Token).ConfigureAwait(false);

                if (text.Length >= _options.EffectiveMinimumTextLength)
                    candidates.Add(new OcrCandidate(pageSegmentationMode, text, ScoreText(text)));
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Local OCR timed out after {TimeoutSeconds} seconds.", _options.EffectiveTimeoutSeconds);
            return null;
        }

        if (candidates.Count == 0) return null;

        var best = SelectBestCandidate(candidates, _options.EffectivePageSegmentationMode);
        _logger.LogInformation(
            "Local OCR selected page segmentation mode {PageSegmentationMode} from {CandidateCount} candidates.",
            best.PageSegmentationMode,
            candidates.Count);
        return best.Text;
    }

    private async Task<string> RunTesseractAsync(
        byte[] imageBytes,
        int pageSegmentationMode,
        CancellationToken cancellationToken)
    {
        var executablePath = string.IsNullOrWhiteSpace(_options.ExecutablePath)
            ? "tesseract"
            : _options.ExecutablePath.Trim();
        using var process = new Process
        {
            StartInfo = CreateStartInfo(executablePath, _options, pageSegmentationMode),
            EnableRaisingEvents = true
        };

        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"Local OCR could not start Tesseract at {executablePath}.");
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException(
                $"Tesseract was not found at {executablePath}. Configure Ocr:ExecutablePath.",
                exception);
        }

        try
        {
            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.StandardInput.BaseStream
                .WriteAsync(imageBytes, cancellationToken)
                .ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await standardOutput.ConfigureAwait(false);
            var error = await standardError.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                _logger.LogWarning(
                    "Local OCR pass PSM {PageSegmentationMode} failed with exit code {ExitCode}. Details: {Error}",
                    pageSegmentationMode,
                    process.ExitCode,
                    LimitForLog(error));
                return string.Empty;
            }

            return NormalizeText(output);
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
            return string.Empty;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(
        string executablePath,
        OcrOptions options,
        int? pageSegmentationMode = null)
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
        startInfo.ArgumentList.Add((pageSegmentationMode ?? options.EffectivePageSegmentationMode)
            .ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--dpi");
        startInfo.ArgumentList.Add(options.EffectiveDpi.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("preserve_interword_spaces=1");
        return startInfo;
    }

    internal static string NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\u00A0', ' ');
        var result = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (character is '\u200E' or '\u200F' or '\u202A' or '\u202B' or '\u202C' or
                '\u202D' or '\u202E' or '\u2066' or '\u2067' or '\u2068' or '\u2069' or
                '\uFEFF' or '\uFFFD')
            {
                continue;
            }

            result.Append(character);
        }

        return string.Join('\n', result.ToString()
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0))
            .Trim();
    }

    internal static int ScoreText(string text)
    {
        var lettersOrDigits = text.Count(char.IsLetterOrDigit);
        var arabicCharacters = text.Count(character => character is >= '\u0600' and <= '\u06FF');
        var usefulLines = text.Split('\n').Count(line => line.Count(char.IsLetterOrDigit) >= 3);
        return lettersOrDigits + arabicCharacters + (usefulLines * 8);
    }

    internal static OcrCandidate SelectBestCandidate(
        IReadOnlyList<OcrCandidate> candidates,
        int preferredPageSegmentationMode)
    {
        if (candidates.Count == 0) throw new ArgumentException("At least one OCR candidate is required.", nameof(candidates));

        var best = candidates.OrderByDescending(candidate => candidate.Score).First();
        var preferred = candidates.FirstOrDefault(
            candidate => candidate.PageSegmentationMode == preferredPageSegmentationMode);

        // Prefer the table-aware mode when its useful-text score is close to the
        // highest score. This avoids selecting a longer but scrambled fallback.
        return preferred is not null && preferred.Score >= best.Score * 0.85
            ? preferred
            : best;
    }

    private byte[] PrepareImage(byte[] source, int scaleFactor, int dpi)
    {
        if (scaleFactor <= 1) return source;

        try
        {
            using var input = new MemoryStream(source);
            using var image = Image.FromStream(input);
            using var bitmap = new Bitmap(
                image.Width * scaleFactor,
                image.Height * scaleFactor,
                PixelFormat.Format24bppRgb);
            bitmap.SetResolution(dpi, dpi);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.White);
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.DrawImage(image, 0, 0, bitmap.Width, bitmap.Height);
            }

            using var output = new MemoryStream();
            bitmap.Save(output, ImageFormat.Png);
            return output.ToArray();
        }
        catch (Exception exception) when (exception is ArgumentException or ExternalException)
        {
            _logger.LogWarning(exception, "OCR image normalization failed; using the original page image.");
            return source;
        }
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
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Process already exited.
        }
    }

    internal sealed record OcrCandidate(int PageSegmentationMode, string Text, int Score);
}
