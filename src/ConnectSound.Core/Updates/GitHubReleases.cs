using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConnectSound.Core.Updates;

public sealed record AvailableUpdate(Version Version, string? Notes, string? DownloadUrl, object? PlatformState = null);

public interface IUpdater
{
    /// <summary>Null when the app is up to date or updates aren't supported in this build (e.g. a dev run).</summary>
    Task<AvailableUpdate?> CheckAsync(CancellationToken ct);

    /// <summary>Downloads and installs; on Windows the app restarts, on Android the system installer opens.</summary>
    Task ApplyAsync(AvailableUpdate update, IProgress<double>? progress, CancellationToken ct);
}

public sealed class NoUpdater : IUpdater
{
    public Task<AvailableUpdate?> CheckAsync(CancellationToken ct) => Task.FromResult<AvailableUpdate?>(null);
    public Task ApplyAsync(AvailableUpdate update, IProgress<double>? progress, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Reads the latest release of the repository straight from the GitHub API.</summary>
public static class GitHubReleases
{
    public const string Owner = "hpixsel";
    public const string Repository = "ConnectSound";
    public const string RepositoryUrl = $"https://github.com/{Owner}/{Repository}";

    public static async Task<GitHubRelease?> GetLatestAsync(HttpClient http, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Owner}/{Repository}/releases/latest");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ConnectSound", "1"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(body, GitHubJson.Default.GitHubRelease, ct).ConfigureAwait(false);
    }

    /// <summary>Parses tags like "v1.4.0" or "1.4".</summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var text = tag.TrimStart('v', 'V');
        var dash = text.IndexOfAny(['-', '+']);
        if (dash >= 0) text = text[..dash];
        return Version.TryParse(text, out var v) ? Normalize(v) : null;
    }

    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}

public sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("prerelease")] bool Prerelease,
    [property: JsonPropertyName("assets")] IReadOnlyList<GitHubAsset> Assets);

public sealed record GitHubAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string DownloadUrl,
    [property: JsonPropertyName("size")] long Size);

[JsonSerializable(typeof(GitHubRelease))]
internal sealed partial class GitHubJson : JsonSerializerContext;

/// <summary>The update can't proceed until the user does something; the message tells them what.</summary>
public sealed class UpdateBlockedException(string message) : Exception(message);
