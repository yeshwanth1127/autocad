using System;
using System.IO;
using System.Reflection;
using System.Threading;

namespace autocad_final.Agent
{
    /// <summary>
    /// Append-only file logger used to diagnose hangs/deadlocks.
    /// Writes to AgentDebug.log beside the plugin DLL.
    /// Safe to call from any thread; every write is a separate File.AppendAllText
    /// so partial output is always visible even when the process is frozen.
    /// </summary>
    internal static class AgentLog
    {
        private static readonly string _path;
        private static int _seq;

        static AgentLog()
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                          ?? AppDomain.CurrentDomain.BaseDirectory;
                _path = System.IO.Path.Combine(dir, "AgentDebug.log");
                // Write a session header so we can tell log files apart
                File.AppendAllText(_path,
                    Environment.NewLine +
                    "══════════════════════════════════════════════════════" + Environment.NewLine +
                    "SESSION START  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                    "  pid=" + System.Diagnostics.Process.GetCurrentProcess().Id + Environment.NewLine +
                    "══════════════════════════════════════════════════════" + Environment.NewLine);
            }
            catch { /* never blow up the plugin */ }
        }

        public static void Write(string tag, string message = "")
        {
            try
            {
                int seq  = Interlocked.Increment(ref _seq);
                int tid  = Thread.CurrentThread.ManagedThreadId;
                string ts = DateTime.Now.ToString("HH:mm:ss.fff");
                string line = $"[{ts}] #{seq:D4} T{tid:D3} [{tag}] {message}{Environment.NewLine}";
                File.AppendAllText(_path, line);
            }
            catch { }
        }

        public static string Path => _path;
    }
}
