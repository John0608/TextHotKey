using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace Updater
{
    // TextHotKey 자동 업데이트 헬퍼.
    // 메인 앱이 임시 폴더로 복사해 실행한다. 다음을 순서대로 수행한다.
    //   1) 메인 앱(PID) 종료 대기
    //   2) 다운로드된 zip을 설치 폴더에 덮어쓰기(잠긴 파일은 잠깐 재시도)
    //   3) 메인 앱 재실행
    //   4) 임시 zip 정리
    // 인자: --zip <경로> --dir <설치폴더> --exe <메인exe경로> --pid <프로세스ID>
    internal static class Program
    {
        private static string _logPath = "";

        private static int Main(string[] args)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "TextHotKey_update");
            try { Directory.CreateDirectory(tempDir); } catch { /* 무시 */ }
            _logPath = Path.Combine(tempDir, "updater.log");

            var opts = ParseArgs(args);

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

                if (opts.TryGetValue("pid", out var pidStr) && int.TryParse(pidStr, out var pid))
                    WaitForExit(pid);

                if (!File.Exists(zip))
                {
                    Log($"zip 파일 없음: {zip}");
                    Relaunch(exe);
                    return 2;
                }

                ExtractOver(zip, dir);
                Log("교체 완료. 앱 재실행.");
                Relaunch(exe);

                TryDelete(zip);
                Log("업데이트 성공.");
                return 0;
            }
            catch (Exception ex)
            {
                Log($"업데이트 실패: {ex}");
                // 실패해도 사용자가 계속 쓸 수 있도록 기존 앱을 다시 띄운다.
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

        // 메인 앱이 종료될 때까지 대기(최대 30초).
        private static void WaitForExit(int pid)
        {
            try
            {
                var p = Process.GetProcessById(pid);
                Log($"PID {pid} 종료 대기...");
                if (!p.WaitForExit(30000))
                    Log($"PID {pid} 30초 내 미종료. 계속 진행.");
                else
                    Log($"PID {pid} 종료 확인.");
            }
            catch (ArgumentException)
            {
                Log($"PID {pid} 이미 종료됨.");
            }
        }

        // zip 내용을 설치 폴더에 덮어쓴다. 잠긴 파일은 잠깐 재시도한다.
        private static void ExtractOver(string zipPath, string destDir)
        {
            Directory.CreateDirectory(destDir);
            var destFull = Path.GetFullPath(destDir);

            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                // 디렉터리 엔트리는 건너뛴다(파일 추출 시 폴더 생성).
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                var targetPath = Path.GetFullPath(Path.Combine(destFull, entry.FullName));

                // zip-slip 방지: 대상 폴더를 벗어나는 경로는 무시.
                if (!targetPath.StartsWith(destFull, StringComparison.OrdinalIgnoreCase))
                {
                    Log($"경로 이탈 엔트리 건너뜀: {entry.FullName}");
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                ExtractEntryWithRetry(entry, targetPath);
            }
        }

        private static void ExtractEntryWithRetry(ZipArchiveEntry entry, string targetPath)
        {
            const int maxAttempts = 20;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    entry.ExtractToFile(targetPath, overwrite: true);
                    return;
                }
                catch (Exception ex) when (
                    (ex is IOException || ex is UnauthorizedAccessException) && attempt < maxAttempts)
                {
                    // 앱 종료 직후 파일이 잠시 잠겨 있을 수 있다. 잠깐 대기 후 재시도.
                    if (attempt == 1)
                        Log($"파일 잠김, 재시도: {targetPath}");
                    Thread.Sleep(500);
                }
            }
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
