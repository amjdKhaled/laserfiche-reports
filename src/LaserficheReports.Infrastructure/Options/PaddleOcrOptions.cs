namespace LaserficheReports.Infrastructure.Options;

public sealed class PaddleOcrOptions
{
    public const string SectionName = "Ocr";

    public bool Enabled { get; init; } = true;
    public string BaseUrl { get; init; } = "http://127.0.0.1:8765";
    public int TimeoutSeconds { get; init; } = 1800;
    public int MinimumTextLength { get; init; } = 3;
    public int MaxImageSizeMegabytes { get; init; } = 50;
    public int MaxFallbackPages { get; init; } = 100;

    internal int EffectiveTimeoutSeconds => Math.Clamp(TimeoutSeconds, 30, 1800);
    internal int EffectiveMinimumTextLength => Math.Clamp(MinimumTextLength, 1, 1000);
    internal long EffectiveMaxImageBytes => Math.Clamp(MaxImageSizeMegabytes, 1, 200) * 1024L * 1024L;
    internal int EffectiveMaxFallbackPages => Math.Clamp(MaxFallbackPages, 1, 1000);
}
