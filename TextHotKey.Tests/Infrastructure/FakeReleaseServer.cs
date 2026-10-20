using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TextHotKey.Tests.Infrastructure;

// GitHub 릴리스 API + 에셋 다운로드를 흉내 내는 로컬 HTTP 서버.
// 앱은 THK_TEST_UPDATE_API=BaseUrl 로 이 서버를 GitHub 대신 바라본다.
// (Octokit은 github.com 이외 주소에 "api/v3/"를 붙이므로 그 경로로 응답한다.)
internal sealed class FakeReleaseServer : IDisposable
{
    public enum DownloadMode
    {
        Normal,     // 파일 전체 전송
        NotFound,   // 404
        Truncated,  // Content-Length는 전체 크기로 알리고 절반만 보낸 뒤 연결 종료
    }

    public sealed record FakeRelease(string Tag, bool Prerelease = false, bool Draft = false, string? AssetName = "TextHotKey-win-x64.zip");

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public string BaseUrl { get; }
    public List<FakeRelease> Releases { get; } = new();
    public HttpStatusCode? ApiFailure { get; set; }
    public string? AssetFilePath { get; set; }
    public byte[]? AssetBytes { get; set; }
    public DownloadMode Mode { get; set; } = DownloadMode.Normal;
    public ConcurrentQueue<string> Requests { get; } = new();

    public FakeReleaseServer()
    {
        var port = GetFreePort();
        BaseUrl = $"http://localhost:{port}/";
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    public string AssetUrl(string name) => $"{BaseUrl}download/{name}";

    public int CountRequests(string pathPrefix) => Requests.Count(r => r.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase));

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        Requests.Enqueue(path);
        var resp = ctx.Response;
        try
        {
            const string repo = "/api/v3/repos/John0608/TextHotKey/releases";
            if (path.StartsWith("/api/v3/", StringComparison.Ordinal))
            {
                if (ApiFailure is { } code)
                {
                    WriteJson(resp, "{\"message\":\"fail\"}", code);
                    return;
                }
                if (path.Equals(repo + "/latest", StringComparison.OrdinalIgnoreCase))
                {
                    // GitHub와 같이 드래프트/사전 릴리스는 latest에서 제외.
                    var latest = Releases.FirstOrDefault(r => !r.Draft && !r.Prerelease);
                    if (latest == null) WriteJson(resp, "{\"message\":\"Not Found\"}", HttpStatusCode.NotFound);
                    else WriteJson(resp, JsonSerializer.Serialize(ToJson(latest, 1)));
                    return;
                }
                if (path.Equals(repo, StringComparison.OrdinalIgnoreCase))
                {
                    WriteJson(resp, JsonSerializer.Serialize(Releases.Select((r, i) => ToJson(r, i + 1))));
                    return;
                }
                WriteJson(resp, "{\"message\":\"Not Found\"}", HttpStatusCode.NotFound);
                return;
            }

            if (path.StartsWith("/download/", StringComparison.Ordinal))
            {
                if (Mode == DownloadMode.NotFound)
                {
                    resp.StatusCode = 404;
                    resp.Close();
                    return;
                }
                var bytes = AssetBytes ?? File.ReadAllBytes(AssetFilePath!);
                resp.ContentType = "application/zip";
                resp.ContentLength64 = bytes.Length;
                if (Mode == DownloadMode.Truncated)
                {
                    resp.OutputStream.Write(bytes, 0, bytes.Length / 2);
                    resp.OutputStream.Flush();
                    resp.Abort(); // 전체를 보내지 않고 연결을 끊는다.
                    return;
                }
                resp.OutputStream.Write(bytes, 0, bytes.Length);
                resp.Close();
                return;
            }

            resp.StatusCode = 404;
            resp.Close();
        }
        catch
        {
            try { resp.Abort(); } catch { /* 무시 */ }
        }
    }

    private object ToJson(FakeRelease r, int id) => new Dictionary<string, object?>
    {
        ["id"] = id,
        ["tag_name"] = r.Tag,
        ["name"] = r.Tag,
        ["draft"] = r.Draft,
        ["prerelease"] = r.Prerelease,
        ["html_url"] = $"{BaseUrl}releases/{r.Tag}",
        ["assets"] = r.AssetName == null
            ? Array.Empty<object>()
            : new object[]
            {
                new Dictionary<string, object?>
                {
                    ["id"] = id * 100,
                    ["name"] = r.AssetName,
                    ["browser_download_url"] = AssetUrl(r.AssetName),
                    ["state"] = "uploaded",
                    ["content_type"] = "application/zip",
                }
            },
    };

    private static void WriteJson(HttpListenerResponse resp, string json, HttpStatusCode code = HttpStatusCode.OK)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        resp.StatusCode = (int)code;
        resp.ContentType = "application/json; charset=utf-8";
        resp.ContentLength64 = bytes.Length;
        resp.OutputStream.Write(bytes, 0, bytes.Length);
        resp.Close();
    }

    private static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { /* 무시 */ }
        try { _loop.Wait(2000); } catch { /* 무시 */ }
    }
}
