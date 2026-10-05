using System;
using System.IO;

namespace BTBridge
{
    // Own log file next to the DLL: the game's logger is filtered to Error by default
    // and is awkward to read from outside the process.
    public static class Log
    {
        private static readonly object Sync = new object();
        private static string path;

        public static void Init(string modDir)
        {
            path = Path.Combine(modDir ?? ".", "BTBridge.log");
            try
            {
                File.WriteAllText(path, "");
            }
            catch
            {
                path = null;
            }
        }

        public static void Info(string msg) => Write("INFO", msg);

        public static void Warn(string msg) => Write("WARN", msg);

        public static void Error(string msg, Exception e = null) =>
            Write("ERROR", e == null ? msg : msg + ": " + e);

        private static void Write(string level, string msg)
        {
            if (path == null)
            {
                return;
            }
            lock (Sync)
            {
                try
                {
                    File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}{Environment.NewLine}");
                }
                catch
                {
                    // Logging must never take the game down.
                }
            }
        }
    }
}
