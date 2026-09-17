using System.Text.Json;

namespace LaserficheReports.Infrastructure.Services;

/// <summary>
/// Parses the JSON body returned by <c>GET /Repositories</c> across Laserfiche API versions.
/// </summary>
/// <remarks>
/// <para>
/// <b>V1</b> returns a plain JSON array: <c>[{ "repoId": "...", "repoName": "..." }, ...]</c>
/// </para>
/// <para>
/// <b>V2</b> returns an OData envelope:
/// <c>{ "value": [{ "repoId": "...", "repoName": "..." }, ...] }</c>
/// </para>
/// <para>
/// Both shapes are handled transparently so the rest of the codebase can remain
/// version-agnostic.  This class is also used by
/// <see cref="ApiVersionDetectionService"/> to validate that a candidate version
/// actually delivers a usable repository list — not merely that the route returns HTTP 200.
/// </para>
/// </remarks>
internal static class RepositoryJsonParser
{
    /// <summary>V1 plain JSON array <c>[{...}]</c>.</summary>
    internal const string ShapeV1Array = "v1-array";
    /// <summary>V2 OData envelope <c>{"value":[...]}</c>.</summary>
    internal const string ShapeV2OData = "v2-odata-value";
    /// <summary>Body could not be interpreted as a repository list.</summary>
    internal const string ShapeUnknown = "unknown";

    /// <summary>
    /// Attempts to parse <paramref name="body"/> as a list of <see cref="RepositoryDto"/>s.
    /// Recognises the V1 plain-array and V2 OData <c>{"value":[...]}</c> shapes.
    /// Returns <c>null</c> — never throws — for any unrecognised or malformed input.
    /// </summary>
    /// <param name="body">Raw JSON response body.</param>
    /// <param name="shape">
    /// Set to a short label describing the detected shape (e.g. <c>"v1-array"</c>,
    /// <c>"v2-odata-value"</c>, <c>"unknown(Object)"</c>).  Use this in log messages.
    /// </param>
    /// <returns>
    /// A (possibly empty) list on success; <c>null</c> when the shape is not recognised
    /// or the body is invalid JSON.
    /// </returns>
    public static List<RepositoryDto>? TryParse(string body, out string shape)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            shape = "empty";
            return null;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            shape = "invalid-json";
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;

            // ── V1: plain JSON array  →  [{...}, ...]  ────────────────────────
            if (root.ValueKind == JsonValueKind.Array)
            {
                shape = ShapeV1Array;
                try
                {
                    return JsonSerializer.Deserialize<List<RepositoryDto>>(body, JsonOptions.Default);
                }
                catch (JsonException)
                {
                    shape = "v1-array-deserialize-error";
                    return null;
                }
            }

            // ── V2 OData: {"value": [{...}, ...], "@odata.context": "..."}  ───
            //
            // V2 uses camelCase property names that differ from V1:
            //   V1: "repoId" / "repoName" / "webclientUrl"
            //   V2: "id"     / "name"     / "webClientUrl"
            //
            // We manually iterate and probe each element so both naming schemes are
            // supported without any dependency on JsonSerializer attribute conventions.
            if (root.ValueKind    == JsonValueKind.Object &&
                root.TryGetProperty("value", out var valueEl) &&
                valueEl.ValueKind == JsonValueKind.Array)
            {
                shape = ShapeV2OData;
                var result = new List<RepositoryDto>();
                foreach (var el in valueEl.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object) continue;

                    var dto = new RepositoryDto();

                    // Repository ID: V2 = "id", V1 fallback = "repoId"
                    if (el.TryGetProperty("id",     out var idEl))     dto.RepoId       = idEl.GetString()     ?? string.Empty;
                    else if (el.TryGetProperty("repoId", out var rid)) dto.RepoId       = rid.GetString()      ?? string.Empty;

                    // Repository name: V2 = "name", V1 fallback = "repoName"
                    if (el.TryGetProperty("name",       out var nmEl))  dto.RepoName     = nmEl.GetString()     ?? string.Empty;
                    else if (el.TryGetProperty("repoName", out var rn)) dto.RepoName     = rn.GetString()       ?? string.Empty;

                    // Web-client URL: V2 = "webClientUrl" (capital C/U), V1 = "webclientUrl"
                    if (el.TryGetProperty("webClientUrl",  out var wc1)) dto.WebclientUrl = wc1.GetString()     ?? string.Empty;
                    else if (el.TryGetProperty("webclientUrl", out var wc2)) dto.WebclientUrl = wc2.GetString() ?? string.Empty;

                    result.Add(dto);
                }
                return result;
            }

            // ── Unrecognised shape (error object, HTML string, metadata, etc.) ─
            shape = $"{ShapeUnknown}({root.ValueKind})";
            return null;
        }
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="body"/> can be interpreted as a repository
    /// list (V1 plain-array or V2 OData envelope), even if the list is empty.
    /// </summary>
    /// <remarks>
    /// Used by <see cref="ApiVersionDetectionService"/> to reject a candidate API version
    /// that returns HTTP 200 with an incompatible JSON shape (error objects, HTML, metadata
    /// envelopes, etc.) rather than a usable repository list.
    /// </remarks>
    public static bool IsCompatibleShape(string body)
        => TryParse(body, out _) is not null;
}
