using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AiTime
{
    public sealed class AllowedWindow
    {
        public long Handle;
        public string Title;
        public string Path;
    }

    // A per-session, non-destructive window guard. It does not terminate processes,
    // change policies, install services, or manipulate the Windows secure desktop.
    public sealed class GuardEngine : IDisposable
    {
        private static readonly object ProcessGuardLock = new object();
        private static GuardEngine _processGuard;
        private readonly Form _owner;
        private readonly HashSet<string> _allowedPaths;
        private readonly HashSet<string> _inputComponentPaths;
        private readonly string _explorerPath;
        private readonly string _journalPath;
        private readonly string _sessionId;
        private readonly uint _ownPid;
        private readonly long _ownStartTime;
        private readonly Native.WinEventProc _eventProc;
        private readonly Native.HookProc _keyboardProc;
        private readonly Native.HookProc _mouseProc;
        private readonly MethodInvoker _reconcileInvoker;
        private readonly object _listLock = new object();
        private readonly Dictionary<long, HiddenWindowRecord> _hidden = new Dictionary<long, HiddenWindowRecord>();
        private readonly Dictionary<long, ulong> _lastHide = new Dictionary<long, ulong>();
        private readonly HashSet<string> _reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CoverageForm> _covers = new Dictionary<string, CoverageForm>(StringComparer.Ordinal);
        private volatile Dictionary<IntPtr, WindowAccess> _windowCache = new Dictionary<IntPtr, WindowAccess>();
        private List<AllowedWindow> _allowedWindows = new List<AllowedWindow>();
        private GuardJournal _journal;
        private System.Windows.Forms.Timer _timer;
        private IntPtr _foregroundHook;
        private IntPtr _showHook;
        private Thread _inputThread;
        private ManualResetEvent _inputReady;
        private Exception _inputError;
        private volatile uint _inputThreadId;
        private volatile bool _inputExit;
        private IntPtr _keyboardHook;
        private IntPtr _mouseHook;
        private readonly bool[] _suppressedKeys = new bool[256];
        private bool _leftWinDown;
        private bool _rightWinDown;
        private int _reconcilePosted;
        private int _blockedCount;
        private int _coveredCount;
        private bool _coverageRequired;
        private ActivationForm _activationPrompt;
        private bool _reconciling;
        private volatile bool _running;
        private volatile bool _stopping;
        private bool _disposed;
        private bool _stopComplete;
        private Mutex _userGuardMutex;
        private bool _userGuardHeld;
        // Trusted OS input-method folders plus the install folders of registered third-party IMEs.
        private readonly List<string> _inputComponentDirs = new List<string>();
        // Install folder of each allowed exe ("同目录组件": conda envs, IDE helpers ...).
        private readonly List<string> _familyRoots = new List<string>();
        private volatile Dictionary<uint, bool> _pidAccess = new Dictionary<uint, bool>();
        private readonly SortedDictionary<string, int> _blockedApps = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private ulong _lastBounce;
        private bool _altTabActive;
        private const uint InjectMarker = 0x41495449;
        private const uint InjectWinSpaceMessage = 0x8001;
        // Developer runtimes that stay usable when an allowed app (or AiTime itself) starts them.
        private static readonly string[] RuntimeNames = {
            "python.exe", "pythonw.exe", "py.exe", "pyw.exe", "conhost.exe", "openconsole.exe", "cmd.exe",
            "powershell.exe", "pwsh.exe", "gcc.exe", "g++.exe", "gdb.exe", "clang.exe", "clang++.exe",
            "java.exe", "javaw.exe", "git.exe", "bash.exe", "sh.exe", "r.exe", "rscript.exe", "rterm.exe", "rgui.exe" };

        public event Action<string> Notice;

        public int BlockedCount { get { return Interlocked.CompareExchange(ref _blockedCount, 0, 0); } }
        public int CoveredCount { get { return Interlocked.CompareExchange(ref _coveredCount, 0, 0); } }
        public bool HasCoveredWindows { get { return CoveredCount > 0; } }
        public bool IsRunning { get { return _running && !_stopping; } }
        /// <summary>Exe names whose windows were hidden in this session (the processes keep running).</summary>
        public string[] BlockedApps
        {
            get { lock (_listLock) { string[] names = new string[_blockedApps.Count]; _blockedApps.Keys.CopyTo(names, 0); return names; } }
        }

        public GuardEngine(Form owner, IEnumerable<string> allowedPaths, string journalPath, string sessionId)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            if (allowedPaths == null) throw new ArgumentNullException("allowedPaths");
            if (String.IsNullOrWhiteSpace(journalPath)) throw new ArgumentException("必须指定恢复记录路径。", "journalPath");
            if (String.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("必须指定本轮会话标识。", "sessionId");
            _owner = owner;
            _journalPath = System.IO.Path.GetFullPath(journalPath);
            _sessionId = sessionId;
            _allowedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in allowedPaths)
            {
                string normalized = NormalizePath(path);
                if (normalized.Length == 0 || !System.IO.Path.IsPathRooted(normalized) ||
                    !String.Equals(System.IO.Path.GetExtension(normalized), ".exe", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("白名单必须使用 exe 的完整路径。", "allowedPaths");
                _allowedPaths.Add(normalized);
            }
            _ownPid = Native.GetCurrentProcessId();
            ProcessIdentity identity;
            if (!ReadIdentity(_ownPid, out identity)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取守卫进程身份。");
            _ownStartTime = identity.StartTime;
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            _explorerPath = NormalizePath(System.IO.Path.Combine(windows, "explorer.exe"));
            _inputComponentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Exact trusted OS locations only. No basename, publisher, or directory wildcard allow rule.
            AddInputPath(windows, @"System32\ctfmon.exe");
            AddInputPath(windows, @"SysWOW64\ctfmon.exe");
            AddInputPath(windows, @"System32\InputHost.exe");
            AddInputPath(windows, @"System32\IME\SHARED\imebroker.exe");
            AddInputPath(windows, @"SysWOW64\IME\SHARED\imebroker.exe");
            AddInputPath(windows, @"SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\TextInputHost.exe");
            AddInputPath(windows, @"SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\InputApp\TextInputHost.exe");
            AddInputPath(windows, @"SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\InputApp\InputHost.exe");
            AddInputPath(windows, @"SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\InputHost.exe");
            AddInputPath(windows, @"SystemApps\MicrosoftWindows.Client.Core_cw5n1h2txyewy\TextInputHost.exe");
            string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
            if (!String.IsNullOrEmpty(common)) AddInputPath(common, @"microsoft shared\ink\TabTip.exe");
            string common32 = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86);
            if (!String.IsNullOrEmpty(common32)) AddInputPath(common32, @"microsoft shared\ink\TabTip32.exe");
            // Microsoft Pinyin / Japanese / Korean IMEs run as e.g. System32\InputMethod\CHS\ChsIME.exe.
            AddInputDir(System.IO.Path.Combine(windows, @"System32\InputMethod"));
            AddInputDir(System.IO.Path.Combine(windows, @"SysWOW64\InputMethod"));
            AddInputDir(System.IO.Path.Combine(windows, @"System32\IME"));
            AddInputDir(System.IO.Path.Combine(windows, @"SysWOW64\IME"));
            foreach (string dir in ThirdPartyImeDirs(windows)) AddInputDir(dir);
            foreach (string path in _allowedPaths)
            {
                string root = FamilyRoot(path, windows);
                if (root != null && !_familyRoots.Exists(r => String.Equals(r, root, StringComparison.OrdinalIgnoreCase))) _familyRoots.Add(root);
            }
            _eventProc = OnWinEvent;
            _keyboardProc = OnKeyboard;
            _mouseProc = OnMouse;
            _reconcileInvoker = delegate
            {
                Interlocked.Exchange(ref _reconcilePosted, 0);
                if (!_running || _stopping) return;
                try { Reconcile(false); }
                catch (Exception ex) { FailSafe(ex); }
            };
        }

        private void AddInputPath(string root, string relative)
        {
            _inputComponentPaths.Add(NormalizePath(System.IO.Path.Combine(root, relative)));
        }

        private void AddInputDir(string dir)
        {
            string normalized = NormalizePath(dir);
            if (normalized.Length > 0 && !_inputComponentDirs.Exists(d => String.Equals(d, normalized, StringComparison.OrdinalIgnoreCase)))
                _inputComponentDirs.Add(normalized);
        }

        private static bool UnderDir(string path, string dir)
        {
            return path.Length > dir.Length + 1 && path.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBroadFolder(string dir, string windows)
        {
            if (String.IsNullOrEmpty(dir)) return true;
            string root = System.IO.Path.GetPathRoot(dir) ?? "";
            if (String.Equals(dir.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return true;
            if (String.Equals(dir, windows, StringComparison.OrdinalIgnoreCase) || UnderDir(dir, windows)) return true;
            Environment.SpecialFolder[] broad = {
                Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86,
                Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments };
            foreach (Environment.SpecialFolder folder in broad)
            {
                string value = NormalizePath(Environment.GetFolderPath(folder));
                if (value.Length > 0 && String.Equals(dir, value, StringComparison.OrdinalIgnoreCase)) return true;
            }
            string programs = NormalizePath(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"));
            return String.Equals(dir, programs, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Install folder of an allowed exe (parent of bin/Scripts). Null for broad or system folders.</summary>
        private static string FamilyRoot(string exe, string windows)
        {
            string dir = System.IO.Path.GetDirectoryName(exe);
            if (String.IsNullOrEmpty(dir)) return null;
            string name = System.IO.Path.GetFileName(dir);
            if (String.Equals(name, "bin", StringComparison.OrdinalIgnoreCase) || String.Equals(name, "Scripts", StringComparison.OrdinalIgnoreCase))
                dir = System.IO.Path.GetDirectoryName(dir);
            dir = NormalizePath(dir ?? "");
            return IsBroadFolder(dir, windows) ? null : dir;
        }

        /// <summary>Folders of third-party text services (Sogou, QQ, WeType ...) registered with Windows.</summary>
        private static List<string> ThirdPartyImeDirs(string windows)
        {
            List<string> result = new List<string>();
            try
            {
                using (RegistryKey tips = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\CTF\TIP"))
                {
                    if (tips == null) return result;
                    foreach (string clsid in tips.GetSubKeyNames())
                    {
                        string dll = null;
                        foreach (string view in new[] { @"CLSID\" + clsid + @"\InprocServer32", @"WOW6432Node\CLSID\" + clsid + @"\InprocServer32" })
                        {
                            using (RegistryKey key = Registry.ClassesRoot.OpenSubKey(view))
                                if (key != null) dll = key.GetValue(null) as string;
                            if (!String.IsNullOrWhiteSpace(dll)) break;
                        }
                        if (String.IsNullOrWhiteSpace(dll)) continue;
                        string dir = NormalizePath(System.IO.Path.GetDirectoryName(Environment.ExpandEnvironmentVariables(dll.Trim().Trim('"'))) ?? "");
                        if (dir.Length == 0 || IsBroadFolder(dir, windows)) continue;
                        result.Add(dir);
                        string parent = NormalizePath(System.IO.Path.GetDirectoryName(dir) ?? "");
                        if (parent.Length > 0 && !IsBroadFolder(parent, windows)) result.Add(parent);
                    }
                }
            }
            catch (Exception) { /* IME discovery is best effort; built-in IMEs are covered above. */ }
            return result;
        }

        private bool IsInputComponent(string path)
        {
            if (_inputComponentPaths.Contains(path)) return true;
            foreach (string dir in _inputComponentDirs) if (UnderDir(path, dir)) return true;
            return false;
        }

        private bool IsFamily(string path)
        {
            foreach (string root in _familyRoots) if (UnderDir(path, root)) return true;
            return false;
        }

        /// <summary>Allowed exe, a component of its install folder, or a developer runtime started by one of them (or by AiTime).</summary>
        private bool IsPermittedProcess(uint pid, ProcessIdentity identity, Dictionary<uint, ProcessIdentity> processes, ref Dictionary<uint, uint> parents)
        {
            if (_allowedPaths.Contains(identity.Path) || IsFamily(identity.Path)) return true;
            string name = System.IO.Path.GetFileName(identity.Path).ToLowerInvariant();
            if (Array.IndexOf(RuntimeNames, name) < 0) return false;
            if (parents == null) parents = ReadParents();
            uint current = pid;
            long childStart = identity.StartTime;
            for (int depth = 0; depth < 6; depth++)
            {
                uint parent;
                if (!parents.TryGetValue(current, out parent) || parent == 0 || parent == current) return false;
                if (parent == _ownPid) return _ownStartTime <= childStart;
                ProcessIdentity info;
                if (!processes.TryGetValue(parent, out info)) { ReadIdentity(parent, out info); processes[parent] = info; }
                if (info == null || info.StartTime > childStart) return false; // PID reuse: a "parent" newer than its child is not the parent.
                if (_allowedPaths.Contains(info.Path) || IsFamily(info.Path)) return true;
                current = parent;
                childStart = info.StartTime;
            }
            return false;
        }

        // Self-test entry points for the allow rules (no hooks, no window changes).
        internal bool InputComponentForTest(string path) { return IsInputComponent(NormalizePath(path)); }
        internal bool FamilyForTest(string path) { return IsFamily(NormalizePath(path)); }
        internal bool ProcessPermittedForTest(uint pid)
        {
            ProcessIdentity identity;
            if (!ReadIdentity(pid, out identity)) return false;
            Dictionary<uint, uint> parents = null;
            return IsInputComponent(identity.Path) || IsPermittedProcess(pid, identity, new Dictionary<uint, ProcessIdentity>(), ref parents);
        }

        private static Dictionary<uint, uint> ReadParents()
        {
            Dictionary<uint, uint> map = new Dictionary<uint, uint>();
            IntPtr snapshot = Native.CreateToolhelp32Snapshot(0x2, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return map;
            try
            {
                Native.PROCESSENTRY32 entry = new Native.PROCESSENTRY32();
                entry.dwSize = (uint)Marshal.SizeOf(typeof(Native.PROCESSENTRY32));
                if (Native.Process32First(snapshot, ref entry))
                    do { map[entry.th32ProcessID] = entry.th32ParentProcessID; }
                    while (Native.Process32Next(snapshot, ref entry));
            }
            finally { Native.CloseHandle(snapshot); }
            return map;
        }

        public void Start()
        {
            if (_disposed) throw new ObjectDisposedException("GuardEngine");
            if (_running) return;
            if (_owner.IsDisposed || _owner.Disposing) throw new InvalidOperationException("专注窗口已关闭。");
            if (_owner.InvokeRequired) throw new InvalidOperationException("请在专注窗口的 UI 线程启动守卫。");
            IntPtr ownerHandle = _owner.Handle; // Create the UI handle before installing out-of-context WinEvents.
            if (ownerHandle == IntPtr.Zero) throw new InvalidOperationException("专注窗口未创建。");
            AcquireUserGuard();
            _stopComplete = false;
            try
            {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_journalPath));
            using (JournalLock journalLock = new JournalLock(_journalPath))
            {
                if (File.Exists(_journalPath))
                {
                    GuardJournal previous = ReadJournal(_journalPath);
                    if (previous.Active && IsSameProcess(previous.GuardProcessId, previous.GuardProcessStartTime))
                        throw new InvalidOperationException("这份恢复记录仍有正在运行的守卫，请先结束上一轮。");
                    RecoverEntries(previous);
                    previous.Active = false;
                    previous.RecoveryNeeded = HasPendingEntries(previous);
                    WriteJournalUnlocked(_journalPath, previous);
                    if (previous.RecoveryNeeded) throw new InvalidOperationException("上一轮仍有窗口尚未恢复，不能覆盖恢复记录。");
                }
                _journal = new GuardJournal();
                _journal.Version = 1;
                _journal.SessionId = _sessionId;
                _journal.PropertyName = PropertyName(_sessionId);
                _journal.PropertyValue = 0x4149544D;
                _journal.GuardProcessId = _ownPid;
                _journal.GuardProcessStartTime = _ownStartTime;
                _journal.Active = true;
                _journal.CreatedUtc = DateTime.UtcNow.ToString("o");
                _journal.Entries = new List<HiddenWindowRecord>();
                WriteJournalUnlocked(_journalPath, _journal);
            }
            _hidden.Clear();
            _lastHide.Clear();
            _reported.Clear();
            _blockedCount = 0;
            _coveredCount = 0;
            _coverageRequired = false;
            _inputError = null;
            _inputExit = false;
            _stopping = false;
            _running = true;
                StartInputThread();
                _foregroundHook = Native.SetWinEventHook(3, 3, IntPtr.Zero, _eventProc, 0, 0, 2);
                _showHook = Native.SetWinEventHook(0x8002, 0x8002, IntPtr.Zero, _eventProc, 0, 0, 2);
                if (_foregroundHook == IntPtr.Zero || _showHook == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法安装窗口变化监测。");
                Reconcile(true);
                if (!_running || _stopping || _disposed) throw new OperationCanceledException("准备已取消，本轮未开始。");
                _timer = new System.Windows.Forms.Timer();
                _timer.Interval = 600; // WinEvents trigger immediate checks; the timer is only a safety net.
                _timer.Tick += OnTimer;
                _timer.Start();
            }
            catch
            {
                Stop();
                throw;
            }
        }

        public void Stop()
        {
            if (_owner.IsHandleCreated && !_owner.IsDisposed && _owner.InvokeRequired)
            {
                _owner.Invoke(new MethodInvoker(Stop));
                return;
            }
            if (_stopping) return;
            if (_stopComplete && !_userGuardHeld) return;
            _stopping = true;
            _running = false; // Lingering input callbacks become pass-through immediately.
            try
            {
                ActivationForm prompt = _activationPrompt;
                _activationPrompt = null;
                if (prompt != null && !prompt.IsDisposed) prompt.CancelFromGuard();
                if (_timer != null)
                {
                    _timer.Stop();
                    _timer.Tick -= OnTimer;
                    _timer.Dispose();
                    _timer = null;
                }
                if (_foregroundHook != IntPtr.Zero) { Native.UnhookWinEvent(_foregroundHook); _foregroundHook = IntPtr.Zero; }
                if (_showHook != IntPtr.Zero) { Native.UnhookWinEvent(_showHook); _showHook = IntPtr.Zero; }
                StopInputThread();
                DisposeCoverage();
                if (_journal != null)
                {
                    // Recover from the in-memory snapshot even when the most recent disk write failed.
                    RecoverEntries(_journal);
                    _journal.Active = false;
                    _journal.RecoveryNeeded = HasPendingEntries(_journal);
                    _journal.EndedUtc = DateTime.UtcNow.ToString("o");
                    try { PersistJournal(); }
                    catch (Exception ex) { RaiseNotice("窗口已执行恢复，但恢复记录写入失败：" + ex.Message); }
                    if (_journal.RecoveryNeeded) RaiseNotice("部分窗口尚未恢复，已保留恢复记录，可用恢复入口重试。");
                }
            }
            finally
            {
                _windowCache = new Dictionary<IntPtr, WindowAccess>();
                DisposeCoverage();
                _coveredCount = 0;
                lock (_listLock) _allowedWindows = new List<AllowedWindow>();
                ReleaseUserGuard();
                _stopComplete = true;
                _stopping = false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            Stop();
            _disposed = true;
        }

        // The watchdog may call this after the owner process exits. A live matching
        // owner is never unlocked by this public recovery entry point.
        public static void RestoreJournal(string journalPath)
        {
            string path = System.IO.Path.GetFullPath(journalPath);
            if (!File.Exists(path)) return;
            using (JournalLock journalLock = new JournalLock(path))
            {
                if (!File.Exists(path)) return;
                GuardJournal journal = ReadJournal(path);
                if (journal.Active && IsSameProcess(journal.GuardProcessId, journal.GuardProcessStartTime)) return;
                RecoverEntries(journal);
                journal.Active = false;
                journal.RecoveryNeeded = HasPendingEntries(journal);
                journal.EndedUtc = DateTime.UtcNow.ToString("o");
                WriteJournalUnlocked(path, journal);
                if (journal.RecoveryNeeded) throw new IOException("部分窗口恢复失败；原恢复记录已保留，可再次恢复。");
            }
        }

        public IList<AllowedWindow> GetAllowedWindows()
        {
            lock (_listLock)
            {
                List<AllowedWindow> result = new List<AllowedWindow>();
                foreach (AllowedWindow window in _allowedWindows)
                    result.Add(new AllowedWindow { Handle = window.Handle, Title = window.Title, Path = window.Path });
                return result;
            }
        }

        public bool ActivateWindow(long handle)
        {
            if (!_running || _stopping) return false;
            if (_owner.InvokeRequired)
                return (bool)_owner.Invoke(new Func<long, bool>(ActivateWindow), new object[] { handle });
            IntPtr window = new IntPtr(handle);
            if (!Native.IsWindow(window)) return false;
            uint pid;
            Native.GetWindowThreadProcessId(window, out pid);
            ProcessIdentity identity;
            if (pid != _ownPid && (!ReadIdentity(pid, out identity) || !_allowedPaths.Contains(identity.Path))) return false;
            if (Native.IsIconic(window)) Native.ShowWindowAsync(window, 9);
            else Native.ShowWindowAsync(window, 5);

            // Do not alter the target application's persistent TOPMOST state.
            // Foreground it first, then rebuild only the coverage holes around the
            // now-current Z order. This avoids a full-screen Z-order promotion.
            bool foreground = Native.SetForegroundWindow(window);
            if (!foreground) return false;
            Reconcile(false);
            return Native.GetForegroundWindow() == window;
        }

        private void OnTimer(object sender, EventArgs e)
        {
            if (!_running || _stopping) return;
            try
            {
                if (_inputThread == null || !_inputThread.IsAlive)
                    throw new InvalidOperationException("输入守卫线程已停止。");
                Reconcile(false);
            }
            catch (Exception ex) { FailSafe(ex); }
        }        private void FailSafe(Exception ex)
        {
            Stop();
            RaiseNotice("窗口守卫发生异常，已释放输入并恢复窗口：" + ex.Message);
        }

        private void RaiseNotice(string message)
        {
            Action<string> handler = Notice;
            if (handler == null) return;
            try
            {
                if (_owner.IsHandleCreated && !_owner.IsDisposed && _owner.InvokeRequired)
                    _owner.BeginInvoke(new Action(delegate { try { handler(message); } catch { } }));
                else handler(message);
            }
            catch { /* A subscriber or a closing form must not break recovery. */ }
        }

        private void ReportOnce(string key, string message)
        {
            if (_reported.Add(key)) RaiseNotice(message);
        }

        private void QueueReconcile()
        {
            if (!_running || _stopping || Interlocked.Exchange(ref _reconcilePosted, 1) != 0) return;
            try { _owner.BeginInvoke(_reconcileInvoker); }
            catch (InvalidOperationException) { Interlocked.Exchange(ref _reconcilePosted, 0); }
        }

        private void OnWinEvent(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint threadId, uint time)
        {
            // No path lookup, filesystem access, restoration, or synchronous UI work in callbacks.
            if (window != IntPtr.Zero && (eventType == 3 || objectId == 0)) QueueReconcile();
        }

        private void Reconcile(bool initial)
        {
            if (!_running || _stopping || _reconciling) return;
            _reconciling = true;
            try
            {
                List<IntPtr> windows = new List<IntPtr>();
                Native.EnumWindows(delegate(IntPtr window, IntPtr state) { windows.Add(window); return true; }, IntPtr.Zero);
                Dictionary<IntPtr, WindowAccess> cache = new Dictionary<IntPtr, WindowAccess>();
                Dictionary<uint, ProcessIdentity> processes = new Dictionary<uint, ProcessIdentity>();
                Dictionary<uint, bool> pidAccess = new Dictionary<uint, bool>();
                Dictionary<uint, uint> parents = null;
                List<HideRequest> pending = new List<HideRequest>();
                List<AllowedWindow> allowed = new List<AllowedWindow>();
                int coveredCount = 0;
                int unsafeWindowCount = 0;
                foreach (IntPtr window in windows)
                {
                    if (!Native.IsWindowVisible(window) || IsCloaked(window)) continue;
                    uint pid;
                    Native.GetWindowThreadProcessId(window, out pid);
                    if (pid == _ownPid) { cache[window] = new WindowAccess(pid, true); continue; }
                    ProcessIdentity identity;
                    if (!processes.TryGetValue(pid, out identity))
                    {
                        ReadIdentity(pid, out identity);
                        processes[pid] = identity;
                    }
                    string windowClass = ReadClass(window);
                    bool shellSurface = identity != null && String.Equals(identity.Path, _explorerPath, StringComparison.OrdinalIgnoreCase) &&
                        (windowClass == "Shell_TrayWnd" || windowClass == "Shell_SecondaryTrayWnd" || windowClass == "Progman" || windowClass == "WorkerW");
                    bool systemInput = identity != null && IsInputComponent(identity.Path);
                    bool processAllowed;
                    if (!pidAccess.TryGetValue(pid, out processAllowed))
                    {
                        processAllowed = identity != null && (systemInput || IsPermittedProcess(pid, identity, processes, ref parents));
                        pidAccess[pid] = processAllowed;
                    }
                    bool userAllowed = processAllowed && !systemInput;
                    bool isAllowed = !shellSurface && processAllowed;
                    cache[window] = new WindowAccess(pid, isAllowed);
                    if (isAllowed)
                    {
                        if (userAllowed && !systemInput && (Native.GetWindowLong(window, -20) & 0x80) == 0)
                        {
                            string title = ReadTitle(window);
                            if (!String.IsNullOrWhiteSpace(title)) allowed.Add(new AllowedWindow { Handle = window.ToInt64(), Title = title, Path = identity.Path });
                        }
                        continue;
                    }
                    if (identity == null)
                    {
                        unsafeWindowCount++;
                        coveredCount++;
                        ReportOnce("identity:" + pid, "检测到无法读取身份的可见窗口；本轮不会强行覆盖它。");
                        continue;
                    }
                    // Untitled render/host surfaces can show and hide themselves as an implementation detail.
                    // Cover them without changing their window state or adding recovery obligations.
                    int extendedStyle = Native.GetWindowLong(window, -20);
                    // Terminal windows (Windows Terminal) have titles and are hidden like any other app window.
                    bool auxiliary = !shellSurface && (String.IsNullOrWhiteSpace(ReadTitle(window)) ||
                        (extendedStyle & (0x08000000 | 0x80)) == (0x08000000 | 0x80));
                    if (auxiliary) continue;
                    pending.Add(new HideRequest { Window = window, Pid = pid, Identity = identity, WindowClass = windowClass });
                }
                // Register every new window first, write the recovery journal ONCE, then hide.
                // (Previously one disk write per window made the start of a session stutter.)
                bool journalChanged = false;
                List<HideRequest> ready = new List<HideRequest>();
                foreach (HideRequest request in pending)
                {
                    string failure;
                    int state = PrepareHide(request, out failure);
                    if (state < 0)
                    {
                        unsafeWindowCount++;
                        coveredCount++;
                        ReportOnce("cover:" + request.Pid + ":" + request.WindowClass,
                            System.IO.Path.GetFileName(request.Identity.Path) + " 无法安全隐藏；本轮不会使用全屏覆盖兜底。");
                        continue;
                    }
                    if (state == 0) continue;
                    if (state == 2) journalChanged = true;
                    ready.Add(request);
                }
                if (journalChanged) PersistJournal(); // Durable intent BEFORE hiding; a failed write triggers in-memory recovery.
                foreach (HideRequest request in ready) ApplyHide(request);
                _windowCache = cache; // Immutable snapshots: hooks never see a dictionary being mutated.
                _pidAccess = pidAccess;
                Interlocked.Exchange(ref _coveredCount, coveredCount);
                if (unsafeWindowCount > 0)
                    throw new InvalidOperationException("检测到 " + unsafeWindowCount + " 个真正无法安全隐藏的高权限或独占窗口。为避免黑屏，本轮未启动；关闭这些窗口后即可开始。");
                allowed.Sort(delegate(AllowedWindow a, AllowedWindow b) { return StringComparer.CurrentCultureIgnoreCase.Compare(a.Title, b.Title); });
                lock (_listLock) _allowedWindows = allowed;
                if (_coverageRequired) UpdateCoverage(initial);
                if (!initial) ReturnFromDeniedForeground(cache);
            }
            finally { _reconciling = false; }
        }

        private void UpdateCoverage(bool initial)
        {
            // These windows are ours. No operation in this method targets a protected game's window.
            EnsureCoverageForms();
            if (!IsInputAllowed(Native.GetForegroundWindow())) FocusOwnerForCoverage();
            List<string> attempts = new List<string>();
            ulong deadline = Native.GetTickCount64() + (initial ? 400UL : 200UL);
            bool usableSurface = false;
            bool foregroundAllowed = false;
            bool coverageVerified = false;
            int attempt = 0;
            do
            {
                if (!_running || _stopping || _disposed) throw new OperationCanceledException("准备已取消，本轮未开始。");
                usableSurface = BuildCoverageRegions();
                foreach (CoverageForm cover in _covers.Values)
                {
                    cover.TopMost = false;
                    Native.SetWindowPos(cover.Handle, new IntPtr(-2), 0, 0, 0, 0,
                        0x1 | 0x2 | 0x10 | 0x40);
                    cover.Update();
                }
                try { Native.DwmFlush(); } catch (EntryPointNotFoundException) { }
                IntPtr foreground = Native.GetForegroundWindow();
                foregroundAllowed = IsInputAllowed(foreground);
                string coverageDiagnostic;
                coverageVerified = VerifyCoverage(out coverageDiagnostic);
                if (usableSurface && foregroundAllowed && coverageVerified)
                {
                    ReportOnce("coverage-ready", "已启用兼容覆盖守卫：高权限窗口保留后台运行，当前仅允许本轮应用操作。");
                    return;
                }
                attempts.Add("attempt=" + (attempt + 1) + ", usableSurface=" + usableSurface +
                    ", foregroundAllowed=" + foregroundAllowed + ", foreground={" + DescribeWindow(foreground) +
                    "}, owner={" + DescribeWindow(_owner.Handle) + "}, coverage={" + coverageDiagnostic + "}");
                if (attempts.Count > 3) attempts.RemoveAt(1); // Retain first and most recent diagnostics.
                attempt++;
                if (Native.GetTickCount64() >= deadline) break;
                FocusOwnerForCoverage();
                // Foreground transitions across input queues are asynchronous. This bounded delay
                // leaves the input-hook thread responsive and never treats DwmFlush as a global fence.
                Thread.Sleep(35);
            }
            while (attempt < 12);
            if (initial && usableSurface && coverageVerified && !foregroundAllowed)
            {
                RequestForegroundActivation();
                if (!_running || _stopping || _disposed) throw new OperationCanceledException("准备已取消，本轮未开始。");
                FocusOwnerForCoverage();
                UpdateCoverage(false); // One genuine click, then the full unchanged verification; no second prompt.
                return;
            }
            throw new InvalidOperationException("当前高权限或独占窗口无法被可靠覆盖；本轮已停止并恢复窗口，未关闭后台程序。\n" +
                String.Join("\n", attempts.ToArray()) + "\nvisible-z-order=" + DescribeZOrder());
        }

        private void RequestForegroundActivation()
        {
            if (!_running || _stopping || _disposed) throw new OperationCanceledException("准备已取消，本轮未开始。");
            RaiseNotice("准备就绪，点击准备窗口开始本轮专注；现在尚未计时。");
            using (ActivationForm prompt = new ActivationForm())
            {
                _activationPrompt = prompt;
                try
                {
                    DialogResult result = prompt.ShowDialog(_owner);
                    if (result != DialogResult.OK || !_running || _stopping || _disposed)
                        throw new OperationCanceledException("准备已取消或超时，本轮未开始。");
                }
                finally
                {
                    if (Object.ReferenceEquals(_activationPrompt, prompt)) _activationPrompt = null;
                }
            }
        }

        private void EnsureCoverageForms()
        {
            HashSet<string> present = new HashSet<string>(StringComparer.Ordinal);
            foreach (Screen screen in Screen.AllScreens)
            {
                present.Add(screen.DeviceName);
                CoverageForm cover;
                if (!_covers.TryGetValue(screen.DeviceName, out cover))
                {
                    cover = new CoverageForm(delegate { if (_running && !_stopping) { FocusOwnerForCoverage(); QueueReconcile(); } });
                    cover.Bounds = screen.Bounds;
                    Region empty = new Region(); empty.MakeEmpty(); cover.ReplaceRegion(empty);
                    _covers.Add(screen.DeviceName, cover);
                    // Keep the coverage windows independent. Downgrading the owner's TopMost state
                    // must not silently downgrade all its owned windows as well.
                    cover.Show();
                }
                else if (cover.Bounds != screen.Bounds) cover.Bounds = screen.Bounds;
            }
            List<string> obsolete = new List<string>();
            foreach (string name in _covers.Keys) if (!present.Contains(name)) obsolete.Add(name);
            foreach (string name in obsolete) { _covers[name].Release(); _covers.Remove(name); }
        }

        private void FocusOwnerForCoverage()
        {
            if (_owner.IsDisposed || !_owner.IsHandleCreated) return;
            bool topMost = _owner.TopMost;
            try
            {
                if (!_owner.Visible) _owner.Show();
                if (_owner.WindowState == FormWindowState.Minimized) _owner.WindowState = FormWindowState.Normal;
                _owner.TopMost = true;
                Native.SetWindowPos(_owner.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x40);
                _owner.BringToFront();
                _owner.Activate();
                Native.SetForegroundWindow(_owner.Handle);
            }
            finally
            {
                _owner.TopMost = topMost;
                Native.SetWindowPos(_owner.Handle, new IntPtr(topMost ? -1 : -2), 0, 0, 0, 0, 0x1 | 0x2 | 0x10);
            }
        }

        private bool BuildCoverageRegions()
        {
            List<IntPtr> ordered = WindowsInZOrder();
            HashSet<IntPtr> coverHandles = new HashSet<IntPtr>();
            foreach (CoverageForm cover in _covers.Values) coverHandles.Add(cover.Handle);
            HashSet<long> launchable = new HashSet<long>();
            lock (_listLock) foreach (AllowedWindow window in _allowedWindows) launchable.Add(window.Handle);
            bool usableSurface = false;
            foreach (CoverageForm cover in _covers.Values)
            {
                Rectangle screen = cover.Bounds;
                using (Region remainder = new Region(screen))
                using (Region permitted = new Region())
                {
                    permitted.MakeEmpty();
                    foreach (IntPtr window in ordered)
                    {
                        // Our input permission is process-wide; our full-screen cover is NEVER a hole.
                        if (coverHandles.Contains(window) || !Native.IsWindowVisible(window) || Native.IsIconic(window) || IsCloaked(window)) continue;
                        Native.RECT rect;
                        if (!Native.GetWindowRect(window, out rect)) continue;
                        Rectangle bounds = Rectangle.FromLTRB(rect.left, rect.top, rect.right, rect.bottom);
                        if (bounds.Width <= 0 || bounds.Height <= 0 || !bounds.IntersectsWith(screen) || IsFullyTransparent(window)) continue;
                        using (Region shape = ReadWindowShape(window, bounds))
                        using (Region exposed = shape.Clone())
                        {
                            exposed.Intersect(remainder);
                            if (IsInputAllowed(window))
                            {
                                permitted.Union(exposed);
                                if ((window == _owner.Handle || launchable.Contains(window.ToInt64())) && HasUsefulArea(exposed)) usableSurface = true;
                            }
                            // Top-to-bottom occlusion: denied surfaces subtract before a lower allowed
                            // window can create a hole. This also prevents an allowed app behind a game
                            // from exposing the game through that app's bounding rectangle.
                            remainder.Exclude(shape);
                        }
                    }
                    Region blocked = new Region(screen);
                    blocked.Exclude(permitted);
                    blocked.Translate(-screen.Left, -screen.Top);
                    cover.ReplaceRegion(blocked);
                }
            }
            return usableSurface;
        }

        private bool VerifyCoverage(out string diagnostic)
        {
            diagnostic = "verified";
            foreach (CoverageForm cover in _covers.Values)
            {
                if (!cover.Visible || !Native.IsWindow(cover.Handle) || (Native.GetWindowLong(cover.Handle, -20) & 0x8) == 0)
                { diagnostic = "cover-not-visible-or-topmost: " + DescribeWindow(cover.Handle) + ", formVisible=" + cover.Visible; return false; }
                Region region = cover.Region;
                if (region == null) { diagnostic = "null-region: " + DescribeWindow(cover.Handle); return false; }
                using (Matrix matrix = new Matrix())
                {
                    RectangleF[] pieces = region.GetRegionScans(matrix);
                    if (pieces.Length > 4096) { diagnostic = "region-fragment-count=" + pieces.Length; return false; }
                    foreach (RectangleF piece in pieces)
                    {
                        if (piece.Width < 1 || piece.Height < 1) continue;
                        int x = cover.Left + (int)Math.Floor(piece.X + piece.Width / 2);
                        int y = cover.Top + (int)Math.Floor(piece.Y + piece.Height / 2);
                        IntPtr hit = Native.WindowFromPoint(new Native.POINT(x, y));
                        if (!IsInputAllowed(hit))
                        { diagnostic = "covered-point-hit-denied at " + x + "," + y + "; hit={" + DescribeWindow(hit) + "}; cover={" + DescribeWindow(cover.Handle) + "}; piece=" + piece; return false; }
                    }
                }
            }
            return true;
        }

        private static string DescribeWindow(IntPtr window)
        {
            if (window == IntPtr.Zero) return "HWND=0";
            uint pid;
            Native.GetWindowThreadProcessId(window, out pid);
            Native.RECT rect;
            Native.GetWindowRect(window, out rect);
            return "HWND=" + window.ToInt64() + ",pid=" + pid + ",class=" + ReadClass(window) +
                ",visible=" + Native.IsWindowVisible(window) + ",iconic=" + Native.IsIconic(window) +
                ",ex=0x" + Native.GetWindowLong(window, -20).ToString("x") +
                ",rect=" + rect.left + "," + rect.top + "," + rect.right + "," + rect.bottom;
        }

        private string DescribeZOrder()
        {
            List<string> descriptions = new List<string>();
            foreach (IntPtr window in WindowsInZOrder())
            {
                if (!Native.IsWindowVisible(window) || Native.IsIconic(window) || IsCloaked(window)) continue;
                descriptions.Add("{" + DescribeWindow(window) + "}");
                if (descriptions.Count == 12) break;
            }
            return String.Join(" | ", descriptions.ToArray());
        }

        private static bool HasUsefulArea(Region region)
        {
            using (Matrix matrix = new Matrix())
            {
                double area = 0;
                foreach (RectangleF rect in region.GetRegionScans(matrix))
                {
                    area += rect.Width * rect.Height;
                    if (area >= 4000) return true;
                }
                return false;
            }
        }

        private static bool IsFullyTransparent(IntPtr window)
        {
            if ((Native.GetWindowLong(window, -20) & 0x80000) == 0) return false;
            uint color, flags; byte alpha;
            return Native.GetLayeredWindowAttributes(window, out color, out alpha, out flags) && (flags & 2) != 0 && alpha == 0;
        }

        private static Region ReadWindowShape(IntPtr window, Rectangle bounds)
        {
            IntPtr native = Native.CreateRectRgn(0, 0, 0, 0);
            try
            {
                if (native != IntPtr.Zero && Native.GetWindowRgn(window, native) != 0)
                {
                    Region shape = Region.FromHrgn(native);
                    shape.Translate(bounds.Left, bounds.Top);
                    return shape;
                }
            }
            finally { if (native != IntPtr.Zero) Native.DeleteObject(native); }
            // DWM's extended frame avoids counting the invisible resize/shadow border as permitted UI.
            Native.RECT frame;
            if (Native.DwmGetWindowFrame(window, 9, out frame, Marshal.SizeOf(typeof(Native.RECT))) == 0 &&
                frame.right > frame.left && frame.bottom > frame.top)
                return new Region(Rectangle.FromLTRB(frame.left, frame.top, frame.right, frame.bottom));
            return new Region(bounds);
        }

        private static List<IntPtr> WindowsInZOrder()
        {
            List<IntPtr> result = new List<IntPtr>();
            HashSet<IntPtr> seen = new HashSet<IntPtr>();
            IntPtr window = Native.GetTopWindow(IntPtr.Zero);
            while (window != IntPtr.Zero && result.Count < 8192 && seen.Add(window))
            {
                result.Add(window);
                window = Native.GetWindow(window, 2); // GW_HWNDNEXT, explicit Z order.
            }
            return result;
        }

        private void DisposeCoverage()
        {
            foreach (CoverageForm cover in _covers.Values)
            {
                try { cover.Release(); } catch { }
            }
            _covers.Clear();
            _coverageRequired = false;
        }

        private sealed class HideRequest
        {
            public IntPtr Window;
            public uint Pid;
            public ProcessIdentity Identity;
            public string WindowClass;
            public bool Created;
        }

        /// <summary>-1 = cannot hide safely, 0 = window vanished, 1 = known window, 2 = newly registered (journal must be written).</summary>
        private int PrepareHide(HideRequest request, out string failure)
        {
            failure = null;
            IntPtr window = request.Window;
            uint pid = request.Pid;
            ProcessIdentity identity = request.Identity;
            long key = window.ToInt64();
            HiddenWindowRecord record;
            if (_hidden.TryGetValue(key, out record) &&
                (record.ProcessId != pid || record.ProcessStartTime != identity.StartTime ||
                 Native.GetProp(window, _journal.PropertyName).ToInt64() != _journal.PropertyValue))
            {
                // A destroyed HWND can be reused even within the same process.
                record.Restored = true;
                _hidden.Remove(key);
                _lastHide.Remove(key);
                record = null;
            }
            if (record != null) return 1;
            Native.WINDOWPLACEMENT placement = new Native.WINDOWPLACEMENT();
            placement.length = Marshal.SizeOf(typeof(Native.WINDOWPLACEMENT));
            if (!Native.GetWindowPlacement(window, ref placement))
            {
                Native.RECT rect;
                if (!Native.GetWindowRect(window, out rect)) return 0; // Window disappeared during enumeration.
                placement.normal = rect;
                placement.showCmd = Native.IsIconic(window) ? 2 : 1;
            }
            record = new HiddenWindowRecord();
            record.Handle = key;
            record.ProcessId = pid;
            record.ProcessStartTime = identity.StartTime;
            record.ExecutablePath = identity.Path;
            record.WindowClass = request.WindowClass;
            record.WasVisible = true;
            record.PlacementFlags = placement.flags;
            record.ShowCommand = placement.showCmd;
            record.MinX = placement.min.x; record.MinY = placement.min.y;
            record.MaxX = placement.max.x; record.MaxY = placement.max.y;
            record.Left = placement.normal.left; record.Top = placement.normal.top;
            record.Right = placement.normal.right; record.Bottom = placement.normal.bottom;
            if (!Native.IsWindow(window)) return 0;
            uint currentPid;
            Native.GetWindowThreadProcessId(window, out currentPid);
            if (currentPid != pid || !String.Equals(ReadClass(window), request.WindowClass, StringComparison.Ordinal)) return 0;
            if (!Native.SetProp(window, _journal.PropertyName, new IntPtr(_journal.PropertyValue)))
            {
                failure = "无法临时隐藏 " + System.IO.Path.GetFileName(identity.Path) + " 的窗口，请先关闭该高权限窗口。";
                return -1;
            }
            // The marker pins this window incarnation across the disk write. It has no visual effect.
            _journal.Entries.Add(record);
            _hidden[key] = record;
            request.Created = true;
            return 2;
        }

        private void ApplyHide(HideRequest request)
        {
            IntPtr window = request.Window;
            long key = window.ToInt64();
            HiddenWindowRecord record;
            if (!_hidden.TryGetValue(key, out record)) return;
            if (!Native.IsWindow(window) || Native.GetProp(window, _journal.PropertyName).ToInt64() != _journal.PropertyValue)
            {
                record.Restored = true;
                _hidden.Remove(key);
                return;
            }
            ulong now = Native.GetTickCount64();
            ulong previous;
            if (_lastHide.TryGetValue(key, out previous) && now - previous < 200) return;
            _lastHide[key] = now;
            if (!Native.ShowWindowAsync(window, 0)) return;
            Interlocked.Increment(ref _blockedCount);
            string name = System.IO.Path.GetFileName(request.Identity.Path);
            bool first;
            lock (_listLock)
            {
                int count;
                first = !_blockedApps.TryGetValue(name, out count);
                _blockedApps[name] = count + 1;
            }
            if (first && request.Created) RaiseNotice("已隐藏 " + name + "：不在本轮允许范围，程序在后台继续运行，结束后自动恢复。");
        }

        /// <summary>If a visible but non-usable window took the foreground (e.g. via Alt+Tab), go back to the focus screen.</summary>
        private void ReturnFromDeniedForeground(Dictionary<IntPtr, WindowAccess> cache)
        {
            IntPtr foreground = Native.GetForegroundWindow();
            if (foreground == IntPtr.Zero) return;
            IntPtr root = Native.GetAncestor(foreground, 2);
            if (root != IntPtr.Zero) foreground = root;
            WindowAccess access;
            if (!cache.TryGetValue(foreground, out access) || access.Allowed) return;
            string windowClass = ReadClass(foreground);
            if (windowClass == "Shell_TrayWnd" || windowClass == "Shell_SecondaryTrayWnd" || windowClass == "Progman" || windowClass == "WorkerW") return;
            ulong now = Native.GetTickCount64();
            if (now - _lastBounce < 500) return;
            _lastBounce = now;
            Native.SetForegroundWindow(_owner.Handle);
        }
        private bool IsInputAllowed(IntPtr window)
        {
            if (window == IntPtr.Zero) return false;
            IntPtr root = Native.GetAncestor(window, 2);
            if (root != IntPtr.Zero) window = root;
            uint pid;
            Native.GetWindowThreadProcessId(window, out pid);
            if (pid == _ownPid) return true;
            Dictionary<IntPtr, WindowAccess> cache = _windowCache;
            WindowAccess access;
            if (cache.TryGetValue(window, out access) && access.ProcessId == pid) return access.Allowed;
            // Owned menus/dialogs may appear between scans. Only trust a cached owner in the same PID.
            IntPtr owner = window;
            for (int i = 0; i < 4; i++)
            {
                owner = Native.GetWindow(owner, 4);
                if (owner == IntPtr.Zero) break;
                uint ownerPid;
                Native.GetWindowThreadProcessId(owner, out ownerPid);
                if (ownerPid != pid) break;
                if (cache.TryGetValue(owner, out access) && access.ProcessId == pid) return access.Allowed;
            }
            QueueReconcile();
            // A brand-new popup, completion list or IME candidate window of an already-allowed process
            // must not swallow the first keystrokes or clicks while the next scan is pending.
            bool processAllowed;
            return _pidAccess.TryGetValue(pid, out processAllowed) && processAllowed;
        }

        private IntPtr OnKeyboard(int code, IntPtr message, IntPtr data)
        {
            if (code < 0 || !_running || _stopping) return Native.CallNextHookEx(IntPtr.Zero, code, message, data);
            try
            {
                Native.KBDLLHOOKSTRUCT key = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(data, typeof(Native.KBDLLHOOKSTRUCT));
                if (key.extra.ToUInt64() == InjectMarker) return Native.CallNextHookEx(IntPtr.Zero, code, message, data); // our own Win+Space
                int vk = (int)key.vkCode;
                if (vk < 0 || vk >= _suppressedKeys.Length) return Native.CallNextHookEx(IntPtr.Zero, code, message, data);
                int msg = message.ToInt32();
                bool down = msg == 0x100 || msg == 0x104;
                bool up = msg == 0x101 || msg == 0x105;
                if (vk == 0x5B) _leftWinDown = down;
                if (vk == 0x5C) _rightWinDown = down;
                if (up && _suppressedKeys[vk]) { _suppressedKeys[vk] = false; return new IntPtr(1); }
                bool alt = (key.flags & 0x20) != 0;
                bool ctrl = (Native.GetAsyncKeyState(0x11) & 0x8000) != 0;
                IntPtr foreground = Native.GetForegroundWindow();
                uint foregroundPid;
                Native.GetWindowThreadProcessId(foreground, out foregroundPid);
                bool altKey = vk == 0x12 || vk == 0xA4 || vk == 0xA5;
                if (altKey && up) _altTabActive = false;
                // Alt+Tab: hidden windows are not in the switcher, so it only cycles usable windows.
                // While the switcher is open, keep Tab/Shift/arrows/Esc/Enter flowing to it.
                if (_altTabActive && (altKey || vk == 0x09 || vk == 0x10 || vk == 0xA0 || vk == 0xA1 || vk == 0x1B || vk == 0x0D || (vk >= 0x25 && vk <= 0x28)))
                    return Native.CallNextHookEx(IntPtr.Zero, code, message, data);
                if (alt && vk == 0x09 && down && !_leftWinDown && !_rightWinDown && IsInputAllowed(foreground))
                {
                    _altTabActive = true;
                    return Native.CallNextHookEx(IntPtr.Zero, code, message, data);
                }
                // Win+Space switches the input method. The Win key itself stays blocked (Start menu,
                // Win+D, Win+Tab ...), so the combination is replayed as one tagged, complete sequence.
                if (vk == 0x20 && (_leftWinDown || _rightWinDown) && IsInputAllowed(foreground))
                {
                    if (down) { _suppressedKeys[vk] = true; uint thread = _inputThreadId; if (thread != 0) Native.PostThreadMessage(thread, InjectWinSpaceMessage, IntPtr.Zero, IntPtr.Zero); }
                    return new IntPtr(1);
                }
                bool shortcut = vk == 0x5B || vk == 0x5C || _leftWinDown || _rightWinDown ||
                    (alt && vk == 0x1B) || (ctrl && vk == 0x1B) ||
                    (alt && vk == 0x73 && foregroundPid == _ownPid);
                if (shortcut || !IsInputAllowed(foreground))
                {
                    if (down) _suppressedKeys[vk] = true;
                    return new IntPtr(1);
                }
            }
            catch { /* Do not throw across the unmanaged hook boundary. */ }
            return Native.CallNextHookEx(IntPtr.Zero, code, message, data);
        }

        private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
        {
            if (code < 0 || !_running || _stopping) return Native.CallNextHookEx(IntPtr.Zero, code, message, data);
            try
            {
                int msg = message.ToInt32();
                if (msg != 0x200) // Pointer movement stays free; block buttons and wheel over denied windows.
                {
                    Native.MSLLHOOKSTRUCT mouse = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(data, typeof(Native.MSLLHOOKSTRUCT));
                    if (!IsInputAllowed(Native.WindowFromPoint(mouse.pt))) return new IntPtr(1);
                }
            }
            catch { /* Never let an input hook crash the host process. */ }
            return Native.CallNextHookEx(IntPtr.Zero, code, message, data);
        }

        private void StartInputThread()
        {
            _inputReady = new ManualResetEvent(false);
            Array.Clear(_suppressedKeys, 0, _suppressedKeys.Length);
            _leftWinDown = false; _rightWinDown = false; _altTabActive = false;
            _inputThread = new Thread(InputLoop);
            _inputThread.IsBackground = true;
            _inputThread.Name = "AiTime input guard";
            _inputThread.SetApartmentState(ApartmentState.STA);
            _inputThread.Start();
            if (!_inputReady.WaitOne(3000)) throw new TimeoutException("输入守卫启动超时。");
            if (_inputError != null) throw new InvalidOperationException("输入守卫未能启动。", _inputError);
        }

        private void InputLoop()
        {
            try
            {
                _inputThreadId = Native.GetCurrentThreadId();
                Native.MSG msg;
                Native.PeekMessage(out msg, IntPtr.Zero, 0, 0, 0); // Ensure PostThreadMessage has a queue.
                IntPtr module = Native.GetModuleHandle(null);
                _keyboardHook = Native.SetWindowsHookEx(13, _keyboardProc, module, 0);
                if (_keyboardHook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "键盘钩子安装失败。");
                _mouseHook = Native.SetWindowsHookEx(14, _mouseProc, module, 0);
                if (_mouseHook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "鼠标钩子安装失败。");
                _inputReady.Set();
                while (!_inputExit)
                {
                    int result = Native.GetMessage(out msg, IntPtr.Zero, 0, 0);
                    if (result <= 0) break;
                    if (msg.message == InjectWinSpaceMessage && msg.hwnd == IntPtr.Zero)
                    {
                        UIntPtr tag = new UIntPtr(InjectMarker);
                        Native.keybd_event(0x5B, 0x5B, 1, tag);
                        Native.keybd_event(0x20, 0x39, 0, tag);
                        Native.keybd_event(0x20, 0x39, 2, tag);
                        Native.keybd_event(0x5B, 0x5B, 3, tag);
                        continue;
                    }
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessage(ref msg);
                }
            }
            catch (Exception ex) { _inputError = ex; }
            finally
            {
                if (_keyboardHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_keyboardHook); _keyboardHook = IntPtr.Zero; }
                if (_mouseHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
                _inputReady.Set();
                _inputThreadId = 0;
            }
        }

        private void StopInputThread()
        {
            _inputExit = true;
            uint id = _inputThreadId;
            if (id != 0) Native.PostThreadMessage(id, 0x12, IntPtr.Zero, IntPtr.Zero);
            Thread thread = _inputThread;
            if (thread != null && thread != Thread.CurrentThread && thread.Join(1500))
            {
                _inputThread = null;
                if (_inputReady != null) { _inputReady.Dispose(); _inputReady = null; }
            }
        }

        private void PersistJournal()
        {
            using (JournalLock journalLock = new JournalLock(_journalPath)) WriteJournalUnlocked(_journalPath, _journal);
        }

        private static GuardJournal ReadJournal(string path)
        {
            FileInfo file = new FileInfo(path);
            if (file.Length > 4 * 1024 * 1024) throw new InvalidDataException("恢复记录异常过大。");
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = 4 * 1024 * 1024;
            serializer.RecursionLimit = 32;
            GuardJournal journal = serializer.Deserialize<GuardJournal>(File.ReadAllText(path, Encoding.UTF8));
            if (journal == null || journal.Version != 1 || String.IsNullOrEmpty(journal.SessionId) ||
                journal.Entries == null || journal.Entries.Count > 8192 || journal.PropertyValue != 0x4149544D ||
                !String.Equals(journal.PropertyName, PropertyName(journal.SessionId), StringComparison.Ordinal))
                throw new InvalidDataException("恢复记录格式无效；未操作任何窗口。");
            foreach (HiddenWindowRecord entry in journal.Entries)
                if (entry == null) throw new InvalidDataException("恢复记录包含无效窗口条目。");
            return journal;
        }

        private static void WriteJournalUnlocked(string path, GuardJournal journal)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = 4 * 1024 * 1024;
            serializer.RecursionLimit = 32;
            byte[] bytes = new UTF8Encoding(false).GetBytes(serializer.Serialize(journal));
            string pending = path + ".next";
            using (FileStream stream = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            // Same-directory rename, replace and write-through: no partially written recovery file.
            if (!Native.MoveFileEx(pending, path, 0x1 | 0x8))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法原子保存窗口恢复记录。");
        }

        private static bool HasPendingEntries(GuardJournal journal)
        {
            foreach (HiddenWindowRecord record in journal.Entries) if (!record.Restored) return true;
            return false;
        }

        private static void RecoverEntries(GuardJournal journal)
        {
            foreach (HiddenWindowRecord record in journal.Entries)
            {
                if (record == null || record.Restored) continue;
                try
                {
                    IntPtr window = new IntPtr(record.Handle);
                    uint pid;
                    if (!Native.IsWindow(window)) { record.Restored = true; continue; }
                    Native.GetWindowThreadProcessId(window, out pid);
                    if (pid != record.ProcessId || Native.GetProp(window, journal.PropertyName).ToInt64() != journal.PropertyValue)
                    { record.Restored = true; continue; }
                    ProcessIdentity identity;
                    if (!ReadIdentity(pid, out identity)) continue;
                    if (identity.StartTime != record.ProcessStartTime ||
                        !String.Equals(identity.Path, record.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                        !String.Equals(ReadClass(window), record.WindowClass, StringComparison.Ordinal))
                    { record.Restored = true; continue; }
                    Native.WINDOWPLACEMENT placement = new Native.WINDOWPLACEMENT();
                    placement.length = Marshal.SizeOf(typeof(Native.WINDOWPLACEMENT));
                    placement.flags = record.PlacementFlags | 0x4; // WPF_ASYNCWINDOWPLACEMENT: never wait on a hung app.
                    placement.showCmd = record.ShowCommand;
                    placement.min = new Native.POINT(record.MinX, record.MinY);
                    placement.max = new Native.POINT(record.MaxX, record.MaxY);
                    placement.normal = new Native.RECT(record.Left, record.Top, record.Right, record.Bottom);
                    Native.SetWindowPlacement(window, ref placement);
                    if (record.WasVisible)
                    {
                        // Restore visibility without intentionally changing the foreground or Z order.
                        Native.SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, 0x1 | 0x2 | 0x4 | 0x10 | 0x40 | 0x4000);
                        for (int attempt = 0; attempt < 8 && Native.IsWindow(window) && !Native.IsWindowVisible(window); attempt++)
                        {
                            Native.ShowWindowAsync(window, record.ShowCommand == 2 || record.ShowCommand == 7 ? 7 : 8);
                            Thread.Sleep(50);
                        }
                    }
                    if (!Native.IsWindow(window) || !record.WasVisible || Native.IsWindowVisible(window))
                    {
                        if (Native.IsWindow(window)) Native.RemoveProp(window, journal.PropertyName);
                        record.Restored = true;
                    }
                }
                catch { /* Continue restoring other windows; failed entries stay pending on disk. */ }
            }
        }

        private static string NormalizePath(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return String.Empty;
            string result = path.Trim().Replace('/', '\\');
            if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) result = @"\\" + result.Substring(8);
            else if (result.StartsWith(@"\\?\", StringComparison.Ordinal) || result.StartsWith(@"\??\", StringComparison.Ordinal)) result = result.Substring(4);
            if (!System.IO.Path.IsPathRooted(result)) return String.Empty;
            return System.IO.Path.GetFullPath(result).TrimEnd('\\');
        }

        private static string PropertyName(string sessionId) { return "AiTime.Guard." + HashText(sessionId).Substring(0, 24); }

        private static string HashText(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
                StringBuilder result = new StringBuilder(bytes.Length * 2);
                foreach (byte b in bytes) result.Append(b.ToString("x2"));
                return result.ToString();
            }
        }

        private static bool IsSameProcess(uint pid, long creationTime)
        {
            ProcessIdentity identity;
            return ReadIdentity(pid, out identity) && identity.StartTime == creationTime;
        }

        private static bool ReadIdentity(uint pid, out ProcessIdentity identity)
        {
            identity = null;
            if (pid == 0) return false;
            IntPtr process = Native.OpenProcess(0x1000, false, pid);
            if (process == IntPtr.Zero) return false;
            try
            {
                StringBuilder path = new StringBuilder(32768);
                uint capacity = (uint)path.Capacity;
                Native.FILETIME created, exited, kernel, user;
                if (!Native.QueryFullProcessImageName(process, 0, path, ref capacity) ||
                    !Native.GetProcessTimes(process, out created, out exited, out kernel, out user)) return false;
                string normalized = NormalizePath(path.ToString());
                long startTime = ((long)created.high << 32) | created.low;
                if (startTime <= 0 || normalized.Length == 0) return false;
                identity = new ProcessIdentity();
                identity.Path = normalized;
                identity.StartTime = startTime;
                return true;
            }
            finally { Native.CloseHandle(process); }
        }

        private static string ReadClass(IntPtr window)
        {
            StringBuilder value = new StringBuilder(256);
            Native.GetClassName(window, value, value.Capacity);
            return value.ToString();
        }

        private static string ReadTitle(IntPtr window)
        {
            StringBuilder value = new StringBuilder(512);
            Native.GetWindowText(window, value, value.Capacity);
            return value.ToString();
        }

        private static bool IsCloaked(IntPtr window)
        {
            try { int cloaked; return Native.DwmGetWindowAttribute(window, 14, out cloaked, 4) == 0 && cloaked != 0; }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }

        private void AcquireUserGuard()
        {
            string sid;
            using (WindowsIdentity current = WindowsIdentity.GetCurrent())
            {
                if (current.User == null) throw new InvalidOperationException("无法确定当前 Windows 用户。");
                sid = current.User.Value;
            }
            lock (ProcessGuardLock)
            {
                if (_processGuard != null) throw new InvalidOperationException("当前程序已有一轮专注守卫正在运行。");
                _userGuardMutex = new Mutex(false, @"Local\AiTimeGuard-" + sid);
                try
                {
                    try { _userGuardHeld = _userGuardMutex.WaitOne(0); }
                    catch (AbandonedMutexException) { _userGuardHeld = true; }
                    if (!_userGuardHeld) throw new InvalidOperationException("当前用户已有一轮专注守卫正在运行，不能重复开启。");
                    _processGuard = this;
                }
                catch
                {
                    _userGuardMutex.Dispose();
                    _userGuardMutex = null;
                    throw;
                }
            }
        }

        private void ReleaseUserGuard()
        {
            if (_userGuardMutex == null) return;
            try
            {
                if (_userGuardHeld) { _userGuardMutex.ReleaseMutex(); _userGuardHeld = false; }
            }
            finally
            {
                _userGuardMutex.Dispose();
                _userGuardMutex = null;
                lock (ProcessGuardLock) if (Object.ReferenceEquals(_processGuard, this)) _processGuard = null;
            }
        }

        private sealed class ActivationForm : Form
        {
            private readonly System.Windows.Forms.Timer _countdown;
            private readonly Label _remaining;
            private ulong _deadline;

            internal ActivationForm()
            {
                Text = "爱时间 · 准备开始";
                FormBorderStyle = FormBorderStyle.FixedDialog;
                StartPosition = FormStartPosition.CenterParent;
                ClientSize = new Size(480, 234);
                MaximizeBox = false;
                MinimizeBox = false;
                ShowInTaskbar = false;
                TopMost = true;
                BackColor = Color.FromArgb(13, 19, 28);
                ForeColor = Color.FromArgb(226, 235, 242);
                Font = new Font("Microsoft YaHei UI", 10f);
                Label heading = new Label();
                heading.Text = "准备就绪";
                heading.Font = new Font("Microsoft YaHei UI", 18f, FontStyle.Bold);
                heading.ForeColor = Color.FromArgb(105, 204, 224);
                heading.SetBounds(24, 19, 420, 42);
                Controls.Add(heading);
                Label explanation = new Label();
                explanation.Text = "点击开始本轮专注。\n窗口准备完成后才会开始计时。";
                explanation.SetBounds(26, 71, 424, 54);
                Controls.Add(explanation);
                _remaining = new Label();
                _remaining.ForeColor = Color.FromArgb(157, 173, 190);
                _remaining.SetBounds(26, 131, 424, 25);
                Controls.Add(_remaining);
                Button begin = MakeButton("开始本轮专注", true);
                begin.SetBounds(24, 177, 250, 38);
                begin.Click += delegate { DialogResult = DialogResult.OK; Close(); };
                Controls.Add(begin);
                Button cancel = MakeButton("返回设置", false);
                cancel.SetBounds(291, 177, 165, 38);
                cancel.Click += delegate { CancelFromGuard(); };
                Controls.Add(cancel);
                AcceptButton = begin;
                CancelButton = cancel;
                _countdown = new System.Windows.Forms.Timer();
                _countdown.Interval = 200;
                _countdown.Tick += delegate
                {
                    ulong now = Native.GetTickCount64();
                    if (now >= _deadline) { CancelFromGuard(); return; }
                    _remaining.Text = "尚未计时 · " + ((_deadline - now + 999) / 1000) + " 秒内点击开始，否则自动返回";
                };
                Shown += delegate
                {
                    _deadline = Native.GetTickCount64() + 20000;
                    _remaining.Text = "尚未计时 · 20 秒内点击开始，否则自动返回";
                    _countdown.Start();
                    begin.Focus();
                };
            }

            private static Button MakeButton(string text, bool accent)
            {
                Button button = new Button();
                button.Text = text;
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = Color.FromArgb(59, 77, 95);
                button.BackColor = accent ? Color.FromArgb(45, 145, 170) : Color.FromArgb(30, 40, 53);
                button.ForeColor = Color.White;
                button.UseVisualStyleBackColor = false;
                return button;
            }

            internal void CancelFromGuard()
            {
                _countdown.Stop();
                DialogResult = DialogResult.Cancel;
                Close();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) _countdown.Dispose();
                base.Dispose(disposing);
            }
        }

        private sealed class CoverageForm : Form
        {
            private readonly Action _returnToOwner;
            private bool _releasing;
            internal CoverageForm(Action returnToOwner)
            {
                _returnToOwner = returnToOwner;
                Text = "爱时间 · 兼容覆盖";
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                ShowInTaskbar = false;
                TopMost = true;
                BackColor = Color.FromArgb(9, 13, 19);
                ForeColor = Color.FromArgb(167, 183, 198);
                Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Regular);
                DoubleBuffered = true;
                MouseDown += delegate { if (!_releasing && _returnToOwner != null) _returnToOwner(); };
            }
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams p = base.CreateParams;
                    p.ExStyle |= 0x08000000 | 0x80; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW.
                    return p;
                }
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                TextRenderer.DrawText(e.Graphics, "专注进行中\n\n点击返回本轮允许的应用", Font, ClientRectangle,
                    ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            }
            protected override void OnFormClosing(FormClosingEventArgs e)
            {
                if (!_releasing) e.Cancel = true;
                base.OnFormClosing(e);
            }
            internal void ReplaceRegion(Region region)
            {
                Region previous = Region;
                Region = region;
                if (previous != null && !Object.ReferenceEquals(previous, region)) previous.Dispose();
            }
            internal void Release()
            {
                _releasing = true;
                Region previous = Region;
                Region = null;
                if (previous != null) previous.Dispose();
                Close();
                Dispose();
            }
        }

        private sealed class WindowAccess
        {
            public readonly uint ProcessId;
            public readonly bool Allowed;
            public WindowAccess(uint pid, bool allowed) { ProcessId = pid; Allowed = allowed; }
        }

        private sealed class ProcessIdentity
        {
            public string Path;
            public long StartTime;
        }

        public sealed class GuardJournal
        {
            public int Version;
            public string SessionId;
            public string PropertyName;
            public long PropertyValue;
            public uint GuardProcessId;
            public long GuardProcessStartTime;
            public bool Active;
            public bool RecoveryNeeded;
            public string CreatedUtc;
            public string EndedUtc;
            public List<HiddenWindowRecord> Entries;
        }

        public sealed class HiddenWindowRecord
        {
            public long Handle;
            public uint ProcessId;
            public long ProcessStartTime;
            public string ExecutablePath;
            public string WindowClass;
            public bool WasVisible;
            public bool Restored;
            public int PlacementFlags;
            public int ShowCommand;
            public int MinX, MinY, MaxX, MaxY, Left, Top, Right, Bottom;
        }

        private sealed class JournalLock : IDisposable
        {
            private readonly Mutex _mutex;
            private bool _held;
            public JournalLock(string path)
            {
                _mutex = new Mutex(false, @"Local\AiTimeJournal_" + HashText(path.ToUpperInvariant()).Substring(0, 24));
                try { _held = _mutex.WaitOne(3000); }
                catch (AbandonedMutexException) { _held = true; }
                if (!_held) { _mutex.Dispose(); throw new IOException("恢复记录正被另一进程使用。"); }
            }
            public void Dispose()
            {
                if (_held) { _mutex.ReleaseMutex(); _held = false; }
                _mutex.Dispose();
            }
        }

        private static class Native
        {
            internal delegate bool EnumWindowsProc(IntPtr window, IntPtr state);
            internal delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint threadId, uint time);
            internal delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

            [StructLayout(LayoutKind.Sequential)] internal struct POINT
            {
                internal int x, y;
                internal POINT(int px, int py) { x = px; y = py; }
            }
            [StructLayout(LayoutKind.Sequential)] internal struct RECT
            {
                internal int left, top, right, bottom;
                internal RECT(int l, int t, int r, int b) { left = l; top = t; right = r; bottom = b; }
            }
            [StructLayout(LayoutKind.Sequential)] internal struct WINDOWPLACEMENT
            {
                internal int length, flags, showCmd;
                internal POINT min, max;
                internal RECT normal;
            }
            [StructLayout(LayoutKind.Sequential)] internal struct FILETIME { internal uint low, high; }
            [StructLayout(LayoutKind.Sequential)] internal struct KBDLLHOOKSTRUCT
            {
                internal uint vkCode, scanCode, flags, time;
                internal UIntPtr extra;
            }
            [StructLayout(LayoutKind.Sequential)] internal struct MSLLHOOKSTRUCT
            {
                internal POINT pt;
                internal uint mouseData, flags, time;
                internal UIntPtr extra;
            }
            [StructLayout(LayoutKind.Sequential)] internal struct MSG
            {
                internal IntPtr hwnd;
                internal uint message;
                internal UIntPtr wParam;
                internal IntPtr lParam;
                internal uint time;
                internal POINT pt;
                internal uint lPrivate;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct PROCESSENTRY32
            {
                internal uint dwSize, cntUsage, th32ProcessID;
                internal IntPtr th32DefaultHeapID;
                internal uint th32ModuleID, cntThreads, th32ParentProcessID;
                internal int pcPriClassBase;
                internal uint dwFlags;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string szExeFile;
            }
            [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW", SetLastError = true)] internal static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW", SetLastError = true)] internal static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);
            [DllImport("user32.dll")] internal static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
            [DllImport("kernel32.dll")] internal static extern uint GetCurrentProcessId();
            [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
            [DllImport("kernel32.dll")] internal static extern ulong GetTickCount64();
            [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
            [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CloseHandle(IntPtr handle);
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref uint size);
            [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetProcessTimes(IntPtr process, out FILETIME created, out FILETIME exited, out FILETIME kernel, out FILETIME user);
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool MoveFileEx(string source, string destination, uint flags);
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr GetModuleHandle(string name);
            [DllImport("user32.dll", SetLastError = true)] internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr state);
            [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr window);
            [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
            [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr window);
            [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr window, StringBuilder value, int maximum);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr window, StringBuilder value, int maximum);
            [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowLong(IntPtr window, int index);
            [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr window, uint command);
            [DllImport("user32.dll")] internal static extern IntPtr GetTopWindow(IntPtr window);
            [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr window, uint flags);
            [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
            [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
            [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(POINT point);
            [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
            [DllImport("user32.dll", SetLastError = true)] internal static extern bool ShowWindowAsync(IntPtr window, int command);
            [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetWindowRect(IntPtr window, out RECT rect);
            [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetWindowPlacement(IntPtr window, ref WINDOWPLACEMENT placement);
            [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPlacement(IntPtr window, ref WINDOWPLACEMENT placement);
            [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool SetProp(IntPtr window, string name, IntPtr data);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetProp(IntPtr window, string name);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr RemoveProp(IntPtr window, string name);
            [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetWinEventHook(uint minimum, uint maximum, IntPtr module, WinEventProc callback, uint pid, uint thread, uint flags);
            [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(IntPtr hook);
            [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] internal static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
            [DllImport("user32.dll", SetLastError = true)] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
            [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
            [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)] internal static extern int GetMessage(out MSG message, IntPtr window, uint minimum, uint maximum);
            [DllImport("user32.dll", EntryPoint = "PeekMessageW")] internal static extern bool PeekMessage(out MSG message, IntPtr window, uint minimum, uint maximum, uint remove);
            [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref MSG message);
            [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] internal static extern IntPtr DispatchMessage(ref MSG message);
            [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)] internal static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);
            [DllImport("user32.dll")] internal static extern bool GetLayeredWindowAttributes(IntPtr window, out uint color, out byte alpha, out uint flags);
            [DllImport("user32.dll")] internal static extern int GetWindowRgn(IntPtr window, IntPtr region);
            [DllImport("gdi32.dll")] internal static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
            [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr value);
            [DllImport("dwmapi.dll")] internal static extern int DwmFlush();
            [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] internal static extern int DwmGetWindowFrame(IntPtr window, int attribute, out RECT value, int size);
            [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
        }
    }
}