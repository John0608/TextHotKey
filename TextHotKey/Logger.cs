using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace TextHotKey
{
    public static class Logger
    {
        static readonly bool active = true;

        public static void Info(string message)
        {
            if (active)
            {
                Debug.Print($"[INFO][{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
            }
        }
        public static void Warn(string message)
        {
            if (active)
            {
                Debug.Print($"[WARN][{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
            }
        }
        public static void Error(string message)
        {
            if (active)
            {
                Debug.Print($"[ERROR][{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
            }
        }
    }
}
