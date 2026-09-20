using LaserficheReports.Infrastructure.Adapters;
using LaserficheReports.Infrastructure.Options;
using Microsoft.Extensions.Options;
using Xunit;

namespace LaserficheReports.Infrastructure.Tests;

/// <summary>
/// URL-builder tests for <see cref="LaserficheApiAdapter"/>. These guard the
/// exact concern from the login-failure investigation: the configured
/// <c>ApiBasePath</c> (<c>/LFRepositoryAPI</c>) must appear exactly once in
/// every built URL, regardless of how the ServerUrl was entered.
/// </summary>
public sealed class LaserficheApiAdapterUrlTests
{
    private static LaserficheApiAdapter CreateAdapter(
        string serverUrl,
        string apiBasePath = "/LFRepositoryAPI",
        string apiVersion  = "v1")
    {
        var options = new LaserficheOptions
        {
            ServerUrl    = serverUrl,
            ApiBasePath  = apiBasePath,
            ApiVersion   = apiVersion,
            RepositoryId = "Documents"
        };
        return new LaserficheApiAdapter(new StaticOptionsMonitor(options));
    }

    // ── Token URL (the login endpoint) ───────────────────────────────────────

    [Fact]
    public void TokenUrl_PlainServerUrl_ContainsBasePathOnce()
    {
        var adapter = CreateAdapter("https://lf-server.corp.local");
        var url = adapter.BuildTokenUrl("Documents");

        Assert.Equal(
            "https://lf-server.corp.local/LFRepositoryAPI/v1/Repositories/Documents/Token",
            url);
    }

    [Fact]
    public void TokenUrl_ServerUrlAlreadyEndsWithBasePath_DoesNotDuplicate()
    {
        var adapter = CreateAdapter("https://lf-server.corp.local/LFRepositoryAPI");
        var url = adapter.BuildTokenUrl("Documents");

        Assert.Equal(
            "https://lf-server.corp.local/LFRepositoryAPI/v1/Repositories/Documents/Token",
            url);
        Assert.Equal(1, CountOccurrences(url, "/LFRepositoryAPI"));
    }

    [Fact]
    public void TokenUrl_ServerUrlWithTrailingSlashes_IsNormalised()
    {
        var adapter = CreateAdapter("https://lf-server.corp.local/LFRepositoryAPI///");
        var url = adapter.BuildTokenUrl("Documents");

        Assert.Equal(
            "https://lf-server.corp.local/LFRepositoryAPI/v1/Repositories/Documents/Token",
            url);
    }

    [Fact]
    public void TokenUrl_BasePathCaseInsensitiveMatch_DoesNotDuplicate()
    {
        var adapter = CreateAdapter("https://lf-server.corp.local/lfrepositoryapi");
        var url = adapter.BuildTokenUrl("Documents");

        // Only one base-path segment, whatever its casing was.
        Assert.Equal(1, CountOccurrencesIgnoreCase(url, "/LFRepositoryAPI"));
        Assert.EndsWith("/v1/Repositories/Documents/Token", url);
    }

    [Fact]
    public void TokenUrlFor_ExplicitServerUrl_ContainsBasePathOnce()
    {
        var adapter = CreateAdapter("https://ignored.example");
        var url = adapter.BuildTokenUrlFor(
            "https://other-server.corp.local/LFRepositoryAPI", "Archive");

        Assert.Equal(
            "https://other-server.corp.local/LFRepositoryAPI/v1/Repositories/Archive/Token",
            url);
    }

    // ── Repositories URL (the diagnostics probe endpoint) ────────────────────

    [Fact]
    public void RepositoriesUrl_PlainServerUrl_IsCorrect()
    {
        var adapter = CreateAdapter("https://lf-server.corp.local");
        Assert.Equal(
            "https://lf-server.corp.local/LFRepositoryAPI/v1/Repositories",
            adapter.BuildRepositoriesUrl());
    }

