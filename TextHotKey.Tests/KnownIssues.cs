namespace TextHotKey.Tests;

// 테스트로 재현된 실제 결함. 수정하면 해당 테스트의 Skip을 지워 회귀 테스트로 전환한다.
internal static class KnownIssues
{
    public const string BetaSortsAboveStable =
        "알려진 결함: vX.Y.Z-beta.N이 X.Y.Z.N으로 변환돼 같은 X.Y.Z 정식판(X.Y.Z.0)보다 높게 정렬됨 → 베타 사용자가 정식판을 못 받음";

    public const string CorruptSettingsCrash =
        "알려진 결함: settings.json/hotkeys.json이 깨지면 생성자에서 JsonException → 앱 시작 불가";

    public const string PartialUpdate =
        "알려진 결함: 교체 중 파일 하나가 잠기면 일부만 신버전으로 바뀐 설치본이 남음(백업·롤백 없음)";

    public const string StaleFiles =
        "알려진 결함: 신버전 패키지에서 빠진 파일이 설치 폴더에 계속 남음";
}
