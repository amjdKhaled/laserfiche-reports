using LaserficheReports.Infrastructure.Services;
using Xunit;

namespace LaserficheReports.Infrastructure.Tests;

/// <summary>
/// Unit tests for <see cref="RepositoryJsonParser"/>.
/// Covers the V1 plain-array, V2 OData-envelope, and all unrecognised/invalid shapes.
/// These guard the regression: "HTTP 200 with V2 OData body must never throw a
/// JsonException or be silently rejected as incompatible."
/// </summary>
public sealed class RepositoryJsonParserTests
{
    // ── V1 plain-array shape ──────────────────────────────────────────────────

    [Fact]
    public void TryParse_V1PlainArray_ParsesCorrectly()
    {
        const string body = """
            [
              { "repoId": "Documents", "repoName": "Documents Library", "webclientUrl": "http://lf/Laserfiche" },
              { "repoId": "Archive",   "repoName": "Archive Repository", "webclientUrl": "http://lf/Laserfiche" }
            ]
            """;

        var repos = RepositoryJsonParser.TryParse(body, out var shape);

        Assert.NotNull(repos);
        Assert.Equal(RepositoryJsonParser.ShapeV1Array, shape);
        Assert.Equal(2, repos.Count);
        Assert.Equal("Documents", repos[0].RepoId);
        Assert.Equal("Documents Library", repos[0].RepoName);
        Assert.Equal("Archive", repos[1].RepoId);
    }

    [Fact]
    public void TryParse_V1EmptyArray_ReturnsEmptyList()
    {
        var repos = RepositoryJsonParser.TryParse("[]", out var shape);

        Assert.NotNull(repos);
        Assert.Equal(RepositoryJsonParser.ShapeV1Array, shape);
        Assert.Empty(repos);
    }

    [Fact]
    public void TryParse_V1Array_SetsShapeLabel_V1Array()
    {
        RepositoryJsonParser.TryParse("""[{"repoId":"X","repoName":"X","webclientUrl":""}]""", out var shape);
        Assert.Equal(RepositoryJsonParser.ShapeV1Array, shape);
    }

    // ── V2 OData-envelope shape ───────────────────────────────────────────────

    [Fact]
    public void TryParse_V2ODataWrapper_ParsesCorrectly()
    {
        const string body = """
            {
              "@odata.context": "http://lf-server/$metadata#Repositories",
              "value": [
                { "repoId": "NewEmployeeTest", "repoName": "New Employee Test", "webclientUrl": "http://lf/Laserfiche" }
              ]
            }
            """;

        var repos = RepositoryJsonParser.TryParse(body, out var shape);

        Assert.NotNull(repos);
        Assert.Equal(RepositoryJsonParser.ShapeV2OData, shape);
        Assert.Single(repos);
        Assert.Equal("NewEmployeeTest", repos[0].RepoId);
        Assert.Equal("New Employee Test", repos[0].RepoName);
    }

    [Fact]
    public void TryParse_V2ODataWrapper_MinimalBody_ParsesCorrectly()
    {
        // Minimal V2 body — no @odata.context.
        const string body = """{"value":[{"repoId":"TestRepo","repoName":"Test","webclientUrl":""}]}""";

        var repos = RepositoryJsonParser.TryParse(body, out var shape);

        Assert.NotNull(repos);
        Assert.Equal(RepositoryJsonParser.ShapeV2OData, shape);
        Assert.Single(repos);
        Assert.Equal("TestRepo", repos[0].RepoId);
    }

    [Fact]
    public void TryParse_V2ODataWrapper_EmptyValueArray_ReturnsEmptyList()
    {
        const string body = """{"@odata.context":"http://lf/$metadata","value":[]}""";

        var repos = RepositoryJsonParser.TryParse(body, out var shape);

        Assert.NotNull(repos);
        Assert.Equal(RepositoryJsonParser.ShapeV2OData, shape);
        Assert.Empty(repos);
    }