    [Fact]
    public void RepositoriesUrl_ServerUrlWithBasePath_DoesNotDuplicate()
    {
        var adapter = CreateAdapter("https://lf-server.corp.local/LFRepositoryAPI/");
        var url = adapter.BuildRepositoriesUrl();

        Assert.Equal(
            "https://lf-server.corp.local/LFRepositoryAPI/v1/Repositories", url);
        Assert.Equal(1, CountOccurrences(url, "/LFRepositoryAPI"));
    }

    // ── Non-default base path and port ────────────────────────────────────────

    [Fact]
    public void TokenUrl_CustomBasePathAndPort_AreRespected()
    {
        var adapter = CreateAdapter(
            "http://lf-server.corp.local:8080", apiBasePath: "CustomApi");
        var url = adapter.BuildTokenUrl("Documents");

        Assert.Equal(
            "http://lf-server.corp.local:8080/CustomApi/v1/Repositories/Documents/Token",
            url);
    }

    [Fact]
    public void TokenUrl_ApiVersionWithSlashes_IsTrimmed()
    {
        var adapter = CreateAdapter("https://lf-server.corp.local", apiVersion: "/v1/");
        var url = adapter.BuildTokenUrl("Documents");

        Assert.Equal(
            "https://lf-server.corp.local/LFRepositoryAPI/v1/Repositories/Documents/Token",
            url);
    }

    // ── API version v2 ───────────────────────────────────────────────────────

    [Fact]
    public void TokenUrl_V2_ContainsV2Segment()
    {
        var adapter = CreateAdapter("https://lf-server.corp.local", apiVersion: "v2");
        var url = adapter.BuildTokenUrl("Documents");

        Assert.Equal(
            "https://lf-server.corp.local/LFRepositoryAPI/v2/Repositories/Documents/Token",
            url);
    }

    [Fact]
    public void RepositoriesUrl_V2_ContainsV2Segment()
    {
        var adapter = CreateAdapter("https://lf-server.corp.local", apiVersion: "v2");
        Assert.Equal(
            "https://lf-server.corp.local/LFRepositoryAPI/v2/Repositories",
            adapter.BuildRepositoriesUrl());
    }

    [Fact]
    public void DocumentPageUrls_V2_UseDocumentPagesRoute()
    {
        var adapter = CreateAdapter("https://lf-server.corp.local", apiVersion: "v2");

        Assert.Equal(
            "https://lf-server.corp.local/LFRepositoryAPI/v2/Repositories/Documents/Entries/42/Document/Pages",
            adapter.BuildEntryUrl("Documents", 42, EntryResource.Pages));
        Assert.Equal(
            "https://lf-server.corp.local/LFRepositoryAPI/v2/Repositories/Documents/Entries/42/Document/Edoc",
            adapter.BuildEntryUrl("Documents", 42, EntryResource.Edoc));
        Assert.Equal(
            "https://lf-server.corp.local/LFRepositoryAPI/v2/Repositories/Documents/Entries/42/Document/Pages/3/Image",
            adapter.BuildPageImageUrl("Documents", 42, 3));
        Assert.Equal(
            "https://lf-server.corp.local/LFRepositoryAPI/v2/Repositories/Documents/Entries/42/Document/Pages/3/Text",
            adapter.BuildPageTextUrl("Documents", 42, 3));
        Assert.Equal(
            "https://lf-server.corp.local/LFRepositoryAPI/v2/Repositories/Documents/Entries/42/Export?pageRange=3",
            adapter.BuildDocumentExportUrl("Documents", 42, "3"));
        Assert.Equal(
            "https://lf-server.corp.local/LFRepositoryAPI/v2/Repositories/Documents/Entries/42/Export",
            adapter.BuildDocumentExportUrl("Documents", 42));
    }

