namespace LaserficheReports.Infrastructure.Options;

public sealed class OcrOptions
{
    public const string SectionName = "Ocr";

    public bool Enabled { get; init; } = true;
    public string ExecutablePath { get; init; } = "tesseract";
    public string Languages { get; init; } = "ara+eng";
    public int PageSegmentationMode { get; init; } = 4;
    public int[] FallbackPageSegmentationModes { get; init; } = [6];
    public int ImageScaleFactor { get; init; } = 2;
    public int Dpi { get; init; } = 300;
    public int TimeoutSeconds { get; init; } = 120;
    public int MinimumTextLength { get; init; } = 3;

    internal int EffectiveTimeoutSeconds => Math.Clamp(TimeoutSeconds, 5, 600);
    internal int EffectiveMinimumTextLength => Math.Clamp(MinimumTextLength, 1, 1000);
    internal int EffectivePageSegmentationMode => Math.Clamp(PageSegmentationMode, 0, 13);
    internal int EffectiveImageScaleFactor => Math.Clamp(ImageScaleFactor, 1, 3);
    internal int EffectiveDpi => Math.Clamp(Dpi, 70, 600);
    internal IReadOnlyList<int> EffectivePageSegmentationModes =>
        new[] { EffectivePageSegmentationMode }
            .Concat(FallbackPageSegmentationModes ?? [])
            .Select(mode => Math.Clamp(mode, 0, 13))
            .Distinct()
            .ToArray();
}
