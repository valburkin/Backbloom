using Microsoft.Extensions.Configuration;

namespace Overglow.App.Updates;

/// <summary>
/// Where an installed Overglow looks for updates: <c>--update-url</c> / <c>OVERGLOW_UPDATE_URL</c> (a Velopack
/// release feed: the folder with <c>releases.&lt;channel&gt;.json</c>), and <c>--update-channel</c> /
/// <c>OVERGLOW_UPDATE_CHANNEL</c> (default: the channel the installed package was built for).
/// </summary>
/// <param name="FeedUrl">Release feed; null when updates are off.</param>
/// <param name="Channel">Release channel to follow; null: the installed package's channel.</param>
public sealed record UpdateSettings(Uri? FeedUrl, string? Channel)
{
    /// <summary>
    /// The release feed builds use until the update server exists. Its <c>.invalid</c> host never resolves, and
    /// <see cref="FromConfiguration"/> turns updates off for it, so no request goes out.
    /// </summary>
    public const string PlaceholderFeedUrl = "https://updates.overglow.invalid/windows";

    /// <summary>The channel <c>packaging/build.ps1</c> packs for unless told otherwise.</summary>
    public const string DefaultChannel = "stable";

    /// <summary>Updates are checked for (a real feed is configured).</summary>
    public bool Enabled => FeedUrl is not null;

    public static UpdateSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var url = Read(configuration, "update-url", "UPDATE_URL") ?? PlaceholderFeedUrl;
        var feed = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" or "file" && !IsPlaceholder(uri) ? uri : null;
        return new UpdateSettings(feed, Read(configuration, "update-channel", "UPDATE_CHANNEL"));
    }

    private static bool IsPlaceholder(Uri uri) => uri.Host.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase);

    private static string? Read(IConfiguration configuration, string option, string environment) =>
        configuration[option] is { Length: > 0 } value ? value : configuration[environment] is { Length: > 0 } env ? env : null;
}
