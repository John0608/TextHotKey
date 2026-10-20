using TextHotKey.Tests.Infrastructure;

namespace TextHotKey.Tests.Unit;

// UpdateManager.DownloadAsync: 정상/실패 응답에서의 동작.
public class DownloadTests : IDisposable
{
    private readonly FakeReleaseServer _server = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "thk-tests", Guid.NewGuid().ToString("N"));
    private string Dest => Path.Combine(_dir, "update.zip");

    public DownloadTests()
    {
        _server.AssetBytes = new byte[3 * 1024 * 1024];
        Random.Shared.NextBytes(_server.AssetBytes);
    }

    [Fact]
    public async Task Normal_WritesWholeFile_AndReportsFullProgress()
    {
        var reports = new List<double>();
        var progress = new SyncProgress(reports.Add);

        await new UpdateManager().DownloadAsync(_server.AssetUrl("a.zip"), Dest, progress);

        Assert.Equal(_server.AssetBytes, File.ReadAllBytes(Dest));
        Assert.NotEmpty(reports);
        Assert.Equal(1.0, reports[^1], 3);
        Assert.Equal(reports.OrderBy(x => x), reports); // 진행률은 줄어들지 않는다
    }

    [Fact]
    public async Task NotFound_Throws()
    {
        _server.Mode = FakeReleaseServer.DownloadMode.NotFound;
        await Assert.ThrowsAsync<HttpRequestException>(
            () => new UpdateManager().DownloadAsync(_server.AssetUrl("a.zip"), Dest));
    }

    // 연결이 중간에 끊기면 "완료"로 취급하면 안 된다(잘린 zip으로 업데이터를 띄우게 됨).
    [Fact]
    public async Task ConnectionDroppedMidway_Throws()
    {
        _server.Mode = FakeReleaseServer.DownloadMode.Truncated;
        await Assert.ThrowsAnyAsync<Exception>(
            () => new UpdateManager().DownloadAsync(_server.AssetUrl("a.zip"), Dest));
    }

    [Fact]
    public async Task Cancel_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new UpdateManager().DownloadAsync(_server.AssetUrl("a.zip"), Dest, null, cts.Token));
    }

    // Progress<T>는 동기화 컨텍스트로 비동기 보고하므로 테스트에선 즉시 기록하는 구현을 쓴다.
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    public void Dispose()
    {
        _server.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* 무시 */ }
    }
}
