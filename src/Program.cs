using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace AiTime
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length > 0 && args[0] == "--watchdog") return Watchdog(args);
                if (args.Length == 2 && args[0] == "--recover") { GuardEngine.RestoreJournal(args[1]); return 0; }
                NativeWindows.SetProcessDPIAware();
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                if (args.Length >= 2 && (args[0] == "--self-test" || args[0] == "--integration-test")) return TestSuite.Run(args[1], args[0] == "--integration-test");
                if (args.Length == 3 && args[0] == "--snapshot") return MainForm.Snapshot(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
                if (args.Length >= 3 && args[0] == "--session-smoke")
                {
                    int seconds; if (!Int32.TryParse(args[2], out seconds) || seconds < 2 || seconds > 30) return 2;
                    int smokeCycles = 1, smokeRest = 0;
                    if (args.Length == 5 && (!Int32.TryParse(args[3], out smokeCycles) || !Int32.TryParse(args[4], out smokeRest) || smokeCycles < 1 || smokeCycles > 3 || smokeRest < 0 || smokeRest > 5)) return 2;
                    var smokeStore = new AppStore(Path.GetFullPath(args[1]));
                    int previousRecords = smokeStore.Records.Count;
                    using (var smoke = new SessionForm(smokeStore, smokeStore.Config.Allowed, seconds, true, smokeCycles, smokeRest, "短时锁定验证", true))
                    {
                        Application.Run(smoke);
                        var produced = smokeStore.Records.Skip(previousRecords).ToList();
                        bool complete = smoke.Failure == null && produced.Count == smokeCycles && produced.All(r => r.IsTest && r.ActualSeconds == seconds && r.Status == "已完成");
                        JsonFile.Write(Path.Combine(smokeStore.Root, "smoke-result.json"), new { Success = complete, Failure = smoke.Failure ?? (complete ? null : "本次验证未完成全部专注轮次。"), Records = produced });
                        return complete ? 0 : 1;
                    }
                }
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiTimeWindows");
                if (args.Length == 2 && args[0] == "--data-dir") root = Path.GetFullPath(args[1]);
                bool background = args.Length == 1 && args[0] == "--background";
                bool created;
                string identity = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
                using (var mutex = new Mutex(true, "Local\\AiTimeWindows-" + identity, out created))
                using (var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\AiTimeWindows-show-" + identity))
                {
                    if (!created)
                    {
                        if (background) return 0;
                        // The running instance may be hidden in the tray (no main window handle), so ask it to show itself.
                        showSignal.Set();
                        foreach (Process p in Process.GetProcessesByName("AiTime"))
                        { try { if (p.Id != Process.GetCurrentProcess().Id && p.MainWindowHandle != IntPtr.Zero) { NativeWindows.ShowWindow(p.MainWindowHandle, 9); NativeWindows.SetForegroundWindow(p.MainWindowHandle); break; } } finally { p.Dispose(); } }
                        return 0;
                    }
                    var store = new AppStore(root); store.RecoverAbandoned();
                    var form = new MainForm(store, background);
                    RegisteredWaitHandle wait = ThreadPool.RegisterWaitForSingleObject(showSignal, delegate {
                        try { if (form.IsHandleCreated && !form.IsDisposed) form.BeginInvoke(new Action(form.ShowFromTray)); }
                        catch (InvalidOperationException) { }
                    }, null, -1, false);
                    try { Application.Run(form); }
                    finally { wait.Unregister(null); }
                    mutex.ReleaseMutex();
                }
                return 0;
            }
            catch (Exception ex)
            {
                if (args.Length > 0 && args[0] == "--watchdog") return 2;
                MessageBox.Show("爱时间启动失败：\n" + ex.Message, "爱时间 · Windows", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
        internal static Process StartWatchdog(int pid, long ticks, string journal, string stopFile, string readyFile, string activePath = null)
        {
            string[] parts = { "--watchdog", pid.ToString(), ticks.ToString(), journal, stopFile, readyFile, activePath ?? "" };
            var psi = new ProcessStartInfo(Application.ExecutablePath, String.Join(" ", parts.Select(NativeWindows.QuoteArg))) { UseShellExecute = false, CreateNoWindow = true };
            return Process.Start(psi);
        }
        private static int Watchdog(string[] args)
        {
            if (args.Length != 7) return 2;
            int pid; long ticks;
            if (!Int32.TryParse(args[1], out pid) || !Int64.TryParse(args[2], out ticks)) return 2;
            string journal = Path.GetFullPath(args[3]), stop = Path.GetFullPath(args[4]), ready = Path.GetFullPath(args[5]);
            Directory.CreateDirectory(Path.GetDirectoryName(ready)); File.WriteAllText(ready, Process.GetCurrentProcess().Id.ToString());
            string activePath = args[6];
            ulong watchStarted = NativeClock.NowMilliseconds;
            ulong lastCheck = 0;
            while (NativeWindows.IsSameProcess(pid, ticks) && !File.Exists(stop))
            {
                ulong now = NativeClock.NowMilliseconds;
                if (!String.IsNullOrEmpty(activePath) && now - lastCheck >= 2000)
                {
                    lastCheck = now;
                    ActiveSession state = null;
                    try { state = JsonFile.Read<ActiveSession>(activePath, null); } catch (IOException) { }
                    if (state != null && state.ParentPid == pid && state.ParentStartTicks == ticks && state.JournalPath == journal && (state.HeartbeatTick == 0 ? now - watchStarted > 30000 : now > state.HeartbeatTick && now - state.HeartbeatTick > 30000))
                    {
                        // Only this executable's exact parent instance can be stopped after its UI heartbeat fails.
                        string path = NativeWindows.PathForPid((uint)pid);
                        if (String.Equals(NativeWindows.NormalizedPath(path), NativeWindows.NormalizedPath(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase) && NativeWindows.IsSameProcess(pid, ticks))
                        {
                            using (Process parent = Process.GetProcessById(pid)) { parent.Kill(); parent.WaitForExit(2000); }
                            File.WriteAllText(journal + ".recovery-note.txt", "The AiTime UI heartbeat stopped for 30 seconds. Only this application's exact parent process was stopped; other applications were not terminated.");
                            break;
                        }
                    }
                }
                Thread.Sleep(250);
            }
            for (int i = 0; i < 3; i++)
            {
                try { GuardEngine.RestoreJournal(journal); return 0; }
                catch (IOException) { Thread.Sleep(250); }
            }
            return 3;
        }
    }
}