namespace TextHotKey.Tests;

// 테스트로 재현된 실제 결함. 수정하면 해당 테스트의 Skip을 지워 회귀 테스트로 전환한다.
internal static class KnownIssues
{
    public const string PartialUpdate =
        "알려진 결함: 교체 중 파일 하나가 잠기면 일부만 신버전으로 바뀐 설치본이 남음(백업·롤백 없음)";

    public const string StaleFiles =
        "알려진 결함: 신버전 패키지에서 빠진 파일이 설치 폴더에 계속 남음";
}
