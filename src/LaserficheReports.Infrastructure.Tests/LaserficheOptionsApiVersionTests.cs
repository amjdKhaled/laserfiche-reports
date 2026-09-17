using LaserficheReports.Infrastructure.Options;
using Xunit;

namespace LaserficheReports.Infrastructure.Tests;

/// <summary>
/// Verifies the Auto/v1/v2 API version resolution rules in
/// <see cref="LaserficheOptions.EffectiveApiVersion"/>.
/// </summary>
public class LaserficheOptionsApiVersionTests
{
    [Fact]
    public void Default_Is_Auto()
    {
        var opts = new LaserficheOptions();
        Assert.True(opts.IsAutoApiVersion);
        Assert.Equal("Auto", opts.ApiVersion);
    }

    [Theory]
    [InlineData("Auto")]
    [InlineData("auto")]
    [InlineData("AUTO")]
    [InlineData("")]
    [InlineData("   ")]
    public void Auto_Without_Detection_Falls_Back_To_V1(string configured)
    {
        var opts = new LaserficheOptions { ApiVersion = configured };
        Assert.True(opts.IsAutoApiVersion);
        Assert.Equal("v1", opts.EffectiveApiVersion);
    }

    [Theory]
    [InlineData("v1")]
    [InlineData("v2")]
    public void Auto_With_Detection_Uses_Detected_Version(string detected)
    {
        var opts = new LaserficheOptions { ApiVersion = "Auto", DetectedApiVersion = detected };
        Assert.Equal(detected, opts.EffectiveApiVersion);
    }

    [Theory]
    [InlineData("v1")]
    [InlineData("v2")]
    public void Explicit_Pin_Wins_Over_Detected_Version(string pinned)
    {
        var opts = new LaserficheOptions
        {
            ApiVersion         = pinned,
            DetectedApiVersion = pinned == "v1" ? "v2" : "v1"  // opposite — must be ignored
        };
        Assert.False(opts.IsAutoApiVersion);
        Assert.Equal(pinned, opts.EffectiveApiVersion);
    }

    [Fact]
    public void Whitespace_Detected_Version_Is_Treated_As_Not_Detected()
    {
        var opts = new LaserficheOptions { ApiVersion = "Auto", DetectedApiVersion = "  " };
        Assert.Equal("v1", opts.EffectiveApiVersion);
    }

    [Fact]
    public void Effective_Version_Is_Trimmed()
    {
        var opts = new LaserficheOptions { ApiVersion = " v2 " };
        Assert.Equal("v2", opts.EffectiveApiVersion);
    }
}
