using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using GemakTV.Application.Ports;

namespace GemakTV.Infrastructure.GitHub;

/// <summary>Where GemakTV's published files live: a branch of a GitHub repository.</summary>
public sealed record GitHubLocation(string Owner, string Repository, string Branch)
{
    public const string ConfigPath = "config.gtv";

    public Uri ApiBase { get; init; } = new("https://api.github.com/");

    public Uri RawBase { get; init; } = new("https://raw.githubusercontent.com/");

    /// <summary>Reads "owner/repo@branch" from the GemakRepo assembly metadata the build stamps into each app.</summary>
    public static GitHubLocation FromAssembly(Assembly assembly)
    {
        var value = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "GemakRepo")?.Value
            ?? throw new InvalidOperationException("The build did not stamp the GemakRepo metadata.");
        return Parse(value);
    }

    public static GitHubLocation Parse(string value)
    {
        var at = value.IndexOf('@');
        var slash = value.IndexOf('/');
        if (slash <= 0 || at <= slash + 1 || at == value.Length - 1)
            throw new FormatException($"Expected owner/repo@branch, got '{value}'.");
        return new GitHubLocation(value[..slash], value[(slash + 1)..at], value[(at + 1)..]);
    }

    public override string ToString() => $"{Owner}/{Repository}@{Branch}";
}

public sealed record GitHubFile(byte[] Content, string Sha);

public sealed record GitHubUser(string Login, string? Name);

public sealed record RepositoryAccess(bool CanPush, bool IsAdmin, bool IsPrivate);

/// <summary>A file to write (Content set) or delete (Content null) in a multi-file commit.</summary>
public sealed record FileChange(string Path, byte[]? Content);

public class GitHubApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

public sealed class GitHubAuthException(HttpStatusCode status, string message) : GitHubApiException(status, message);

public sealed class GitHubRateLimitException(DateTimeOffset? resetsAt)
    : GitHubApiException(HttpStatusCode.Forbidden, "GitHub's request limit was reached" + (resetsAt is { } r ? $"; it resets at {r.ToLocalTime():HH:mm}." : "."))
{
    public DateTimeOffset? ResetsAt { get; } = resetsAt;
}

/// <summary>
/// The small slice of the GitHub REST API GemakTV needs: read files, write a file with optimistic concurrency,
/// make an atomic multi-file commit, and check who is signed in. Anonymous use (no token) can only read.
/// </summary>
public sealed class GitHubClient(HttpClient http, GitHubLocation location, string? token = null)
{
    private const string ApiVersion = "2022-11-28";

    public GitHubLocation Location => location;

    public bool IsAuthenticated => !string.IsNullOrEmpty(token);

