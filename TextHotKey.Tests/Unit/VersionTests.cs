namespace TextHotKey.Tests.Unit;

// 릴리스 태그 ↔ 버전 변환과 정렬. 업데이트 여부 판정의 기준이 된다.
public class VersionTests
{
    [Theory]
    [InlineData("v1.3.3", "1.3.3.0")]
    [InlineData("1.3.3", "1.3.3.0")]
    [InlineData("V2.0.10", "2.0.10.0")]
    [InlineData("v1.4.0-beta.1", "1.4.0.1")]
    [InlineData("v1.4.0-beta.12", "1.4.0.12")]
    public void ParseTag_ValidTags(string tag, string expected)
    {
        Assert.Equal(Version.Parse(expected), UpdateManager.ParseTag(tag));
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v1.3")]
    [InlineData("v1.3.3.4")]
    [InlineData("v1.3.3-rc.1")]
    [InlineData("v1.3.3-beta")]
    [InlineData("v1.3.3 ")]
    public void ParseTag_InvalidTags_ReturnNull(string tag)
    {
        Assert.Null(UpdateManager.ParseTag(tag));
    }

    [Theory]
    [InlineData("1.3.3.0", "1.3.3")]
    [InlineData("1.4.0.2", "1.4.0-beta.2")]
    public void FormatVersion_RoundTripsWithTag(string version, string expected)
    {
        var formatted = UpdateManager.FormatVersion(Version.Parse(version));
        Assert.Equal(expected, formatted);
        Assert.Equal(Version.Parse(version), UpdateManager.ParseTag("v" + formatted));
    }

    [Theory]
    [InlineData("v1.3.2", "v1.3.3")]
    [InlineData("v1.3.3", "v1.4.0-beta.1")]
    [InlineData("v1.4.0-beta.1", "v1.4.0-beta.2")]
    [InlineData("v1.9.0", "v1.10.0")]
    // 시맨틱 버전 규칙상 정식판은 같은 버전의 베타보다 새 버전이어야 한다.
    // 그래야 베타 사용자가 같은 버전의 정식판이 나왔을 때 업데이트를 받는다.
    [InlineData("v1.4.0-beta.3", "v1.4.0")]
    [InlineData("v1.4.0-beta.99", "v1.4.0")]
    [InlineData("v1.4.0", "v1.4.1-beta.1")]
    public void Ordering_NewerTagIsNewer(string older, string newer)
    {
        var o = UpdateManager.ParseTag(older)!;
        var n = UpdateManager.ParseTag(newer)!;
        Assert.True(UpdateManager.IsNewer(n, o));
        Assert.False(UpdateManager.IsNewer(o, n));
    }

    [Theory]
    [InlineData("v1.4.0")]
    [InlineData("v1.4.0-beta.2")]
    public void Ordering_SameVersionIsNotNewer(string tag)
    {
        var v = UpdateManager.ParseTag(tag)!;
        Assert.False(UpdateManager.IsNewer(v, v));
    }
}
