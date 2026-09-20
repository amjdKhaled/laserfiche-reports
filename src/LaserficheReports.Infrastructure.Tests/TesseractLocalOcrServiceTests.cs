using LaserficheReports.Infrastructure.Options;
using LaserficheReports.Infrastructure.Services;
using Xunit;

namespace LaserficheReports.Infrastructure.Tests;

public sealed class TesseractLocalOcrServiceTests
{
    [Fact]
    public void CreateStartInfo_UsesStreamingArabicAndEnglishArguments()
    {
        var options = new OcrOptions
        {
            Languages = "ara+eng",
            PageSegmentationMode = 4,
            Dpi = 300
        };

        var startInfo = TesseractLocalOcrService.CreateStartInfo("tesseract.exe", options);

        Assert.Equal("tesseract.exe", startInfo.FileName);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.Equal(
            new[]
            {
                "stdin", "stdout", "-l", "ara+eng", "--psm", "4",
                "--dpi", "300", "-c", "preserve_interword_spaces=1"
            },
            startInfo.ArgumentList.ToArray());
    }

    [Theory]
    [InlineData("  Arabic text\r\nEnglish text  ", "Arabic text\nEnglish text")]
    [InlineData("\u200EInv#\u200F 3521\n\uFFFD العربية", "Inv# 3521\nالعربية")]
    [InlineData("Description     price   Qty   Total", "Description     price   Qty   Total")]
    [InlineData("   ", "")]
    public void NormalizeText_NormalizesLineEndingsAndWhitespace(string input, string expected)
    {
        Assert.Equal(expected, TesseractLocalOcrService.NormalizeText(input));
    }

    [Fact]
    public void SelectBestCandidate_PrefersTableModeWhenScoresAreClose()
    {
        TesseractLocalOcrService.OcrCandidate[] candidates =
        [
            new(4, "table layout", 90),
            new(6, "slightly longer but scrambled", 100)
        ];

        var selected = TesseractLocalOcrService.SelectBestCandidate(candidates, 4);

        Assert.Equal(4, selected.PageSegmentationMode);
    }

    [Fact]
    public void SelectBestCandidate_UsesFallbackWhenMateriallyBetter()
    {
        TesseractLocalOcrService.OcrCandidate[] candidates =
        [
            new(4, "weak", 40),
            new(6, "better", 100)
        ];

        var selected = TesseractLocalOcrService.SelectBestCandidate(candidates, 4);

        Assert.Equal(6, selected.PageSegmentationMode);
    }
}