    [Fact]
    public void TryParse_V2ODataWrapper_SetsShapeLabel_V2OData()
    {
        RepositoryJsonParser.TryParse("""{"value":[]}""", out var shape);
        Assert.Equal(RepositoryJsonParser.ShapeV2OData, shape);
    }

    // ── Unrecognised / invalid shapes (must return null, never throw) ─────────

    [Fact]
    public void TryParse_InvalidJson_ReturnsNull()
    {
        var repos = RepositoryJsonParser.TryParse("not json at all <<<", out var shape);

        Assert.Null(repos);
        Assert.Equal("invalid-json", shape);
    }

    [Fact]
    public void TryParse_HtmlBody_ReturnsNull()
    {
        // A reverse-proxy might return an HTML error page with HTTP 200.
        const string html = "<html><body><h1>Service Unavailable</h1></body></html>";

        var repos = RepositoryJsonParser.TryParse(html, out var shape);

        Assert.Null(repos);
        Assert.Contains("invalid-json", shape);  // HTML is not valid JSON
    }

    [Fact]
    public void TryParse_JsonErrorObject_ReturnsNull()
    {
        // An API error response (JSON object that is NOT an OData envelope).
        const string body = """{"error":"unauthorized","code":401}""";

        var repos = RepositoryJsonParser.TryParse(body, out var shape);

        Assert.Null(repos);
        Assert.Contains(RepositoryJsonParser.ShapeUnknown, shape);
    }

    [Fact]
    public void TryParse_ODataObjectWithNonArrayValue_ReturnsNull()
    {
        // A "value" property exists but it is a string, not an array.
        const string body = """{"value":"repositories not available"}""";

        var repos = RepositoryJsonParser.TryParse(body, out var shape);

        Assert.Null(repos);
        Assert.Contains(RepositoryJsonParser.ShapeUnknown, shape);
    }

    [Fact]
    public void TryParse_EmptyBody_ReturnsNull()
    {
        var repos = RepositoryJsonParser.TryParse(string.Empty, out var shape);

        Assert.Null(repos);
        Assert.Equal("empty", shape);
    }

    [Fact]
    public void TryParse_WhitespaceBody_ReturnsNull()
    {
        var repos = RepositoryJsonParser.TryParse("   \t\n", out var shape);

        Assert.Null(repos);
        Assert.Equal("empty", shape);
    }

    // ── Exact real V2 response from the Laserfiche server ────────────────────
    //    Property names are: "id", "name", "webClientUrl" (camelCase).
    //    This was the body causing "()" in the Discover dropdown.

    [Fact]
    public void TryParse_RealV2Response_FourRepos_ParsesAllCorrectly()
    {
        // Exact shape observed on the real Laserfiche machine.
        const string body = """
            {
              "@odata.context": "https://localhost/LFRepositoryAPI/v2/$metadata#Repositories",
              "value": [
                {
                  "@odata.type": "#Laserfiche.Repository.Repository",
                  "id": "LFNewRepoWF",
                  "name": "LFNewRepoWF",
                  "webClientUrl": "http://localhost/laserfiche?repo=LFNewRepoWF"
                },
                {
                  "id": "NewEmployeeTest",
                  "name": "NewEmployeeTest",
                  "webClientUrl": "http://localhost/laserfiche?repo=NewEmployeeTest"
                },
                {
                  "id": "NewLFWorkflow",
                  "name": "NewLFWorkflow",
                  "webClientUrl": "http://localhost/laserfiche?repo=NewLFWorkflow"
                },
                {
                  "id": "TestEmployee",
                  "name": "TestEmployee",
                  "webClientUrl": "http://localhost/laserfiche?repo=TestEmployee"
                }
              ]
            }
            """;

        var repos = RepositoryJsonParser.TryParse(body, out var shape);

        Assert.NotNull(repos);
        Assert.Equal(RepositoryJsonParser.ShapeV2OData, shape);
        Assert.Equal(4, repos.Count);

        Assert.Equal("LFNewRepoWF",    repos[0].RepoId);
        Assert.Equal("LFNewRepoWF",    repos[0].RepoName);

        Assert.Equal("NewEmployeeTest", repos[1].RepoId);
        Assert.Equal("NewEmployeeTest", repos[1].RepoName);

        Assert.Equal("NewLFWorkflow",   repos[2].RepoId);
        Assert.Equal("TestEmployee",    repos[3].RepoId);
    }

