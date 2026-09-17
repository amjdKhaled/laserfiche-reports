using LaserficheReports.Infrastructure.Options;
using Xunit;

namespace LaserficheReports.Infrastructure.Tests;

public sealed class LaserficheOptionsTests
{
    [Fact]
    public void DefaultTimeout_AllowsSlowRepositoryRequests()
    {
        var options = new LaserficheOptions();

        Assert.Equal(120, options.TimeoutSeconds);
        Assert.Equal(120, options.EffectiveTimeoutSeconds);
    }

    [Fact]
    public void LegacyThirtySecondSetting_IsRaisedAtRuntime()
    {
        var options = new LaserficheOptions { TimeoutSeconds = 30 };

        Assert.Equal(120, options.EffectiveTimeoutSeconds);
    }

    [Fact]
    public void ExplicitLongerTimeout_IsPreserved()
    {
        var options = new LaserficheOptions { TimeoutSeconds = 240 };

        Assert.Equal(240, options.EffectiveTimeoutSeconds);
    }
}
