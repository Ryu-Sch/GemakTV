// Local stand-in for the GitHub REST API endpoints GemakTV uses (user, repository, contents GET/PUT, raw files).
// Usage:
//   dotnet tools/GemakTV.FakeGitHub/bin/Debug/net10.0/GemakTV.FakeGitHub.dll [--port 5080] [--token test-token]
//          [--user gemaktv-bot] [--data <folder>] [--seed Ryu-Sch/GemakTV@published]
// Point the manager's "Server" field (debug builds) at http://10.0.2.2:5080 from the Android emulator.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

var options = args.Select((a, i) => (a, i)).Where(x => x.a.StartsWith("--")).ToDictionary(x => x.a[2..], x => x.i + 1 < args.Length ? args[x.i + 1] : "");
var port = int.Parse(options.GetValueOrDefault("port", "5080"));
var token = options.GetValueOrDefault("token", "test-token");
var login = options.GetValueOrDefault("user", "gemaktv-bot");
var data = options.GetValueOrDefault("data", Path.Combine(Path.GetTempPath(), "gemaktv-fakegithub"));
Directory.CreateDirectory(data);

if (options.TryGetValue("seed", out var seed))
{
    // Copy the real published files so the manager starts from the real channel list.
    var at = seed.IndexOf('@');
    using var http = new HttpClient();
    foreach (var file in new[] { "config.gtv", "README.md" })
    {
        var url = $"https://raw.githubusercontent.com/{seed[..at]}/{seed[(at + 1)..]}/{file}";
        var response = await http.GetAsync(url);
        if (response.IsSuccessStatusCode)
        {
            await File.WriteAllBytesAsync(Path.Combine(data, file), await response.Content.ReadAsByteArrayAsync());
            Console.WriteLine($"Seeded {file} from {url}");
        }
    }
}

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
builder.Logging.ClearProviders();
var app = builder.Build();
var gate = new object();

app.Use(async (context, next) =>
{
    await next();
    Console.WriteLine($"{DateTime.Now:HH:mm:ss} {context.Request.Method} {context.Request.Path}{context.Request.QueryString} -> {context.Response.StatusCode}");
});

bool Authorized(HttpRequest request) => request.Headers.Authorization.ToString() == $"Bearer {token}";
IResult Json(JsonNode node, int status = 200) => Results.Content(node.ToJsonString(), "application/json", Encoding.UTF8, status);
IResult Error(int status, string message) => Json(new JsonObject { ["message"] = message }, status);
string BlobSha(byte[] content) => Convert.ToHexStringLower(SHA1.HashData([.. Encoding.ASCII.GetBytes($"blob {content.Length}\0"), .. content]));
string FilePath(string path) => Path.Combine(data, path.Replace('/', Path.DirectorySeparatorChar));

app.MapGet("/user", (HttpRequest request) =>
    Authorized(request) ? Json(new JsonObject { ["login"] = login, ["name"] = "GemakTV bot (local test)" }) : Error(401, "Bad credentials"));

app.MapGet("/repos/{owner}/{repo}", (HttpRequest request) =>
    Json(new JsonObject
    {
        ["private"] = false,
        ["permissions"] = new JsonObject { ["push"] = Authorized(request), ["admin"] = false, ["pull"] = true },
    }));

app.MapGet("/repos/{owner}/{repo}/contents/{**path}", (string path, HttpRequest request) =>
{
    var file = FilePath(path);
    if (!File.Exists(file)) return Error(404, "Not Found");
    var content = File.ReadAllBytes(file);
    if (request.Headers.Accept.ToString().Contains("raw")) return Results.Bytes(content, "application/octet-stream");
    return Json(new JsonObject
    {
        ["type"] = "file",
        ["path"] = path,
        ["encoding"] = "base64",
        ["size"] = content.Length,
        ["sha"] = BlobSha(content),
        ["content"] = string.Join('\n', Convert.ToBase64String(content).Chunk(60).Select(c => new string(c))),
    });
});

app.MapPut("/repos/{owner}/{repo}/contents/{**path}", async (string path, HttpRequest request) =>
{
    if (!Authorized(request)) return Error(401, "Bad credentials");
    var body = JsonNode.Parse(await new StreamReader(request.Body).ReadToEndAsync())!;
    var sha = body["sha"]?.GetValue<string>();
    var bytes = Convert.FromBase64String(body["content"]!.GetValue<string>());
    var file = FilePath(path);
    lock (gate)
    {
        if (File.Exists(file))
        {
            var current = BlobSha(File.ReadAllBytes(file));
            if (sha is null) return Error(422, "Invalid request.\n\n\"sha\" wasn't supplied.");
            if (sha != current) return Error(409, $"{path} does not match {sha}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, bytes);
    }
    Console.WriteLine($"   wrote {path} ({bytes.Length} bytes): {body["message"]}");
    return Json(new JsonObject { ["content"] = new JsonObject { ["path"] = path, ["sha"] = BlobSha(bytes) }, ["commit"] = new JsonObject { ["message"] = body["message"]?.GetValue<string>() } });
});

app.MapGet("/raw/{owner}/{repo}/{branch}/{**path}", (string path) =>
    File.Exists(FilePath(path)) ? Results.Bytes(File.ReadAllBytes(FilePath(path)), "application/octet-stream") : Results.NotFound());

Console.WriteLine($"Fake GitHub on http://localhost:{port} (emulator: http://10.0.2.2:{port}); user {login}; data in {data}");
app.Run();
