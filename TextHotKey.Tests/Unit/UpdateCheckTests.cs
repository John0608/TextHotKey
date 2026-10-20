using System.Net;
using TextHotKey.Tests.Infrastructure;

namespace TextHotKey.Tests.Unit;

[CollectionDefinition(Name, DisableParallelization = true)]
public class EnvVarCollection { public const string Name = "EnvVars"; }

// 가짜 릴리스 서버를 상대로 UpdateManager.CheckAsync의 판정을 검증한다.
// 테스트 대상 어셈블리(TextHotKey.dll)의 버전 = csproj의 <Version> (현재 버전).
[Collection(EnvVarCollection.Name)]
public class UpdateCheckTests : IDisposable
{
    private readonly FakeReleaseServer _server = new();
    private readonly string _current = new UpdateManager().GetCurrentVersion();

    public UpdateCheckTests()
    {
        Environment.SetEnvironmentVariable("THK_TEST_UPDATE_API", _server.BaseUrl);
    }

    [Fact]
    public async Task NewerStable_IsAvailable_WithZipUrl()
    {
        _server.Releases.Add(new("v99.0.0"));
        var info = await new UpdateManager().CheckAsync();
        Assert.False(info.Failed);
        Assert.True(info.UpdateAvailable);
        Assert.Equal("99.0.0", info.Latest);
        Assert.Equal(_server.AssetUrl("TextHotKey-win-x64.zip"), info.DownloadUrl);
    }

    [Fact]
    public async Task SameVersion_IsNotAvailable()
    {
        _server.Releases.Add(new("v" + _current));
        var info = await new UpdateManager().CheckAsync();
        Assert.False(info.Failed);
        Assert.False(info.UpdateAvailable);
    }

    [Fact]
    public async Task OlderVersion_IsNotAvailable()
    {
        _server.Releases.Add(new("v0.0.1"));
        var info = await new UpdateManager().CheckAsync();
        Assert.False(info.UpdateAvailable);
    }

    [Fact]
    public async Task StableChannel_IgnoresPrerelease()
    {
        _server.Releases.Add(new("v99.0.0-beta.1", Prerelease: true));
        _server.Releases.Add(new("v" + _current));
        var info = await new UpdateManager().CheckAsync(includePrerelease: false);
        Assert.False(info.UpdateAvailable);
    }

    [Fact]
    public async Task BetaChannel_PicksHighestNonDraft()
    {
        _server.Releases.Add(new("v99.0.0-beta.9", Draft: true));
        _server.Releases.Add(new("v98.0.0-beta.2", Prerelease: true));
        _server.Releases.Add(new("v98.0.0-beta.1", Prerelease: true));
        _server.Releases.Add(new("v" + _current));
        var info = await new UpdateManager().CheckAsync(includePrerelease: true);
        Assert.True(info.UpdateAvailable);
        Assert.Equal("98.0.0-beta.2", info.Latest);
    }

    // 베타 채널에서 같은 버전의 베타와 정식판이 함께 있으면 정식판을 고른다.
    [Fact]
    public async Task BetaChannel_PrefersStableOverSameVersionBeta()
    {
        _server.Releases.Add(new("v98.0.0-beta.3", Prerelease: true));
        _server.Releases.Add(new("v98.0.0"));
        var info = await new UpdateManager().CheckAsync(includePrerelease: true);
        Assert.True(info.UpdateAvailable);
        Assert.Equal("98.0.0", info.Latest);
    }

    [Fact]
    public async Task ReleaseWithoutZipAsset_HasNullDownloadUrl()
    {
        _server.Releases.Add(new("v99.0.0", AssetName: null));
        var info = await new UpdateManager().CheckAsync();
        Assert.True(info.UpdateAvailable);
        Assert.Null(info.DownloadUrl);
    }

    [Fact]
    public async Task NoRelease_IsNotFailure()
    {
        var info = await new UpdateManager().CheckAsync();
        Assert.False(info.Failed);
        Assert.False(info.UpdateAvailable);
    }

    [Fact]
    public async Task ServerError_IsFailure()
    {
        _server.ApiFailure = HttpStatusCode.InternalServerError;
        var info = await new UpdateManager().CheckAsync();
        Assert.True(info.Failed);
        Assert.False(info.UpdateAvailable);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("THK_TEST_UPDATE_API", null);
        _server.Dispose();
    }
}
