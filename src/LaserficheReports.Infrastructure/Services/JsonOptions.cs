using System.Text.Json;
using System.Text.Json.Serialization;

namespace LaserficheReports.Infrastructure.Services;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> for deserialising Laserfiche API responses.
/// The Laserfiche Repository API v2 returns camelCase JSON with ISO-8601 date strings.
/// </summary>
internal static class JsonOptions
{
    /// <summary>
    /// Singleton options instance configured for Laserfiche API JSON payloads.
    /// Use this for all <see cref="JsonSerializer"/> calls within the Infrastructure layer
    /// to ensure consistent deserialisation behaviour.
    /// </summary>
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        Converters                  = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
