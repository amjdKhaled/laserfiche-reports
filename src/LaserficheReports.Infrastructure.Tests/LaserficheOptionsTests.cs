using LaserficheReports.Infrastructure.Options;
using Xunit;

namespace LaserficheReports.Infrastructure.Tests;

public sealed class LaserficheOptionsTests
{
    [Fact]
    public void DefaultTimeout_IsBounded()
    {
        var options = new LaserficheOptions();

        Assert.Equal(120, options.TimeoutSeconds);
        Assert.Equal(45, options.EffectiveTimeoutSeconds);
    }

    [Fact]
    public void ShortTimeout_IsRespected()
    {
        var options = new LaserficheOptions { TimeoutSeconds = 30 };

        Assert.Equal(30, options.EffectiveTimeoutSeconds);
    }

    [Fact]
    public void LegacyLongTimeout_IsBounded()
    {
        var options = new LaserficheOptions { TimeoutSeconds = 240 };

        Assert.Equal(45, options.EffectiveTimeoutSeconds);
    }
}
