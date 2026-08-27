namespace Lampa.Desktop.Services;

/// <summary>Same hosts and site API as the Android Lampa client.</summary>
public static class AppEndpoints
{
    public const string GitHubLatestReleaseApi = "https://api.github.com/repos/VerentiX/Lampa-Desktop/releases/latest";
    public const string SiteUrl = "https://hattabych.ru";
    public const string AppLatestApi = SiteUrl + "/api/app/latest";
    public const string SubscriptionPrimaryHost = "gw.zizmos.ru";
    public const string SubscriptionFallbackHost = "sub.subhotig.buzz";
    public const string SubscriptionReserveHost = "v.hattabych.ru";

    public static readonly string[] AppLatestCheckUrls =
    [
        GitHubLatestReleaseApi,
        AppLatestApi + "?platform=windows",
        AppLatestApi,
    ];
}
