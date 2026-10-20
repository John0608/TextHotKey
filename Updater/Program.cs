using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace Updater
{
    // TextHotKey 자동 업데이트 헬퍼.
    // 메인 앱이 임시 폴더로 복사해 실행한다. 다음을 순서대로 수행한다.
    //   1) 메인 앱(PID) 종료 대기 — 끝내 종료되지 않으면 아무것도 바꾸지 않고 중단
    //   2) 설치 폴더를 트랜잭션처럼 교체(Installer.Apply) — 실패하면 구버전 그대로 복원
    //   3) 결과 기록(update-result.txt, 앱이 다음 실행 때 실패를 안내) + 메인 앱 재실행
    //   4) 임시 zip 정리
    // 인자: --zip <경로> --dir <설치폴더> --exe <메인exe경로> --pid <프로세스ID>
    //       [--log <로그파일>] [--wait <앱 종료 대기 ms, 기본 30000>]
    internal static class Program
    {
        private static string _logPath = "";

        private static int Main(string[] args)
        {
            var opts = ParseArgs(args);

            _logPath = opts.TryGetValue("log", out var logArg)
                ? logArg
                : Path.Combine(Path.GetTempPath(), "TextHotKey_update", "updater.log");
            try { Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!); } catch { /* 무시 */ }
            var resultPath = Path.Combine(Path.GetDirectoryName(_logPath)!, "update-result.txt");

            try
            {
                Log($"Updater 시작. args: {string.Join(' ', args)}");

                if (!opts.TryGetValue("zip", out var zip) ||
                    !opts.TryGetValue("dir", out var dir) ||
                    !opts.TryGetValue("exe", out var exe))
                {
                    Log("필수 인자 누락 (--zip, --dir, --exe).");
                    return 1;
                }

                var waitMs = opts.TryGetValue("wait", out var w) && int.TryParse(w, out var ms) ? ms : 30000;
                if (opts.TryGetValue("pid", out var pidStr) && int.TryParse(pidStr, out var pid) && !WaitForExit(pid, waitMs))
                {
                    // 앱이 아직 떠 있다 → 파일을 건드리지 않는다(앱은 계속 실행 중이므로 재실행도 하지 않음).
                    WriteResult(resultPath, false, "실행 중인 TextHotKey가 종료되지 않아 업데이트를 중단했습니다.");
                    return 3;
                }

                if (!File.Exists(zip))
                {
                    Log($"zip 파일 없음: {zip}");
                    WriteResult(resultPath, false, "다운로드한 업데이트 파일을 찾지 못했습니다.");
                    Relaunch(exe);
                    return 2;
                }

                new Installer(Log).Apply(zip, dir);
                WriteResult(resultPath, true, "");
                Log("교체 완료. 앱 재실행.");
                Relaunch(exe);

                TryDelete(zip);
                Log("업데이트 성공.");
                return 0;
            }
            catch (Exception ex)
            {
                Log($"업데이트 실패: {ex}");
                WriteResult(resultPath, false, ex is UpdateException ? ex.Message : $"업데이트 중 오류: {ex.Message}");
                // 실패해도 사용자가 계속 쓸 수 있도록 기존 앱을 다시 띄운다(설치 폴더는 구버전으로 복원된 상태).
                if (opts.TryGetValue("exe", out var exe) && File.Exists(exe))
                    Relaunch(exe);
                return 99;
            }
        }

        // --key value 형태를 파싱한다.
        private static Dictionary<string, string> ParseArgs(string[] args)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    d[args[i][2..]] = args[i + 1];
                    i++;
                }
            }
            return d;
        }

        // 메인 앱이 종료될 때까지 대기. 시간 안에 종료되면(또는 이미 종료됐으면) true.
        private static bool WaitForExit(int pid, int timeoutMs)
        {
            try
            {
                var p = Process.GetProcessById(pid);
                Log($"PID {pid} 종료 대기...");
                if (!p.WaitForExit(timeoutMs))
                {
                    Log($"PID {pid} {timeoutMs}ms 내 미종료. 업데이트 중단.");
                    return false;
                }
                Log($"PID {pid} 종료 확인.");
                return true;
            }
            catch (ArgumentException)
            {
                Log($"PID {pid} 이미 종료됨.");
                return true;
            }
        }

        // 앱이 다음 실행 때 읽어 실패를 안내한다. 1행: ok|fail, 2행: 사용자용 메시지.
        private static void WriteResult(string path, bool ok, string message)
        {
            try { File.WriteAllText(path, (ok ? "ok" : "fail") + Environment.NewLine + message); }
            catch (Exception ex) { Log($"결과 기록 실패: {ex.Message}"); }
        }

        private static void Relaunch(string exePath)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exePath)!,
                });
            }
            catch (Exception ex)
            {
                Log($"재실행 실패: {ex.Message}");
            }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { /* 무시 */ }
        }

        private static void Log(string msg)
        {
            try
            {
                File.AppendAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}");
            }
            catch { /* 무시 */ }
            Debug.WriteLine(msg);
        }
    }
}