    [Fact]
    public void TryParse_RealV2Response_NoBlankIds()
    {
        // This ensures the Discover dropdown never shows blank entries.
        const string body = """
            {
              "value": [
                { "id": "LFNewRepoWF",    "name": "LFNewRepoWF",    "webClientUrl": "" },
                { "id": "NewEmployeeTest","name": "NewEmployeeTest", "webClientUrl": "" },
                { "id": "NewLFWorkflow",  "name": "NewLFWorkflow",   "webClientUrl": "" },
                { "id": "TestEmployee",   "name": "TestEmployee",    "webClientUrl": "" }
              ]
            }
            """;

        var repos = RepositoryJsonParser.TryParse(body, out _)!;

        foreach (var r in repos)
        {
            Assert.False(string.IsNullOrWhiteSpace(r.RepoId),
                "RepoId must not be blank — this causes '()' in the Discover dropdown.");
            Assert.False(string.IsNullOrWhiteSpace(r.RepoName),
                "RepoName must not be blank — this causes '()' in the Discover dropdown.");
        }
    }

    [Fact]
    public void TryParse_V2RealShape_IsCompatibleShape_ReturnsTrue()
    {
        const string body = """
            {
              "@odata.context": "https://localhost/LFRepositoryAPI/v2/$metadata#Repositories",
              "value": [
                { "id": "LFNewRepoWF", "name": "LFNewRepoWF", "webClientUrl": "" }
              ]
            }
            """;

        Assert.True(RepositoryJsonParser.IsCompatibleShape(body));
    }

    // ── IsCompatibleShape ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("""[{"repoId":"X","repoName":"X","webclientUrl":""}]""")]
    [InlineData("[]")]
    public void IsCompatibleShape_V1Array_ReturnsTrue(string body)
    {
        Assert.True(RepositoryJsonParser.IsCompatibleShape(body));
    }

    [Theory]
    [InlineData("""{"value":[{"repoId":"X","repoName":"X","webclientUrl":""}]}""")]
    [InlineData("""{"@odata.context":"http://x","value":[]}""")]
    public void IsCompatibleShape_V2ODataEnvelope_ReturnsTrue(string body)
    {
        Assert.True(RepositoryJsonParser.IsCompatibleShape(body));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"error":"unauthorized"}""")]
    [InlineData("<html></html>")]
    [InlineData("""{"value":"not-an-array"}""")]
    public void IsCompatibleShape_UnrecognisedBodies_ReturnsFalse(string body)
    {
        Assert.False(RepositoryJsonParser.IsCompatibleShape(body));
    }

    // ── Requirement 9: HTTP 200 with incompatible JSON must not mark version as compatible ──
    //
    // This directly tests the invariant that IsCompatibleShape is used by
    // ApiVersionDetectionService.RouteExistsAsync to reject HTTP 200 responses that
    // contain an error object, HTML, or any other non-repository-list shape.

    [Fact]
    public void IsCompatibleShape_JsonErrorObject_ReturnsFalse_IncompatibleVersionMustBeRejected()
    {
        // This body is what a misconfigured reverse-proxy might return with HTTP 200.
        // Auto-detect must NOT accept this as "v2 available" — it's an error response.
        const string proxyErrorBody = """
            {
              "error": "Internal Server Error",
              "status": 500,
              "message": "Proxy configuration error"
            }
            """;

        Assert.False(
            RepositoryJsonParser.IsCompatibleShape(proxyErrorBody),
            "A JSON error object with HTTP 200 must not be accepted as a compatible repository list.");
    }
}
