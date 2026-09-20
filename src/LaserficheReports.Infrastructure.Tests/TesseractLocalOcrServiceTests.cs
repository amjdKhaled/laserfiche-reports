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
            PageSegmentationMode = 6
        };

        var startInfo = TesseractLocalOcrService.CreateStartInfo("tesseract.exe", options);

        Assert.Equal("tesseract.exe", startInfo.FileName);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.Equal(
            new[] { "stdin", "stdout", "-l", "ara+eng", "--psm", "6" },
            startInfo.ArgumentList.ToArray());
    }

    [Theory]
    [InlineData("  Arabic text\r\nEnglish text  ", "Arabic text\nEnglish text")]
    [InlineData("   ", "")]
    public void NormalizeText_NormalizesLineEndingsAndWhitespace(string input, string expected)
    {
        Assert.Equal(expected, TesseractLocalOcrService.NormalizeText(input));
    }
}
