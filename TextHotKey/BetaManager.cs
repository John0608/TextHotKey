using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TextHotKey
{
    // 테스트(베타) 프로그램: 기기 참가 코드 발급 + Supabase로 신청/승인 확인.
    //
    // 백엔드 = Supabase(오너 전용). supabase/setup.sql 참고.
    //  1) 앱이 기기별 고유 참가 코드(TH-XXXXXX)를 생성/저장
    //  2) 사용자가 "신청" → beta_requests 테이블에 code/email INSERT (anon 키, RLS로 insert만 허용)
    //  3) 오너가 대시보드에서 해당 행 approved=true 로 승인
    //  4) 앱이 rpc beta_is_approved(code) 로 승인 여부(boolean)만 조회
    internal class BetaManager
    {
        // Supabase 프로젝트 설정. anon 키는 공개 키이며 RLS로 보호된다(INSERT만 허용).
        // 대시보드 → Project Settings → API 에서 확인.
        private const string SupabaseUrl = "https://oeybcebycdzixeiriotc.supabase.co";
        private const string SupabaseAnonKey = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Im9leWJjZWJ5Y2R6aXhlaXJpb3RjIiwicm9sZSI6ImFub24iLCJpYXQiOjE3ODU4MjA3NTAsImV4cCI6MjEwMTM5Njc1MH0.W4QwHULu08cKoZWivgBGKS1LJ1xDlCxbTmHHfyQDKy8";

        private readonly SettingManager _settings;
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

        public BetaManager(SettingManager settings) => _settings = settings;

        // Supabase 설정이 채워졌는지(플레이스홀더가 아닌지) 확인.
        private static bool Configured =>
            !SupabaseUrl.Contains("YOUR_PROJECT") && !SupabaseAnonKey.StartsWith("YOUR_");

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

        // 참가코드(기기 코드)를 새로 발급한다. 완전 탈퇴 후 재신청 시 새 신원으로 시작하기 위함.
        public void ResetDeviceCode() => _settings.Set("BetaDeviceId", Guid.NewGuid().ToString("N"));

        // 신청: beta_requests 에 code/email 을 INSERT 한다. 성공 시 true.
        // 콜드 스타트(첫 TLS 연결) 등 일시적 실패에 대비해 짧게 1회 재시도한다.
        public async Task<bool> SubmitRequestAsync(string email)
        {
            if (!Configured)
            {
                Logger.Warn("Beta submit skipped: Supabase not configured.");
                return false;
            }

            var code = GetDeviceCode();
            SetEmail(email);
            var json = JsonSerializer.Serialize(new
            {
                code,
                email = string.IsNullOrWhiteSpace(email) ? null : email
            });

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    using var req = NewRequest(HttpMethod.Post, "/rest/v1/beta_requests");
                    req.Headers.Add("Prefer", "return=minimal");
                    req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                    using var resp = await Http.SendAsync(req);
                    if (resp.IsSuccessStatusCode) return true;
                    Logger.Warn($"Beta submit HTTP {(int)resp.StatusCode} (attempt {attempt})");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Beta submit attempt {attempt} failed: {ex.Message}");
                }
                if (attempt < 2) await Task.Delay(500);
            }
            return false;
        }

        // 승인 확인: rpc beta_is_approved(p_code) → boolean. 실패/미승인 시 false.
        public async Task<bool> IsApprovedAsync()
        {
            if (!Configured) return false;

            var json = JsonSerializer.Serialize(new { p_code = GetDeviceCode() });

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    using var req = NewRequest(HttpMethod.Post, "/rest/v1/rpc/beta_is_approved");
                    req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                    using var resp = await Http.SendAsync(req);
                    if (resp.IsSuccessStatusCode)
                    {
                        var body = (await resp.Content.ReadAsStringAsync()).Trim();
                        return body.Equals("true", StringComparison.OrdinalIgnoreCase);
                    }
                    Logger.Warn($"Beta approval HTTP {(int)resp.StatusCode} (attempt {attempt})");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Beta approval attempt {attempt} failed: {ex.Message}");
                }
                if (attempt < 2) await Task.Delay(500);
            }
            return false;
        }

        // 참가 완전 탈퇴: rpc beta_leave(p_code)로 이 기기 코드의 신청/승인 행을 서버에서 삭제한다.
        public async Task<bool> LeaveAsync()
        {
            if (!Configured) return false;

            var json = JsonSerializer.Serialize(new { p_code = GetDeviceCode() });

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    using var req = NewRequest(HttpMethod.Post, "/rest/v1/rpc/beta_leave");
                    req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                    using var resp = await Http.SendAsync(req);
                    if (resp.IsSuccessStatusCode) return true;
                    Logger.Warn($"Beta leave HTTP {(int)resp.StatusCode} (attempt {attempt})");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Beta leave attempt {attempt} failed: {ex.Message}");
                }
                if (attempt < 2) await Task.Delay(500);
            }
            return false;
        }

        // Supabase REST 요청 뼈대(인증 헤더 포함) 생성.
        private static HttpRequestMessage NewRequest(HttpMethod method, string path)
        {
            var req = new HttpRequestMessage(method, SupabaseUrl + path);
            req.Headers.Add("apikey", SupabaseAnonKey);
            req.Headers.Add("Authorization", $"Bearer {SupabaseAnonKey}");
            return req;
        }
    }
}
