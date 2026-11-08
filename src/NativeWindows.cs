using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace AiTime
{
    public static class NativeClock
    {
        [DllImport("kernel32.dll")] private static extern bool QueryUnbiasedInterruptTime(out ulong value);
        public static ulong NowMilliseconds { get { ulong n; if (!QueryUnbiasedInterruptTime(out n)) throw new InvalidOperationException("无法读取 Windows 单调时钟。"); return n / 10000; } }
    }
    public static class NativeWindows
    {
        private delegate bool EnumProc(IntPtr h, IntPtr p);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr p);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder b, int n);
        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr p, uint flags, StringBuilder path, ref int n);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int command);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        public static string NormalizedPath(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return "";
            string p = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (p.StartsWith("\\\\?\\", StringComparison.Ordinal)) p = p.Substring(4);
            return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
        }
        public static bool IsAllowedExecutablePath(string path)
        {
            try { return !String.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) && Path.GetPathRoot(path).Length >= 3 && String.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path); }
            catch (ArgumentException) { return false; } catch (NotSupportedException) { return false; } catch (PathTooLongException) { return false; }
        }
        public static string PathForPid(uint pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid);
            if (h == IntPtr.Zero) return "";
            try { var s = new StringBuilder(32768); int n = s.Capacity; return QueryFullProcessImageName(h, 0, s, ref n) ? s.ToString() : ""; }
            finally { CloseHandle(h); }
        }
        public static List<AllowedApp> ListVisibleApplications()
        {
            var apps = new Dictionary<string, AllowedApp>(StringComparer.OrdinalIgnoreCase);
            uint self = (uint)Process.GetCurrentProcess().Id;
            EnumWindows(delegate(IntPtr h, IntPtr unused) {
                if (!IsWindowVisible(h)) return true;
                var title = new StringBuilder(1024); GetWindowText(h, title, title.Capacity);
                if (title.Length == 0) return true;
                uint pid; GetWindowThreadProcessId(h, out pid); if (pid == self) return true;
                string p = PathForPid(pid); if (!IsAllowedExecutablePath(p)) return true;
                string norm = NormalizedPath(p);
                if (!apps.ContainsKey(norm)) apps.Add(norm, new AllowedApp { Name = title.ToString(), Path = p, Arguments = "" });
                return true;
            }, IntPtr.Zero);
            return apps.Values.OrderBy(a => a.Name).ToList();
        }
        public static bool IsSameProcess(int pid, long startTicks)
        {
            try { using (Process p = Process.GetProcessById(pid)) return !p.HasExited && p.StartTime.ToUniversalTime().Ticks == startTicks; }
            catch (ArgumentException) { return false; } catch (InvalidOperationException) { return false; } catch (System.ComponentModel.Win32Exception) { return false; }
        }
        public static string QuoteArg(string arg)
        {
            if (arg == null) arg = "";
            var b = new StringBuilder("\""); int slashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { b.Append('\\', slashes * 2 + 1); b.Append('"'); slashes = 0; continue; }
                b.Append('\\', slashes); slashes = 0; b.Append(c);
            }
            b.Append('\\', slashes * 2); b.Append('"'); return b.ToString();
        }
    }
}
