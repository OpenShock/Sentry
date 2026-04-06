using System.Text.Json;

namespace OpenShock.Sentry.Detection.Backends;

internal static class DetectorJsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}
