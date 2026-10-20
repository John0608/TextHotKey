using System.IO;
using System.IO.Compression;

namespace Updater
{
    // 사용자에게 보여줄 수 있는 업데이트 실패(설치 폴더는 구버전으로 복원된 상태).
    internal sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

    // 설치 폴더를 새 패키지로 "전부 아니면 전무" 방식으로 교체한다.
    //   1) zip 전체를 작업 폴더(staging)에 먼저 푼다. zip이 깨졌으면 여기서 실패 → 설치 폴더는 그대로.
    //   2) 기존 파일을 backup으로 옮긴다. 잠긴 파일이 있으면 옮긴 것을 되돌리고 중단 → 구버전 그대로.
    //   3) staging 파일을 설치 폴더로 옮긴다. 실패하면 넣은 파일을 지우고 backup을 되돌림 → 구버전 그대로.
    //   4) 성공하면 작업 폴더(backup 포함)를 지운다. 새 패키지에 없는 옛 파일은 backup과 함께 사라진다.
    // 작업 폴더는 설치 폴더 안에 두어 같은 볼륨 내 이동(rename)만 하게 한다(빠르고, 복사 도중 실패가 없음).
    internal sealed class Installer(Action<string> log)
    {
        public const string WorkDirName = ".update";

        private const int MaxAttempts = 20;
        private const int RetryDelayMs = 500;

        // 패키지 파일이 아니라 설치 마법사(Inno Setup)가 만든 제거 프로그램. 교체·삭제하지 않는다.
        private static bool IsPreserved(string relativePath) =>
            !relativePath.Contains(Path.DirectorySeparatorChar) &&
            relativePath.StartsWith("unins", StringComparison.OrdinalIgnoreCase);

        public void Apply(string zipPath, string installDir)
        {
            var install = Path.GetFullPath(installDir);
            var work = Path.Combine(install, WorkDirName);
            var staging = Path.Combine(work, "staging");
            var backup = Path.Combine(work, "backup");

            // 이전에 비정상 종료된 업데이트의 잔재 정리.
            DeleteDirectory(work);
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(backup);

            try
            {
                var newFiles = Extract(zipPath, staging);
                if (newFiles.Count == 0)
                    throw new UpdateException("업데이트 패키지가 비어 있습니다.");

                var oldFiles = Directory.EnumerateFiles(install, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(install, f))
                    .Where(r => !r.StartsWith(WorkDirName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    .Where(r => !IsPreserved(r))
                    .ToList();
                log($"교체 준비: 기존 {oldFiles.Count}개, 신규 {newFiles.Count}개");

                // 2) 기존 파일 → backup
                var movedOut = new List<string>();
                try
                {
                    foreach (var rel in oldFiles)
                    {
                        MoveWithRetry(Path.Combine(install, rel), Path.Combine(backup, rel));
                        movedOut.Add(rel);
                    }
                }
                catch (Exception ex)
                {
                    log($"기존 파일 이동 실패 → 되돌림: {ex.Message}");
                    Restore(movedOut, backup, install);
                    throw new UpdateException("설치 폴더의 파일이 사용 중이라 업데이트하지 못했습니다. 이전 버전을 그대로 사용합니다.", ex);
                }

                // 3) staging → 설치 폴더
                var movedIn = new List<string>();
                try
                {
                    foreach (var rel in newFiles)
                    {
                        MoveWithRetry(Path.Combine(staging, rel), Path.Combine(install, rel));
                        movedIn.Add(rel);
                    }
                }
                catch (Exception ex)
                {
                    log($"새 파일 배치 실패 → 되돌림: {ex.Message}");
                    foreach (var rel in movedIn)
                        TryDeleteFile(Path.Combine(install, rel));
                    Restore(movedOut, backup, install);
                    throw new UpdateException("새 버전 파일을 배치하지 못했습니다. 이전 버전을 그대로 사용합니다.", ex);
                }

                RemoveEmptyDirectories(install, work);
            }
            catch (InvalidDataException ex)
            {
                throw new UpdateException("업데이트 파일이 손상되었습니다. 이전 버전을 그대로 사용합니다.", ex);
            }
            finally
            {
                DeleteDirectory(work);
            }
        }

        // zip을 staging에 풀고, 풀린 파일들의 상대 경로를 돌려준다.
        private List<string> Extract(string zipPath, string staging)
        {
            var files = new List<string>();
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                // 디렉터리 엔트리는 건너뛴다(파일 추출 시 폴더 생성).
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                var target = Path.GetFullPath(Path.Combine(staging, entry.FullName));

                // zip-slip 방지: 대상 폴더를 벗어나는 경로는 무시.
                if (!target.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    log($"경로 이탈 엔트리 건너뜀: {entry.FullName}");
                    continue;
                }

                var rel = Path.GetRelativePath(staging, target);
                if (IsPreserved(rel))
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                files.Add(rel);
            }
            return files;
        }

        private void Restore(List<string> movedOut, string backup, string install)
        {
            foreach (var rel in movedOut)
            {
                try { MoveWithRetry(Path.Combine(backup, rel), Path.Combine(install, rel)); }
                catch (Exception ex) { log($"복원 실패(수동 복구 필요): {rel} — {ex.Message}"); }
            }
        }

        // 앱 종료 직후·백신 검사 등으로 파일이 잠시 잠겨 있을 수 있다. 잠깐씩 재시도한다.
        private void MoveWithRetry(string from, string to)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(from, to, overwrite: true);
                    return;
                }
                catch (Exception ex) when (
                    (ex is IOException || ex is UnauthorizedAccessException) && attempt < MaxAttempts)
                {
                    if (attempt == 1)
                        log($"파일 잠김, 재시도: {from}");
                    Thread.Sleep(RetryDelayMs);
                }
            }
        }

        private static void RemoveEmptyDirectories(string root, string skip)
        {
            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                         .Where(d => !d.StartsWith(skip, StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch { /* 무시 */ }
            }
        }

        private void DeleteDirectory(string dir)
        {
            for (int i = 0; i < 5 && Directory.Exists(dir); i++)
            {
                try { Directory.Delete(dir, recursive: true); }
                catch (Exception ex)
                {
                    if (i == 4) log($"작업 폴더 정리 실패: {dir} — {ex.Message}");
                    Thread.Sleep(200);
                }
            }
        }

        private static void TryDeleteFile(string path)
        {
            try { File.Delete(path); } catch { /* 무시 */ }
        }
    }
}