    [Fact]
    public void SearchUrls_V2_UseDocumentedLongOperationRoutes()
    {
        var adapter = CreateAdapter("https://lf-server.corp.local", apiVersion: "v2");

        Assert.EndsWith("/Searches/SearchAsync", adapter.BuildSearchUrl("Documents", SearchType.Advanced));
        Assert.EndsWith("/Tasks?taskIds=abc", adapter.BuildTaskStatusUrl("Documents", "abc"));
        Assert.EndsWith("/Searches/abc/Results", adapter.BuildSearchResultsUrl("Documents", "abc"));
    }

    [Fact]
    public void DocumentPageUrls_V1_PreserveLegacyRoute()
    {
        var adapter = CreateAdapter("https://lf-server.corp.local", apiVersion: "v1");

        Assert.Equal(
            "https://lf-server.corp.local/LFRepositoryAPI/v1/Repositories/Documents/Entries/42/pages",
            adapter.BuildEntryUrl("Documents", 42, EntryResource.Pages));
        Assert.Equal(
            "https://lf-server.corp.local/LFRepositoryAPI/v1/Repositories/Documents/Entries/42/Laserfiche.Repository.Document/edoc",
            adapter.BuildEntryUrl("Documents", 42, EntryResource.Edoc));
        Assert.Equal(
            "https://lf-server.corp.local/LFRepositoryAPI/v1/Repositories/Documents/Entries/42/pages/3/image",
            adapter.BuildPageImageUrl("Documents", 42, 3));
    }

    [Fact]
    public void AllUrlBuilders_V1_ContainV1NotV2()
    {
        var adapter = CreateAdapter("https://lf-server.corp.local", apiVersion: "v1");

        var urls = new[]
        {
            adapter.BuildTokenUrl("R"),
            adapter.BuildRepositoriesUrl(),
        };

        foreach (var url in urls)
        {
            Assert.Contains("/v1/", url);
            Assert.DoesNotContain("/v2/", url);
        }
    }

    // ── Repository ID URL encoding ────────────────────────────────────────────

    [Fact]
    public void TokenUrl_RepositoryWithSpace_IsPercentEncoded()
    {
        var adapter = CreateAdapter("https://lf-server.corp.local");
        var url = adapter.BuildTokenUrl("My Repository");

        Assert.Contains("/Repositories/My%20Repository/Token", url);
        Assert.DoesNotContain("/Repositories/My Repository/Token", url);
    }

    [Fact]
    public void TokenUrl_RepositoryWithAmpersand_IsPercentEncoded()
    {
        var adapter = CreateAdapter("https://lf-server.corp.local");
        var url = adapter.BuildTokenUrl("A&B");

        Assert.Contains("/Repositories/A%26B/Token", url);
    }

    [Fact]
    public void TokenUrl_RepositoryWithPlusSign_IsPercentEncoded()
    {
        var adapter = CreateAdapter("https://lf-server.corp.local");
        var url = adapter.BuildTokenUrl("Finance+HR");

        // + must be encoded as %2B (not left raw, which would be misread as a space).
        Assert.Contains("/Repositories/Finance%2BHR/Token", url);
    }

    [Fact]
    public void TokenUrl_StandardAlphanumericRepository_IsUnchanged()
    {
        var adapter = CreateAdapter("https://lf-server.corp.local");
        var url = adapter.BuildTokenUrl("LFNewRepoWF");

        // Plain alphanumeric names must not be altered.
        Assert.Contains("/Repositories/LFNewRepoWF/Token", url);
    }

    // ── BuildTokenUrlV2 — always uses /v2/, regardless of configured ApiVersion ──
    //
    // Requirement 15: BuildTokenUrlV2 must use the hard-coded /v2/ segment and
    // must ONLY be invoked by the SSO OAuth2 authorization-code exchange flow.
    // All normal V1 resource operations (entry listing, search, etc.) must use
    // BuildTokenUrl (which honours the configured/detected ApiVersion), never V2.

