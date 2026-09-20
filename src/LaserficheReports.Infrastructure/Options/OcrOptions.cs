namespace LaserficheReports.Infrastructure.Options;

public sealed class OcrOptions
{
    public const string SectionName = "Ocr";

    public bool Enabled { get; init; } = true;
    public string ExecutablePath { get; init; } = "tesseract";
    public string Languages { get; init; } = "ara+eng";
    public int PageSegmentationMode { get; init; } = 6;
    public int TimeoutSeconds { get; init; } = 120;
    public int MinimumTextLength { get; init; } = 3;

    internal int EffectiveTimeoutSeconds => Math.Clamp(TimeoutSeconds, 5, 600);
    internal int EffectiveMinimumTextLength => Math.Clamp(MinimumTextLength, 1, 1000);
    internal int EffectivePageSegmentationMode => Math.Clamp(PageSegmentationMode, 0, 13);
}
