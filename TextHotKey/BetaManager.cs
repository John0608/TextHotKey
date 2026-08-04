using Octokit;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TextHotKey
{
    // 테스트(베타) 프로그램: 기기 참가 코드 발급 + 신청 이슈 생성 + 승인(허용목록) 확인.
    //
    // 승인 흐름(백엔드 없음):
    //  1) 앱이 기기별 고유 참가 코드(TH-XXXXXX)를 생성/저장
    //  2) 사용자가 "신청" → 코드·이메일이 담긴 GitHub 이슈가 열림
    //  3) 오너가 beta-allowlist.json에 그 코드를 추가·푸시 = 승인
    //  4) 앱이 허용목록을 조회해 이 기기 코드가 있으면 승인됨으로 판정
    internal class BetaManager
    {
        private const string GitHubOwner = "John0608";
        private const string GitHubRepo = "TextHotKey";
        private const string AllowlistPath = "beta-allowlist.json";

        private readonly SettingManager _settings;

        public BetaManager(SettingManager settings) => _settings = settings;

        // 기기 고유 참가 코드. 설정에 저장된 GUID 기반이라 재실행해도 동일하다.
        public string GetDeviceCode()
        {
            var id = _settings.Get("BetaDeviceId", "");
            if (string.IsNullOrEmpty(id))
            {
                id = Guid.NewGuid().ToString("N");
                _settings.Set("BetaDeviceId", id);
            }
            return "TH-" + id.Substring(0, 6).ToUpperInvariant();
        }

        public string GetEmail() => _settings.Get("BetaEmail", "");
        public void SetEmail(string email) => _settings.Set("BetaEmail", email ?? "");

        // 신청용 GitHub 이슈 URL(제목/본문 미리 채움).
        public string BuildRequestIssueUrl(string email)
        {
            var code = GetDeviceCode();
            var title = Uri.EscapeDataString($"[베타 신청] {code}");
            var body = Uri.EscapeDataString(
                "테스트(베타) 프로그램 참가를 신청합니다.\n\n" +
                $"- 참가 코드: {code}\n" +
                $"- 이메일: {email}\n\n" +
                "───\n" +
                $"승인: beta-allowlist.json 의 approved 배열에 아래를 추가하세요.\n" +
                $"{{ \"code\": \"{code}\", \"email\": \"{email}\", \"name\": \"\" }}");
            return $"https://github.com/{GitHubOwner}/{GitHubRepo}/issues/new" +
                   $"?title={title}&body={body}";
        }

        // 허용목록을 받아 이 기기 코드가 승인됐는지 확인한다. 실패/미승인 시 false.
        public async Task<bool> IsApprovedAsync()
        {
            try
            {
                var code = GetDeviceCode();
                var client = new GitHubClient(new ProductHeaderValue("TextHotKey"));
                var contents = await client.Repository.Content.GetAllContents(
                    GitHubOwner, GitHubRepo, AllowlistPath);
                var json = contents.Count > 0 ? contents[0].Content : null;
                if (string.IsNullOrEmpty(json)) return false;

                var doc = JsonSerializer.Deserialize<Allowlist>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return doc?.Approved?.Any(e =>
                    string.Equals(e.Code, code, StringComparison.OrdinalIgnoreCase)) ?? false;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Beta allowlist check failed: {ex.Message}");
                return false;
            }
        }

        private class Allowlist
        {
            [JsonPropertyName("approved")]
            public List<Entry> Approved { get; set; } = new();
        }

        private class Entry
        {
            [JsonPropertyName("code")] public string Code { get; set; } = "";
            [JsonPropertyName("email")] public string? Email { get; set; }
            [JsonPropertyName("name")] public string? Name { get; set; }
        }
    }
}