    [Fact]
    public void BuildTokenUrlV2_AlwaysContainsV2Segment_RegardlessOfConfiguredVersion()
    {
        // Even when the adapter is configured for v1, V2 SSO token exchange must
        // use /v2/ because the LFDS token endpoint is always V2.
        var adapter = CreateAdapter("https://lf-server.corp.local", apiVersion: "v1");
        var url     = adapter.BuildTokenUrlV2("Documents");

        // BuildTokenUrlV2 must always use /v2/ — it is the SSO token endpoint.
        Assert.Contains("/v2/", url);
        Assert.DoesNotContain("/v1/", url);
    }

    [Fact]
    public void BuildTokenUrlV2_DiffersFromBuildTokenUrl_WhenConfiguredAsV1()
    {
        // This guards the invariant that V2 SSO token exchange and V1 resource
        // operations use different URL paths — they must never be swapped.
        var adapter = CreateAdapter("https://lf-server.corp.local", apiVersion: "v1");

        var v1Url = adapter.BuildTokenUrl("Documents");
        var v2Url = adapter.BuildTokenUrlV2("Documents");

        // V2 SSO token URL must differ from the V1 resource token URL.
        Assert.NotEqual(v1Url, v2Url);
        Assert.Contains("/v1/", v1Url);
        Assert.Contains("/v2/", v2Url);
    }

    [Fact]
    public void BuildTokenUrlV2_ContainsRepositoryIdAndTokenSegment()
    {
        var adapter = CreateAdapter("https://lf-server.corp.local");
        var url     = adapter.BuildTokenUrlV2("Documents");

        Assert.Contains("/Repositories/Documents/Token", url);
    }

    [Fact]
    public void BuildTokenUrl_V1_DoesNotContainV2Segment()
    {
        // Verify that the regular V1 token URL never contains /v2/ — this guards
        // against accidentally routing V1 resource operations through the V2 path.
        var adapter = CreateAdapter("https://lf-server.corp.local", apiVersion: "v1");
        var url     = adapter.BuildTokenUrl("Documents");

        // V1 resource token URL must not contain /v2/ — V2 path is reserved for SSO BuildTokenUrlV2.
        Assert.DoesNotContain("/v2/", url);
    }

    // ── BuildFolderChildrenUrl — version-aware ────────────────────────────────
    //
    // Root cause confirmed from server Swagger:
    //   V1 path: /Entries/{id}/Laserfiche.Repository.Folder/children
    //   V2 path: /Entries/{id}/Folder/Children          (V1 path returns HTTP 404 on V2)
    //
    // Tasks 1 & 10 from the requirement spec.

    [Fact]
    public void FolderChildrenUrl_V2_UsesSimpleFolderChildrenPath()
    {
        var adapter = CreateAdapter("https://lf-server.corp.local", apiVersion: "v2");
        var url = adapter.BuildFolderChildrenUrl("TestEmployee", 1);

        // Must use the V2 simple path — NOT the V1 OData-typed cast path.
        Assert.Equal(
            "https://lf-server.corp.local/LFRepositoryAPI/v2/Repositories/TestEmployee/Entries/1/Folder/Children?groupByEntryType=false&formatFieldValues=false",
            url);
    }

    [Fact]
    public void FolderChildrenUrl_V2_DoesNotContainODataTypedCastPath()
    {
        var adapter = CreateAdapter("https://lf-server.corp.local", apiVersion: "v2");
        var url = adapter.BuildFolderChildrenUrl("LFNewRepoWF", 1);

        Assert.DoesNotContain("Laserfiche.Repository.Folder", url);
    }

    [Fact]
    public void FolderChildrenUrl_V1_UsesODataTypedCastPath()
    {
        var adapter = CreateAdapter("https://lf-server.corp.local", apiVersion: "v1");
        var url = adapter.BuildFolderChildrenUrl("Documents", 5);

        Assert.Equal(
            "https://lf-server.corp.local/LFRepositoryAPI/v1/Repositories/Documents/Entries/5/Laserfiche.Repository.Folder/children",
            url);
    }

