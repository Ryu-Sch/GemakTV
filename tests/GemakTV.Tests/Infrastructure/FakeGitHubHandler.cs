using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GemakTV.Tests.Infrastructure;

/// <summary>
/// An in-memory stand-in for the parts of the GitHub REST API that GemakTV uses, faithful to the behaviour
/// that matters: blob SHAs, refusing stale SHAs, and non-fast-forward ref updates.
/// </summary>
internal sealed class FakeGitHubHandler : HttpMessageHandler
{
    public Dictionary<string, byte[]> Files { get; } = new();

    public bool BranchExists { get; set; } = true;

    public string Head { get; set; } = "commit-0";

    public string? RequiredToken { get; set; }

    public bool RateLimited { get; set; }

    public List<string> Log { get; } = [];

    public Dictionary<string, byte[]> CdnFiles { get; } = new();

    public static string BlobSha(byte[] content) =>
        Convert.ToHexStringLower(SHA1.HashData([.. Encoding.ASCII.GetBytes($"blob {content.Length}\0"), .. content]));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!;
        var path = url.AbsolutePath;
        Log.Add($"{request.Method} {url.Host}{path}");

        if (url.Host == "raw.githubusercontent.com")
        {
            var file = string.Join('/', path.Split('/').Skip(4));
            return CdnFiles.TryGetValue(file, out var cdn) ? Bytes(cdn) : Status(HttpStatusCode.NotFound);
        }

        if (RateLimited)
        {
            var limited = Json(HttpStatusCode.Forbidden, new JsonObject { ["message"] = "API rate limit exceeded" });
            limited.Headers.Add("x-ratelimit-remaining", "0");
            limited.Headers.Add("x-ratelimit-reset", "1900000000");
            return limited;
        }

        if (RequiredToken is not null && request.Headers.Authorization?.Parameter != RequiredToken)
            return Json(HttpStatusCode.Unauthorized, new JsonObject { ["message"] = "Bad credentials" });

        if (path == "/user")
            return Json(HttpStatusCode.OK, new JsonObject { ["login"] = "gemaktv-bot", ["name"] = "GemakTV Bot" });

        const string repo = "/repos/Ryu-Sch/GemakTV";
        if (path == repo)
            return Json(HttpStatusCode.OK, new JsonObject { ["private"] = false, ["permissions"] = new JsonObject { ["push"] = true, ["admin"] = false } });

        if (path.StartsWith(repo + "/contents/", StringComparison.Ordinal))
        {
            var file = Uri.UnescapeDataString(path[(repo.Length + "/contents/".Length)..]);
            if (request.Method == HttpMethod.Get)
            {
                if (!BranchExists || !Files.TryGetValue(file, out var content))
                    return Json(HttpStatusCode.NotFound, new JsonObject { ["message"] = "Not Found" });
                if (request.Headers.Accept.ToString().Contains("raw"))
                    return Bytes(content);
                return Json(HttpStatusCode.OK, new JsonObject
                {
                    ["type"] = "file",
                    ["encoding"] = "base64",
                    ["sha"] = BlobSha(content),
                    // GitHub wraps base64 at 60 characters.
                    ["content"] = string.Join('\n', Convert.ToBase64String(content).Chunk(60).Select(c => new string(c))),
                });
            }
            if (request.Method == HttpMethod.Put)
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
                var sha = body["sha"]?.GetValue<string>();
                if (Files.TryGetValue(file, out var existing))
                {
                    if (sha is null) return Json(HttpStatusCode.UnprocessableEntity, new JsonObject { ["message"] = "Invalid request.\n\n\"sha\" wasn't supplied." });
                    if (sha != BlobSha(existing)) return Json(HttpStatusCode.Conflict, new JsonObject { ["message"] = $"{file} does not match {sha}" });
                }
                var bytes = Convert.FromBase64String(body["content"]!.GetValue<string>());
                Files[file] = bytes;
                Head = "commit-" + Log.Count;
                return Json(HttpStatusCode.OK, new JsonObject { ["content"] = new JsonObject { ["sha"] = BlobSha(bytes) } });
            }
        }

        if (path == $"{repo}/git/ref/heads/published")
            return BranchExists ? Json(HttpStatusCode.OK, new JsonObject { ["object"] = new JsonObject { ["sha"] = Head } }) : Status(HttpStatusCode.NotFound);

        if (path.StartsWith($"{repo}/git/commits/", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
            return Json(HttpStatusCode.OK, new JsonObject { ["tree"] = new JsonObject { ["sha"] = "tree-of-" + path.Split('/')[^1] } });

        if (path == $"{repo}/git/blobs")
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            var bytes = Convert.FromBase64String(body["content"]!.GetValue<string>());
            var sha = BlobSha(bytes);
            _blobs[sha] = bytes;
            return Json(HttpStatusCode.Created, new JsonObject { ["sha"] = sha });
        }

        if (path == $"{repo}/git/trees")
        {
            _pendingTree = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            return Json(HttpStatusCode.Created, new JsonObject { ["sha"] = "tree-new" });
        }

        if (path == $"{repo}/git/commits" && request.Method == HttpMethod.Post)
        {
            _pendingParents = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!["parents"]!.AsArray().Select(p => p!.GetValue<string>()).ToList();
            return Json(HttpStatusCode.Created, new JsonObject { ["sha"] = "commit-new" });
        }

        if (path == $"{repo}/git/refs" && request.Method == HttpMethod.Post)
        {
            if (BranchExists) return Json(HttpStatusCode.UnprocessableEntity, new JsonObject { ["message"] = "Reference already exists" });
            BranchExists = true;
            Files.Clear();
            ApplyPendingTree("commit-new");
            return Json(HttpStatusCode.Created, new JsonObject());
        }

        if (path == $"{repo}/git/refs/heads/published" && request.Method == HttpMethod.Patch)
        {
            if (_pendingParents.FirstOrDefault() != Head)
                return Json(HttpStatusCode.UnprocessableEntity, new JsonObject { ["message"] = "Update is not a fast forward" });
            ApplyPendingTree("commit-new");
            return Json(HttpStatusCode.OK, new JsonObject());
        }

        return Status(HttpStatusCode.NotFound);
    }

    private readonly Dictionary<string, byte[]> _blobs = new();
    private JsonNode? _pendingTree;
    private List<string> _pendingParents = [];

    private void ApplyPendingTree(string commit)
    {
        foreach (var entry in _pendingTree!["tree"]!.AsArray())
        {
            var file = entry!["path"]!.GetValue<string>();
            var sha = entry["sha"]?.GetValue<string>();
            if (sha is null) Files.Remove(file); else Files[file] = _blobs[sha];
        }
        Head = commit;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(byte[] content) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };

    private static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new StringContent("{\"message\":\"Not Found\"}") };
}
