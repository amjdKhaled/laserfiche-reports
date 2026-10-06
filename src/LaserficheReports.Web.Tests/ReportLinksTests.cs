using LaserficheReports.Web;
using Xunit;

public class ReportLinksTests
{
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
