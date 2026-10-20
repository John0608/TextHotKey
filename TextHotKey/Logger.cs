using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace TextHotKey
{
    public static class Logger
    {
        static readonly bool active = true;
        static readonly object fileLock = new();

        public static void Info(string message)
        {
            if (active)
            {
                Write($"[INFO][{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
            }
        }
        public static void Warn(string message)
        {
            if (active)
            {
                Write($"[WARN][{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
            }
        }
        public static void Error(string message)
        {
            if (active)
            {
                Write($"[ERROR][{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
            }
        }

        // 디버그 출력 + (테스트 모드일 때) 로그 파일.
        static void Write(string line)
        {
            Debug.Print(line);
            if (AppEnv.LogFile is not { } path) return;
            try
            {
                lock (fileLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
            catch { /* 로그 실패는 무시 */ }
        }
    }
}
