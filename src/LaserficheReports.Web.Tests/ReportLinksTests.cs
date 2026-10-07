using LaserficheReports.Web;
using Xunit;

public class ReportLinksTests
{
    [Fact]
    public void AdvertisedRepositoryQueryIsNormalizedBeforeBuildingEntryLinks()
    {
        var root = ReportLinks.DiscoveredBaseUrl("https://localhost/laserfiche?repo=TestEmployee");
        Assert.Equal("https://localhost/laserfiche", root);
        Assert.Equal("https://localhost/laserfiche/DocView.aspx?db=TestEmployee&id=619",
            ReportLinks.EntryUrl(root, "TestEmployee", 619));
        Assert.Throws<ArgumentException>(() => ReportLinks.DiscoveredBaseUrl("javascript:alert(1)"));
        Assert.Throws<ArgumentException>(() => ReportLinks.Build("https://localhost/laserfiche?repo=A", "A", [1]));
    }
    [Fact]
    public void SearchIncludesEveryDistinctDocumentAndEncodesRepository()
    {
        var url = Assert.Single(ReportLinks.Build("https://desktop-k1svi53/Laserfiche", "HR & المالية", [618, 42, 618]));
        Assert.StartsWith("https://desktop-k1svi53/Laserfiche/Browse.aspx?db=HR%20%26%20", url);
        Assert.Contains("{LF:ID=618} | {LF:ID=42}", Uri.UnescapeDataString(url));
        Assert.Contains("#?search=", url);
        Assert.DoesNotContain(";view=search", url);
    }
    [Fact]
    public void LargeReportsKeepAllDocumentsAcrossBoundedGroups()
    {
        var urls = ReportLinks.Build("https://localhost/laserfiche", "repo", Enumerable.Range(1, 1001));
        Assert.Equal(3, urls.Count);
        Assert.Contains("{LF:ID=1001}", Uri.UnescapeDataString(urls[2]));
        Assert.Empty(ReportLinks.Build("https://localhost/laserfiche", "repo", []));
    }
    [Fact]
    public void IndividualReferenceUsesDocumentQueryParametersAndFolderUsesBrowse()
    {
        Assert.Equal("https://lf.test/Laserfiche/DocView.aspx?db=HR%20%26%20Finance&id=618", ReportLinks.EntryUrl("https://lf.test/Laserfiche", "HR & Finance", 618));
        Assert.Equal("https://lf.test/Laserfiche/Browse.aspx?db=Repo#?id=10", ReportLinks.EntryUrl("https://lf.test/Laserfiche", "Repo", 10, true));
        Assert.Throws<ArgumentException>(() => ReportLinks.EntryUrl("javascript:alert(1)", "Repo", 618));
    }

    [Fact]
    public void SessionLicenseErrorIsNotReportedAsReadPermissionFailure()
    {
        var error = new LaserficheReports.Domain.Exceptions.LaserficheException("private", 429, "9030");
        var message = RepositoryReadError.Message(error);
        Assert.Contains("9030", message);
        Assert.Contains("Named User", message);
        Assert.DoesNotContain("صلاحية قراءة", message);
    }

    [Theory]
    [InlineData(401, "مصادقة")]
    [InlineData(403, "صلاحية")]
    [InlineData(504, "بوابته")]
    public void ReadErrorsIdentifyAuthenticationPermissionAndGatewaySeparately(int status, string expected)
    {
        var error = new LaserficheReports.Domain.Exceptions.LaserficheException("private upstream details", status);
        var message = RepositoryReadError.Message(error);
        Assert.Contains(expected, message);
        Assert.DoesNotContain("private", message);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:secret@localhost/laserfiche")]
    [InlineData("https://localhost/laserfiche?db=other")]
    public void RejectsUnsafeWebClientConfiguration(string value) =>
        Assert.Throws<ArgumentException>(() => ReportLinks.Build(value, "repo", [1]));

    [Theory]
    [InlineData("TestEmployee", true)]
    [InlineData("مستودع الموارد البشرية", true)]
    [InlineData("", false)]
    [InlineData("bad\nheader", false)]
    public void RepositoryValidation(string value, bool expected) =>
        Assert.Equal(expected, ReportSessionEndpoints.ValidRepository(value));
}
