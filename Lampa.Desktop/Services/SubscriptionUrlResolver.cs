using System.Text.RegularExpressions;

namespace Lampa.Desktop.Services;

/// <summary>
/// Rewrites managed /auto/{id} subscription URLs across the same fallback hosts as Android.
/// </summary>
public static class SubscriptionUrlResolver
{
    private static readonly Regex SubIdPath = new(@"/auto/([A-Za-z0-9_-]{6,128})/?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string? ExtractSubId(string? url)
    {
        var normalized = url?.Trim() ?? "";
        if (normalized.Length == 0) return null;
        var match = SubIdPath.Match(normalized);
        return match.Success ? match.Groups[1].Value : null;
    }

    public static bool IsManaged(string? url) => ExtractSubId(url) is not null;

    public static IReadOnlyList<string> CandidateUrls(string originalUrl)
    {
        var trimmed = originalUrl.Trim();
        var subId = ExtractSubId(trimmed);
        if (subId is null) return [trimmed];
        return new[]
        {
            AppEndpoints.SubscriptionPrimaryHost,
            AppEndpoints.SubscriptionFallbackHost,
            AppEndpoints.SubscriptionReserveHost,
        }.Select(host => BuildUrl(host, subId)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string NormalizeStoredUrl(string url)
    {
        var subId = ExtractSubId(url);
        return subId is null ? url.Trim() : BuildUrl(AppEndpoints.SubscriptionPrimaryHost, subId);
    }

    private static string BuildUrl(string host, string subId) => $"https://{host}/auto/{subId}";
}