    /// <summary>Reads a file of up to 1 MB with its blob SHA. Null when the file or the branch does not exist.</summary>
    public async Task<GitHubFile?> GetFileAsync(string path, CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Get, $"{ContentsUrl(path)}?ref={Uri.EscapeDataString(location.Branch)}");
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.GetProperty("type").GetString() != "file")
            throw new GitHubApiException(response.StatusCode, $"'{path}' is not a file.");
        var sha = root.GetProperty("sha").GetString()!;
        var encoding = root.TryGetProperty("encoding", out var e) ? e.GetString() : null;
        if (encoding != "base64")
            throw new GitHubApiException(response.StatusCode, $"'{path}' is too large for the contents API.");
        var base64 = root.GetProperty("content").GetString() ?? "";
        return new GitHubFile(Convert.FromBase64String(base64.Replace("\n", "", StringComparison.Ordinal)), sha);
    }

    /// <summary>Reads a file through GitHub's CDN (no API rate limit, but may lag a few minutes behind).</summary>
    public async Task<byte[]?> GetFileFromCdnAsync(string path, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, RawUrl(path));
        request.Headers.UserAgent.ParseAdd("GemakTV/1.0");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Streams a file of any size (up to GitHub's 100 MB limit) into <paramref name="destination"/>.</summary>
    public async Task DownloadAsync(string path, Stream destination, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage? response = null;
        try
        {
            using (var request = Request(HttpMethod.Get, $"{ContentsUrl(path)}?ref={Uri.EscapeDataString(location.Branch)}", accept: "application/vnd.github.raw+json"))
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
            {
                // Rate-limited or GitHub API trouble: the CDN serves the same bytes.
                response.Dispose();
                using var cdn = new HttpRequestMessage(HttpMethod.Get, RawUrl(path));
                cdn.Headers.UserAgent.ParseAdd("GemakTV/1.0");
                response = await http.SendAsync(cdn, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new GitHubApiException(HttpStatusCode.NotFound, $"'{path}' was not found on {location}.");
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await body.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            response?.Dispose();
        }
    }

    /// <summary>
    /// Creates or replaces one file with a single commit. Pass the SHA from <see cref="GetFileAsync"/> to replace;
    /// if the file changed since, GitHub refuses and <see cref="ConcurrencyConflictException"/> is thrown.
    /// </summary>
    /// <returns>The new blob SHA of the file.</returns>
    public async Task<string> PutFileAsync(string path, byte[] content, string? expectedSha, string message, CancellationToken cancellationToken = default)
    {
        var body = Json(w =>
        {
            w.WriteString("message", message);
            w.WriteString("content", Convert.ToBase64String(content));
            w.WriteString("branch", location.Branch);
            if (expectedSha is not null) w.WriteString("sha", expectedSha);
        });
        using var request = Request(HttpMethod.Put, ContentsUrl(path), body: body);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
        {
            var detail = await ErrorMessageAsync(response, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Conflict || detail.Contains("sha", StringComparison.OrdinalIgnoreCase))
                throw new ConcurrencyConflictException("The channel list was changed by someone else since it was loaded.");
            throw new GitHubApiException(response.StatusCode, detail);
        }
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return json.RootElement.GetProperty("content").GetProperty("sha").GetString()!;
    }

    /// <summary>The commit SHA at the tip of the publish branch, or null when the branch does not exist yet.</summary>
    public async Task<string?> GetBranchHeadAsync(CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Get, RepoUrl($"git/ref/heads/{location.Branch}"));
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return json.RootElement.GetProperty("object").GetProperty("sha").GetString();
    }

    /// <summary>
    /// Writes and deletes several files in one commit on the publish branch, so readers never see a config that
    /// points at an APK that is not there yet. Creates the branch (with no history) when it does not exist.
    /// Fails with <see cref="ConcurrencyConflictException"/> if the branch moved after <paramref name="expectedHead"/>.
    /// </summary>
    /// <returns>The new commit SHA.</returns>
    public async Task<string> CommitAsync(IReadOnlyList<FileChange> changes, string message, string? expectedHead, CancellationToken cancellationToken = default)
    {
        string? baseTree = null;
        if (expectedHead is not null)
        {
            using var commitJson = await GetJsonAsync(RepoUrl($"git/commits/{expectedHead}"), cancellationToken).ConfigureAwait(false);
            baseTree = commitJson.RootElement.GetProperty("tree").GetProperty("sha").GetString();
        }

        var entries = new List<(string Path, string? Sha)>();
        foreach (var change in changes)
        {
            if (change.Content is null)
            {
                entries.Add((change.Path, null));
                continue;
            }
            var blobBody = Json(w =>
            {
                w.WriteString("content", Convert.ToBase64String(change.Content));
                w.WriteString("encoding", "base64");
            });
            using var blob = await PostJsonAsync(RepoUrl("git/blobs"), blobBody, cancellationToken).ConfigureAwait(false);
            entries.Add((change.Path, blob.RootElement.GetProperty("sha").GetString()));
        }

        var treeBody = Json(w =>
        {
            if (baseTree is not null) w.WriteString("base_tree", baseTree);
            w.WriteStartArray("tree");
            foreach (var (path, sha) in entries)
            {
                w.WriteStartObject();
                w.WriteString("path", path);
                w.WriteString("mode", "100644");
                w.WriteString("type", "blob");
                if (sha is null) w.WriteNull("sha"); else w.WriteString("sha", sha);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
        using var tree = await PostJsonAsync(RepoUrl("git/trees"), treeBody, cancellationToken).ConfigureAwait(false);
        var treeSha = tree.RootElement.GetProperty("sha").GetString()!;

        var commitBody = Json(w =>
        {
            w.WriteString("message", message);
            w.WriteString("tree", treeSha);
            w.WriteStartArray("parents");
            if (expectedHead is not null) w.WriteStringValue(expectedHead);
            w.WriteEndArray();
        });
        using var commit = await PostJsonAsync(RepoUrl("git/commits"), commitBody, cancellationToken).ConfigureAwait(false);
        var commitSha = commit.RootElement.GetProperty("sha").GetString()!;

        if (expectedHead is null)
        {
            var refBody = Json(w =>
            {
                w.WriteString("ref", $"refs/heads/{location.Branch}");
                w.WriteString("sha", commitSha);
            });
            using var request = Request(HttpMethod.Post, RepoUrl("git/refs"), body: refBody);
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
                throw new ConcurrencyConflictException($"Branch '{location.Branch}' was created by someone else meanwhile.");
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var refBody = Json(w =>
            {
                w.WriteString("sha", commitSha);
                w.WriteBoolean("force", false);
            });
            using var request = Request(HttpMethod.Patch, RepoUrl($"git/refs/heads/{location.Branch}"), body: refBody);
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
                throw new ConcurrencyConflictException($"Branch '{location.Branch}' moved while publishing; nothing was changed.");
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        }
        return commitSha;
    }

    /// <summary>Lists file names in a folder of the publish branch (empty when it does not exist).</summary>
    public async Task<IReadOnlyList<string>> ListFolderAsync(string folder, CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Get, $"{ContentsUrl(folder)}?ref={Uri.EscapeDataString(location.Branch)}");
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return [];
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        if (json.RootElement.ValueKind != JsonValueKind.Array) return [];
        return json.RootElement.EnumerateArray().Select(e => e.GetProperty("path").GetString()!).ToList();
    }

    public async Task<GitHubUser> GetUserAsync(CancellationToken cancellationToken = default)
    {
        using var json = await GetJsonAsync(new Uri(location.ApiBase, "user"), cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        return new GitHubUser(root.GetProperty("login").GetString()!, root.TryGetProperty("name", out var n) ? n.GetString() : null);
    }

    public async Task<RepositoryAccess> GetRepositoryAccessAsync(CancellationToken cancellationToken = default)
    {
        using var json = await GetJsonAsync(RepoUrl(""), cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        var isPrivate = root.TryGetProperty("private", out var p) && p.GetBoolean();
        if (!root.TryGetProperty("permissions", out var permissions))
            return new RepositoryAccess(false, false, isPrivate);
        return new RepositoryAccess(
            permissions.TryGetProperty("push", out var push) && push.GetBoolean(),
            permissions.TryGetProperty("admin", out var admin) && admin.GetBoolean(),
            isPrivate);
    }

    private Uri RepoUrl(string relative) =>
        new(location.ApiBase, $"repos/{location.Owner}/{location.Repository}" + (relative.Length > 0 ? "/" + relative : ""));

    private string ContentsUrl(string path) =>
        RepoUrl("contents/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString))).ToString();

    private Uri RawUrl(string path) =>
        new(location.RawBase, $"{location.Owner}/{location.Repository}/{location.Branch}/{path}");

    private HttpRequestMessage Request(HttpMethod method, Uri url, string accept = "application/vnd.github+json", string? body = null) =>
        Request(method, url.ToString(), accept, body);

    private HttpRequestMessage Request(HttpMethod method, string url, string accept = "application/vnd.github+json", string? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.ParseAdd(accept);
        request.Headers.UserAgent.ParseAdd("GemakTV/1.0");
        request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }

    private async Task<JsonDocument> GetJsonAsync(Uri url, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, url);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument> PostJsonAsync(Uri url, string body, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Post, url, body: body);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
            && response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault() == "0")
        {
            DateTimeOffset? reset = response.Headers.TryGetValues("x-ratelimit-reset", out var r) && long.TryParse(r.FirstOrDefault(), out var epoch)
                ? DateTimeOffset.FromUnixTimeSeconds(epoch)
                : null;
            throw new GitHubRateLimitException(reset);
        }

        var message = await ErrorMessageAsync(response, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new GitHubAuthException(response.StatusCode, "GitHub did not accept the access token. " + message);
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new GitHubAuthException(response.StatusCode, "This GitHub account may not do that. " + message);
        throw new GitHubApiException(response.StatusCode, $"GitHub answered {(int)response.StatusCode}: {message}");
    }

    private static async Task<string> ErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var json = JsonDocument.Parse(text);
            return json.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? text : text;
        }
        catch (JsonException)
        {
            return response.ReasonPhrase ?? response.StatusCode.ToString();
        }
    }

    private static string Json(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
