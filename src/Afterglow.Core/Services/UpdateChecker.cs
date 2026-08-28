using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Afterglow.Core.Services;

public sealed record UpdateCheckResult(string LatestTag, bool UpdateAvailable);

/// <summary>
/// Opt-in update check: one anonymous GET against the GitHub Releases API,
/// comparing the latest tag to the running version. Off by default; nothing
/// about the machine is sent (GitHub sees the same request a browser would).
/// </summary>
public static class UpdateChecker
{
    public const string ReleasesApiUrl = "https://api.github.com/repos/minewefu/afterglow/releases/latest";
    public const string ReleasesPageUrl = "https://github.com/minewefu/afterglow/releases/latest";

    private static readonly Lazy<HttpClient> Client = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Afterglow-update-check", CurrentVersion?.ToString(3) ?? "dev"));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    });

    public static Version? CurrentVersion => typeof(UpdateChecker).Assembly.GetName().Version;

    /// <summary>"v1.0.3" / "1.0.3" → Version(1,0,3); null when unparseable.</summary>
    public static Version? TryParseTag(string tag)
    {
        string trimmed = tag.Trim().TrimStart('v', 'V');
        return System.Version.TryParse(trimmed, out var version) && version.Build >= 0
            ? new Version(version.Major, version.Minor, version.Build)
            : System.Version.TryParse(trimmed + ".0", out var twoPart)
                ? new Version(twoPart.Major, twoPart.Minor, Math.Max(twoPart.Build, 0))
                : null;
    }

    /// <summary>Pure comparison used by the fetch path and by tests.</summary>
    public static bool IsNewer(string latestTag, Version current) =>
        TryParseTag(latestTag) is { } latest &&
        latest > new Version(current.Major, current.Minor, Math.Max(current.Build, 0));

    /// <summary>Null means the check could not run (offline, rate-limited, bad JSON).</summary>
    public static async Task<UpdateCheckResult?> CheckAsync(
        Version current, CancellationToken cancellationToken = default)
    {
        try
        {
            string json = await Client.Value.GetStringAsync(ReleasesApiUrl, cancellationToken)
                .ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tag_name", out var tagElement) ||
                tagElement.GetString() is not { Length: > 0 } tag)
            {
                return null;
            }

            return new UpdateCheckResult(tag, IsNewer(tag, current));
        }
        catch (Exception ex) when (
            ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