    [Fact]
    public void FolderChildrenUrl_V1_DoesNotContainSimpleFolderPath()
    {
        var adapter = CreateAdapter("https://lf-server.corp.local", apiVersion: "v1");
        var url = adapter.BuildFolderChildrenUrl("Documents", 1);

        // V1 must NOT use the V2 simplified path.
        Assert.DoesNotContain("/Folder/Children", url);
    }

    [Fact]
    public void FolderChildrenUrl_V2_DynamicRepositoryId()
    {
        var adapter = CreateAdapter("https://lf-server.corp.local", apiVersion: "v2");

        var url1 = adapter.BuildFolderChildrenUrl("RepoA", 10);
        var url2 = adapter.BuildFolderChildrenUrl("RepoB", 20);

        Assert.Contains("/Repositories/RepoA/Entries/10/Folder/Children", url1);
        Assert.Contains("/Repositories/RepoB/Entries/20/Folder/Children", url2);
    }

    [Fact]
    public void FolderChildrenUrl_V2_IncludesRequiredQueryParameters()
    {
        var adapter = CreateAdapter("https://lf-server.corp.local", apiVersion: "v2");
        var url = adapter.BuildFolderChildrenUrl("TestEmployee", 1);

        Assert.Contains("groupByEntryType=false", url);
        Assert.Contains("formatFieldValues=false", url);
    }

    [Fact]
    public void FolderChildrenUrl_V2_ContainsV2Segment()
    {
        var adapter = CreateAdapter("https://lf-server.corp.local", apiVersion: "v2");
        var url = adapter.BuildFolderChildrenUrl("TestEmployee", 1);

        Assert.Contains("/v2/", url);
        Assert.DoesNotContain("/v1/", url);
    }

    [Fact]
    public void FolderChildrenUrl_V1_ContainsV1Segment()
    {
        var adapter = CreateAdapter("https://lf-server.corp.local", apiVersion: "v1");
        var url = adapter.BuildFolderChildrenUrl("Documents", 1);

        Assert.Contains("/v1/", url);
        Assert.DoesNotContain("/v2/", url);
    }

    [Fact]
    public void BuildEntryUrl_FolderChildren_V2_DelegatesToVersionAwareBuilder()
    {
        // BuildEntryUrl(FolderChildren) must produce the same URL as
        // BuildFolderChildrenUrl so neither code path can diverge.
        var adapter = CreateAdapter("https://lf-server.corp.local", apiVersion: "v2");

        var viaEnum   = adapter.BuildEntryUrl("TestEmployee", 42, EntryResource.FolderChildren);
        var viaDirect = adapter.BuildFolderChildrenUrl("TestEmployee", 42);

        Assert.Equal(viaDirect, viaEnum);
        Assert.Contains("/Folder/Children", viaEnum);
        Assert.DoesNotContain("Laserfiche.Repository.Folder", viaEnum);
    }

    [Fact]
    public void BuildEntryUrl_FolderChildren_V1_DelegatesToVersionAwareBuilder()
    {
        var adapter = CreateAdapter("https://lf-server.corp.local", apiVersion: "v1");

        var viaEnum   = adapter.BuildEntryUrl("Documents", 7, EntryResource.FolderChildren);
        var viaDirect = adapter.BuildFolderChildrenUrl("Documents", 7);

        Assert.Equal(viaDirect, viaEnum);
        Assert.Contains("Laserfiche.Repository.Folder/children", viaEnum);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static int CountOccurrencesIgnoreCase(string haystack, string needle)
    {
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    /// <summary>Minimal fixed-value <see cref="IOptionsMonitor{T}"/> for tests.</summary>
    private sealed class StaticOptionsMonitor : IOptionsMonitor<LaserficheOptions>
    {
        public StaticOptionsMonitor(LaserficheOptions value) => CurrentValue = value;
        public LaserficheOptions CurrentValue { get; }
        public LaserficheOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<LaserficheOptions, string?> listener) => null;
    }
}
