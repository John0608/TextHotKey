using System.IO;

namespace TextHotKey
{
    // 실행 환경 경로/설정. 자동화 테스트가 실사용 환경(설정 파일, 레지스트리, 실행 중인 앱)을
    // 건드리지 않도록 환경변수로 격리할 수 있게 한다. 실사용 시엔 환경변수가 없으므로 기본값과 같다.
    //   THK_TEST_ROOT       : 테스트 격리 루트. 설정되면 테스트 모드(데이터/임시 폴더·단일 인스턴스 이름 분리,
    //                         레지스트리 기록·베타 서버 조회 생략, 로그 파일 기록).
    //   THK_TEST_UPDATE_API : GitHub API 대신 사용할 주소(가짜 릴리스 서버).
    internal static class AppEnv
    {
        public static readonly string? TestRoot = ReadEnv("THK_TEST_ROOT");

        public static bool IsTestMode => TestRoot != null;

        // 설정·단축키 저장 폴더.
        public static string DataDir => IsTestMode
            ? Path.Combine(TestRoot!, "data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TextHotKey");

        // 업데이트 zip 다운로드 + Updater 임시 실행 폴더.
        public static string UpdateTempDir => IsTestMode
            ? Path.Combine(TestRoot!, "update-temp")
            : Path.Combine(Path.GetTempPath(), "TextHotKey_update");

        // 단일 인스턴스 Mutex/Event 이름 접미사. 테스트 인스턴스가 실사용 중인 앱과 충돌하지 않게 한다.
        public static string InstanceSuffix => IsTestMode
            ? "_Test_" + (uint)StringComparer.OrdinalIgnoreCase.GetHashCode(Path.GetFullPath(TestRoot!))
            : "";

        // 테스트 모드에서만 앱 로그를 파일로 남긴다(테스트가 앱 내부 진행 상황을 확인하는 용도).
        public static string? LogFile => IsTestMode ? Path.Combine(TestRoot!, "app.log") : null;

        public static Uri? UpdateApiUrl =>
            ReadEnv("THK_TEST_UPDATE_API") is { } url ? new Uri(url) : null;

        private static string? ReadEnv(string name)
        {
            var v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
    }
}
