using LaserficheReports.Application.DTOs;
using LaserficheReports.Web.Permissions;
using Xunit;

namespace LaserficheReports.Web.Tests;

public sealed class FullHierarchyPreflightTests
{
    private static readonly RepositoryDescriptor Repository = new("selected", "https://localhost", "testemployee", "testemployee");

    [Fact]
    public void MissingBindingReportsDeploymentProblemWithoutChangingAuthorization()
    {
        var status = FullHierarchyPreflight.Check(Repository, new PermissionVerificationOptions(), true);
        Assert.Equal("repository_binding_missing_or_duplicate", status);
    }

    [Fact]
    public void WrongServerOrIncompleteNativeNamesAreNotReportedReady()
    {
        var options = ValidOptions();
        options.Repositories["testemployee"].ApiServerUrl = "https://other-server";
        Assert.Equal("repository_binding_invalid", FullHierarchyPreflight.Check(Repository, options, true));
        options.Repositories["testemployee"].ApiServerUrl = "https://localhost";
        options.Repositories["testemployee"].ServerName = "";
        Assert.Equal("repository_binding_invalid", FullHierarchyPreflight.Check(Repository, options, true));
    }

    [Fact]
    public void MissingSdkPayloadIsDifferentFromMissingBinding()
    {
        Assert.Equal("sdk_bridge_payload_missing_or_platform_unsupported",
            FullHierarchyPreflight.Check(Repository, ValidOptions(), false));
    }

    [Fact]
    public void ValidPreflightStillRequiresRealSdkAuthorization()
    {
        Assert.Equal("ready_for_live_validation", FullHierarchyPreflight.Check(Repository, ValidOptions(), true));
    }

    private static PermissionVerificationOptions ValidOptions() => new()
    {
        Repositories = new Dictionary<string, PermissionRepositoryBinding>
        {
            ["testemployee"] = new()
            {
                ApiServerUrl = "https://localhost",
                ServerName = "laserfiche-server",
                RepositoryName = "testemployee"
            }
        }
    };
}
