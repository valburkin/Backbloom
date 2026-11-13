using Microsoft.Extensions.Configuration;

namespace Backbloom.App.Updates;

/// <summary>
/// Where an installed Backbloom looks for updates: <c>--update-url</c> / <c>BACKBLOOM_UPDATE_URL</c> (default: the
/// GitHub releases of <see cref="DefaultFeedUrl"/>; or a Velopack release feed: the folder with
/// <c>releases.&lt;channel&gt;.json</c>; <c>none</c> turns updates off), and <c>--update-channel</c> /
/// <c>BACKBLOOM_UPDATE_CHANNEL</c> (default: the channel the installed package was built for).
/// </summary>
/// <param name="FeedUrl">Release feed; null when updates are off.</param>
/// <param name="Channel">Release channel to follow; null: the installed package's channel.</param>
public sealed record UpdateSettings(Uri? FeedUrl, string? Channel)
{
    /// <summary>The repository whose GitHub releases carry the installer and the release feed files.</summary>
    public const string DefaultFeedUrl = "https://github.com/valburkin/Backbloom";

    /// <summary>The <c>--update-url</c> value that turns automatic updates off.</summary>
    public const string Off = "none";

    /// <summary>The channel <c>packaging/build.ps1</c> packs for unless told otherwise.</summary>
    public const string DefaultChannel = "stable";

    /// <summary>Updates are checked for (a feed is configured).</summary>
    public bool Enabled => FeedUrl is not null;

    /// <summary>The feed is a GitHub repository (<c>https://github.com/owner/name</c>): its releases are read through the GitHub API.</summary>
    public bool IsGitHubRepository =>
        FeedUrl is { Scheme: "https" } feed && feed.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && feed.AbsolutePath.Trim('/').Split('/') is [{ Length: > 0 }, { Length: > 0 }] && string.IsNullOrEmpty(feed.Query);

    public static UpdateSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var url = Read(configuration, "update-url", "UPDATE_URL") ?? DefaultFeedUrl;
        var feed = !url.Equals(Off, StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" or "file" ? uri : null;
        return new UpdateSettings(feed, Read(configuration, "update-channel", "UPDATE_CHANNEL"));
    }

    private static string? Read(IConfiguration configuration, string option, string environment) =>
        configuration[option] is { Length: > 0 } value ? value : configuration[environment] is { Length: > 0 } env ? env : null;
}
