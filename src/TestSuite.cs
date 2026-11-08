using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace AiTime
{
    public static class TestSuite
    {
        public sealed class Check
        {
            public string name;
            public bool passed;
            public string observed;
        }

        public sealed class TestReport
        {
            public string app = "AiTime Windows";
            public string scope;
            public string runId;
            public string startedAt;
            public string endedAt;
            public string testDataPath;
            public int executedTests;
            public int passedTests;
            public int failedTests;
            public bool success;
            public List<Check> checks = new List<Check>();
            public List<string> notices = new List<string>();

            public void Assert(string name, bool passed, string observed)
            {
                checks.Add(new Check { name = name, passed = passed, observed = observed });
            }

            public void Finish()
            {
                endedAt = DateTimeOffset.Now.ToString("o");
                executedTests = checks.Count;
                passedTests = checks.Count(c => c.passed);
                failedTests = executedTests - passedTests;
                success = executedTests > 0 && failedTests == 0;
            }
        }

        public static int Run(string outputPath, bool integration)
        {
            outputPath = Path.GetFullPath(outputPath);
            string outputDirectory = Path.GetDirectoryName(outputPath);
            Directory.CreateDirectory(outputDirectory);
            var report = new TestReport {
                scope = integration ? "native-window-integration" : "data-and-timer",
                runId = Guid.NewGuid().ToString("N"),
                startedAt = DateTimeOffset.Now.ToString("o")
            };
            report.testDataPath = Path.Combine(outputDirectory, "test-data-" + report.runId);
            Directory.CreateDirectory(report.testDataPath);
            try
            {
                if (integration)
                {
                    using (var harness = new IntegrationHarness(report)) Application.Run(harness);
                }
                else RunDataTests(report);
            }
            catch (Exception ex)
            {
                report.Assert("unexpected_exception", false, ex.ToString());
            }
            report.Finish();
            var serializer = new JavaScriptSerializer { MaxJsonLength = 16777216 };
            File.WriteAllText(outputPath, serializer.Serialize(report), new UTF8Encoding(false));
            return report.success ? 0 : 1;
        }

        // Read-only test prerequisite. Normal user clicks establish foreground ownership.
        // This helper never activates a window, moves the pointer, or injects input.
        public static bool TryPrepareForeground(Form owner, out string detail)
        {
            detail = "test_preparation=read_only; input_events_sent=0";
            if (owner == null || owner.IsDisposed || owner.Disposing || !owner.IsHandleCreated || !owner.Visible
                || owner.WindowState == FormWindowState.Minimized || owner.InvokeRequired)
            {
                detail += "; owner must be a visible, ready test Form on its UI thread";
                return false;
            }
            IntPtr expected = owner.Handle;
            IntPtr actual = GetForegroundWindow();
            bool verified = actual == expected;
            detail += "; foreground_hwnd=" + actual.ToInt64() + "; expected_hwnd=" + expected.ToInt64();
            if (!verified)
                detail += "; 请手动切回并点击可见测试窗口，再运行验证；本检查不会激活窗口或注入输入。";
            return verified;
        }

        private static void RunDataTests(TestReport report)
        {
            ulong before = NativeClock.NowMilliseconds;
            Thread.Sleep(35);
            ulong after = NativeClock.NowMilliseconds;
            report.Assert("monotonic_clock_advances_while_awake", after > before,
                "before_ms=" + before + "; after_ms=" + after + "; this does not simulate OS sleep or wall-clock changes");

            string ownExe = Application.ExecutablePath;
            string dotted = Path.Combine(Path.GetDirectoryName(ownExe), ".", Path.GetFileName(ownExe));
            report.Assert("exe_path_dot_segments_normalized",
                String.Equals(NativeWindows.NormalizedPath(ownExe), NativeWindows.NormalizedPath(dotted), StringComparison.OrdinalIgnoreCase),
                NativeWindows.NormalizedPath(dotted));
            report.Assert("existing_absolute_exe_accepted", NativeWindows.IsAllowedExecutablePath(ownExe), ownExe);
            string textPath = Path.Combine(report.testDataPath, "document.txt");
            File.WriteAllText(textPath, "test fixture", new UTF8Encoding(false));
            report.Assert("non_executable_document_rejected", !NativeWindows.IsAllowedExecutablePath(textPath), textPath);
            string missingPath = Path.Combine(report.testDataPath, "does-not-exist.exe");
            report.Assert("missing_exe_rejected", !NativeWindows.IsAllowedExecutablePath(missingPath), missingPath);
            report.Assert("relative_exe_rejected", !NativeWindows.IsAllowedExecutablePath("Fixture.exe"), "Fixture.exe");

            var store = new AppStore(report.testDataPath);
            report.Assert("new_store_has_no_fabricated_history", store.Records.Count == 0, "records=" + store.Records.Count);
            report.Assert("new_store_defaults_to_one_cycle", store.Config.Cycles == 1, "cycles=" + store.Config.Cycles);
            store.Config.Minutes = 45;
            store.Config.DailyGoalMinutes = 180;
            store.Config.Cycles = 3;
            store.Config.BreakMinutes = 5;
            store.Config.Subject = "中文路径与学习任务";
            store.Config.Allowed.Add(new AllowedApp { Name = "Self test", Path = ownExe, Arguments = "" });
            store.SaveConfig();
            store.Tasks.Add(new TaskItem { Id = "test-task-1", Title = "完成今天的 Python 实操", Done = false });
            store.SaveTasks();
            var reloaded = new AppStore(report.testDataPath);
            report.Assert("settings_roundtrip", reloaded.Config.Minutes == 45 && reloaded.Config.DailyGoalMinutes == 180
                && reloaded.Config.Cycles == 3 && reloaded.Config.BreakMinutes == 5 && reloaded.Config.Subject == "中文路径与学习任务",
                "minutes=" + reloaded.Config.Minutes + "; cycles=" + reloaded.Config.Cycles + "; subject=" + reloaded.Config.Subject);
            report.Assert("allowlist_roundtrip", reloaded.Config.Allowed.Count == 1
                && String.Equals(reloaded.Config.Allowed[0].Path, ownExe, StringComparison.OrdinalIgnoreCase),
                "allowed_count=" + reloaded.Config.Allowed.Count);
            report.Assert("todo_roundtrip", reloaded.Tasks.Count == 1 && reloaded.Tasks[0].Title == "完成今天的 Python 实操"
                && !reloaded.Tasks[0].Done, "task_count=" + reloaded.Tasks.Count);
            reloaded.Tasks[0].Done = true;
            reloaded.SaveTasks();
            report.Assert("todo_completion_persisted", new AppStore(report.testDataPath).Tasks[0].Done, "completed=true after reload");

            DateTime anchor = DateTime.Today.AddHours(1);
            reloaded.AddRecord(new FocusRecord { Id = "self-normal", Subject = "验证", Status = "已完成",
                StartedAt = anchor, EndedAt = anchor.AddSeconds(120), PlannedSeconds = 120, ActualSeconds = 120, IsTest = false });
            reloaded.AddRecord(new FocusRecord { Id = "self-trial", Subject = "试锁", Status = "已完成",
                StartedAt = anchor.AddMinutes(3), EndedAt = anchor.AddMinutes(3).AddSeconds(15), PlannedSeconds = 15, ActualSeconds = 15, IsTest = true });
            reloaded.AddRecord(new FocusRecord { Id = "self-yesterday", Subject = "昨天", Status = "已完成",
                StartedAt = anchor.AddDays(-1), EndedAt = anchor.AddDays(-1).AddSeconds(60), PlannedSeconds = 60, ActualSeconds = 60, IsTest = false });
            report.Assert("today_excludes_trial_and_other_dates", reloaded.GetTodaySeconds() == 120,
                "today_seconds=" + reloaded.GetTodaySeconds() + "; expected=120");
            reloaded.AddRecord(new FocusRecord { Id = "self-cross-midnight", Subject = "跨日", Status = "已完成",
                StartedAt = DateTime.Today.AddSeconds(-30), EndedAt = DateTime.Today.AddSeconds(30),
                PlannedSeconds = 60, ActualSeconds = 60, IsTest = false });
            report.Assert("cross_midnight_time_is_split", reloaded.GetTodaySeconds() == 150,
                "today_seconds=" + reloaded.GetTodaySeconds() + "; expected=150");
            reloaded.AddRecord(new FocusRecord { Id = "self-normal", Subject = "duplicate", StartedAt = anchor,
                EndedAt = anchor.AddSeconds(120), PlannedSeconds = 120, ActualSeconds = 120 });
            report.Assert("duplicate_record_id_is_idempotent", reloaded.Records.Count(r => r.Id == "self-normal") == 1
                && reloaded.GetTodaySeconds() == 150,
                "matching_id_count=" + reloaded.Records.Count(r => r.Id == "self-normal") + "; today_seconds=" + reloaded.GetTodaySeconds());
            var finalStore = new AppStore(report.testDataPath);
            report.Assert("history_persisted", finalStore.Records.Count == 4 && finalStore.GetTodaySeconds() == 150,
                "records=" + finalStore.Records.Count + "; today_seconds=" + finalStore.GetTodaySeconds());
            string export = Path.Combine(report.testDataPath, "export.json");
            finalStore.ExportRecords(export);
            string exported = File.ReadAllText(export, Encoding.UTF8);
            object parsed = new JavaScriptSerializer().DeserializeObject(exported);
            report.Assert("export_is_valid_json_and_contains_saved_record", parsed != null && exported.Contains("self-normal"),
                "export_bytes=" + new FileInfo(export).Length);

            RunGuardRuleTests(report);
            report.notices.Add("Tests use an isolated data directory; no real settings, tasks, or study history are changed.");
            report.notices.Add("OS sleep/hibernation, elevated windows, multiple displays, and actual physical keyboard/mouse input require separate runtime checks.");
        }

        // Allow-rule classification only: no hooks are installed and no window is hidden.
        private static void RunGuardRuleTests(TestReport report)
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string ownExe = Application.ExecutablePath;
            string installRoot = Path.GetDirectoryName(Path.GetDirectoryName(ownExe));
            using (Form owner = new Form())
            {
                GuardEngine guard = new GuardEngine(owner, new[] { ownExe }, Path.Combine(report.testDataPath, "rules.windows.json"), "rules-" + report.runId);
                string ime = Path.Combine(windows, @"System32\InputMethod\CHS\ChsIME.exe");
                report.Assert("rule_microsoft_ime_is_input_component", guard.InputComponentForTest(ime), ime);
                string notepad = Path.Combine(system, "notepad.exe");
                report.Assert("rule_ordinary_system_app_is_not_input_component", !guard.InputComponentForTest(notepad), notepad);
                string sibling = Path.Combine(installRoot, @"tools\helper.exe");
                report.Assert("rule_install_folder_component_allowed", guard.FamilyForTest(sibling), sibling);
                string elsewhere = Path.Combine(Path.GetDirectoryName(installRoot), @"Other\helper.exe");
                report.Assert("rule_other_folder_not_allowed", !guard.FamilyForTest(elsewhere), elsewhere);

                Process runtime = null, plain = null;
                try
                {
                    runtime = Process.Start(new ProcessStartInfo(Path.Combine(system, "cmd.exe"), "/c ping -n 6 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true });
                    plain = Process.Start(new ProcessStartInfo(Path.Combine(system, "PING.EXE"), "-n 6 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true });
                    Thread.Sleep(300);
                    report.Assert("rule_runtime_started_by_session_allowed", guard.ProcessPermittedForTest((uint)runtime.Id), "cmd.exe pid=" + runtime.Id);
                    report.Assert("rule_non_runtime_child_not_allowed", !guard.ProcessPermittedForTest((uint)plain.Id), "PING.EXE pid=" + plain.Id);
                }
                finally
                {
                    foreach (Process p in new[] { runtime, plain })
                        if (p != null) { try { if (!p.HasExited) p.Kill(); } catch (Exception) { } p.Dispose(); }
                }
            }
            report.Assert("rule_console_program_detected", SessionForm.IsConsoleProgram(Path.Combine(system, "cmd.exe")) && !SessionForm.IsConsoleProgram(ownExe),
                "cmd=console; AiTime=windows");
        }

        private sealed class IntegrationHarness : Form
        {
            private readonly TestReport report;
            private readonly System.Windows.Forms.Timer timer;
            private readonly Stopwatch elapsed = Stopwatch.StartNew();
            private readonly Label status;
            private Process allowedProcess;
            private Process blockedProcess;
            private Process watchdogProcess;
            private IntPtr allowedHandle;
            private IntPtr blockedHandle;
            private IntPtr inputEdit;
            private string inputBaseline;
            private bool activationAccepted;
            private Point savedPointer;
            private bool pointerSaved;
            private int mouseCountBefore;
            private GuardEngine guard;
            private string journal;
            private string watchdogStopPath;
            private string watchdogReadyPath;
            private string allowedPath;
            private string blockedPath;
            private int stage;
            private long stageAt;
            private bool finished;
            private bool guardStarted;
            private WindowPlacement initialAllowed;
            private WindowPlacement initialBlocked;

            public IntegrationHarness(TestReport report)
            {
                this.report = report;
                Text = "AiTime · 锁定验证（自动恢复）";
                Size = new Size(760, 370);
                StartPosition = FormStartPosition.CenterScreen;
                BackColor = Color.FromArgb(12, 15, 21);
                ForeColor = Color.White;
                ControlBox = false;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                Font = new Font("Microsoft YaHei UI", 11);
                status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                    Text = "正在准备专用验证窗口\n验证结束后自动恢复所有窗口" };
                Controls.Add(status);
                timer = new System.Windows.Forms.Timer { Interval = 120 };
                timer.Tick += Tick;
                Shown += delegate { StartFixtures(); };
                FormClosing += delegate(object sender, FormClosingEventArgs e) { if (!finished) e.Cancel = true; };
            }

            private void StartFixtures()
            {
                try
                {
                    allowedPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tests", "allowed", "Fixture.exe");
                    blockedPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tests", "blocked", "Fixture.exe");
                    report.Assert("fixture_binaries_exist", File.Exists(allowedPath) && File.Exists(blockedPath),
                        "allowed_exists=" + File.Exists(allowedPath) + "; blocked_exists=" + File.Exists(blockedPath));
                    if (!File.Exists(allowedPath) || !File.Exists(blockedPath)) { Finish(); return; }
                    report.Assert("fixtures_have_same_name_different_paths", Path.GetFileName(allowedPath) == Path.GetFileName(blockedPath)
                        && !String.Equals(allowedPath, blockedPath, StringComparison.OrdinalIgnoreCase),
                        "same_name=" + Path.GetFileName(allowedPath) + "; different_directories=true");
                    allowedProcess = Process.Start(new ProcessStartInfo(allowedPath, "allowed-" + report.runId) { UseShellExecute = false });
                    blockedProcess = Process.Start(new ProcessStartInfo(blockedPath, "blocked-" + report.runId) { UseShellExecute = false });
                    journal = Path.Combine(report.testDataPath, "integration-window-journal.json");
                    watchdogStopPath = Path.Combine(report.testDataPath, "integration-watchdog.stop");
                    watchdogReadyPath = Path.Combine(report.testDataPath, "integration-watchdog.ready");
                    stageAt = elapsed.ElapsedMilliseconds;
                    timer.Start();
                }
                catch (Exception ex) { report.Assert("fixture_start", false, ex.ToString()); Finish(); }
            }

            private void Tick(object sender, EventArgs args)
            {
                if (finished) return;
                try
                {
                    long age = elapsed.ElapsedMilliseconds - stageAt;
                    if (elapsed.ElapsedMilliseconds > 20000)
                    {
                        report.Assert("integration_deadline", false, "Exceeded 20 seconds; restoring windows.");
                        Finish(); return;
                    }
                    if (stage == 0)
                    {
                        allowedProcess.Refresh(); blockedProcess.Refresh();
                        allowedHandle = allowedProcess.MainWindowHandle;
                        blockedHandle = blockedProcess.MainWindowHandle;
                        if (allowedHandle == IntPtr.Zero || blockedHandle == IntPtr.Zero)
                        {
                            if (age > 8000) { report.Assert("fixtures_ready", false, "A fixture did not create a window within 8 seconds."); Finish(); }
                            return;
                        }
                        initialAllowed = Placement(allowedHandle);
                        initialBlocked = Placement(blockedHandle);
                        report.Assert("fixtures_initially_visible", IsWindowVisible(allowedHandle) && IsWindowVisible(blockedHandle),
                            "allowed_visible=" + IsWindowVisible(allowedHandle) + "; blocked_visible=" + IsWindowVisible(blockedHandle));
                        using (Process self = Process.GetCurrentProcess())
                            watchdogProcess = Program.StartWatchdog(self.Id, self.StartTime.ToUniversalTime().Ticks,
                                journal, watchdogStopPath, watchdogReadyPath);
                        status.Text = "正在准备独立恢复进程\n恢复进程就绪后才开始窗口锁定验证";
                        Advance(10);
                    }
                    else if (stage == 10)
                    {
                        bool watchdogAlive = watchdogProcess != null && !watchdogProcess.HasExited;
                        bool ready = false;
                        if (watchdogAlive && File.Exists(watchdogReadyPath))
                        {
                            try
                            {
                                int readyPid;
                                ready = Int32.TryParse(File.ReadAllText(watchdogReadyPath).Trim(), out readyPid)
                                    && readyPid == watchdogProcess.Id;
                            }
                            catch (IOException) { }
                        }
                        if (!ready)
                        {
                            if (!watchdogAlive || age >= 4000)
                            {
                                report.Assert("watchdog_ready_before_guard_start", false,
                                    "alive=" + watchdogAlive + "; ready=false; waited_ms=" + age + "; guard was not started");
                                Finish();
                            }
                            return;
                        }
                        report.Assert("watchdog_ready_before_guard_start", true,
                            "ready_pid=" + watchdogProcess.Id + "; waited_ms=" + age + "; no crash was injected");
                        guard = new GuardEngine(this, new string[] { allowedPath }, journal, report.runId);
                        guard.Notice += delegate(string notice) { if (report.notices.Count < 50) report.notices.Add(notice); };
                        // Guard.Start may pump UI messages. Do not let this test timer re-enter.
                        timer.Stop();
                        try
                        {
                            string preparationDetail;
                            bool prepared = TryPrepareForeground(this, out preparationDetail);
                            report.Assert("test_preparation_verified_foreground", prepared, preparationDetail);
                            report.notices.Add("Preparation only verified that the test Form was already foreground; no window activation, pointer movement, or input was injected.");
                            if (!prepared) { Finish(); return; }
                            guard.Start();
                            guardStarted = true;
                            if (elapsed.ElapsedMilliseconds > 20000)
                            {
                                report.Assert("integration_deadline", false, "Guard.Start returned after the 20-second test deadline; no lock validation was claimed.");
                                Finish(); return;
                            }
                        }
                        finally { if (!finished) timer.Start(); }
                        status.Text = "正在验证 exe 白名单\n其他窗口暂时隐藏，后台进程继续运行\n即将自动恢复";
                        Advance(1);
                    }
                    else if (stage == 1 && age >= 1400)
                    {
                        report.Assert("allowed_window_remains_visible", IsWindowVisible(allowedHandle), "visible=" + IsWindowVisible(allowedHandle));
                        report.Assert("same_name_unlisted_path_is_blocked", !IsWindowVisible(blockedHandle), "blocked_visible=" + IsWindowVisible(blockedHandle));
                        report.Assert("both_fixture_processes_keep_running", !allowedProcess.HasExited && !blockedProcess.HasExited,
                            "allowed_running=" + !allowedProcess.HasExited + "; blocked_running=" + !blockedProcess.HasExited);
                        var windows = guard.GetAllowedWindows();
                        report.Assert("allowed_window_is_switchable", windows.Any(w => w.Handle == allowedHandle.ToInt64()),
                            "allowed_window_count=" + windows.Count);
                        report.Assert("unlisted_window_not_in_launcher", !windows.Any(w => w.Handle == blockedHandle.ToInt64()),
                            "blocked_handle_in_list=" + windows.Any(w => w.Handle == blockedHandle.ToInt64()));
                        report.Assert("blocked_window_activation_rejected", !guard.ActivateWindow(blockedHandle.ToInt64()),
                            "Only the disallowed test fixture was requested.");
                        IntPtr edit = FindFixtureEdit(allowedHandle);
                        inputEdit = edit;
                        bool messageRoundTrip = false;
                        if (edit != IntPtr.Zero)
                        {
                            string value = "Allowed fixture text: " + report.runId;
                            inputBaseline = value;
                            UIntPtr messageResult;
                            IntPtr writeOk = SendMessageText(edit, 0x000C, IntPtr.Zero, value, 2, 500, out messageResult);
                            var buffer = new StringBuilder(256);
                            IntPtr readOk = SendMessageBuffer(edit, 0x000D, new IntPtr(buffer.Capacity), buffer, 2, 500, out messageResult);
                            messageRoundTrip = writeOk != IntPtr.Zero && readOk != IntPtr.Zero && buffer.ToString() == value;
                        }
                        report.Assert("allowed_window_message_io", messageRoundTrip,
                            "WM_SETTEXT/WM_GETTEXT to the dedicated fixture edit control; this is not a physical-input test.");
                        if (!messageRoundTrip) { Finish(); return; }
                        activationAccepted = guard.ActivateWindow(allowedHandle.ToInt64());
                        Advance(20);
                    }
                    else if (stage == 20 && age >= 400)
                    {
                        if (!ConfirmInputTarget("allowed_window_receives_foreground_and_textbox_focus")) { Finish(); return; }
                        bool modifiersReleased = !AnyInputModifierHeld();
                        report.Assert("synthetic_input_precondition_no_modifiers", modifiersReleased,
                            "Ctrl/Alt/Shift/left-Win/right-Win must all be up before injecting a character.");
                        if (!modifiersReleased) { Finish(); return; }
                        UIntPtr selectionResult;
                        int insertionPoint = inputBaseline.Length;
                        IntPtr selectionOk = SendMessageValue(inputEdit, 0x00B1, new IntPtr(insertionPoint), new IntPtr(insertionPoint), 2, 500, out selectionResult);
                        UIntPtr selectedRange;
                        IntPtr selectionReadOk = SendMessageValue(inputEdit, 0x00B0, IntPtr.Zero, IntPtr.Zero, 2, 500, out selectedRange);
                        // This fixture is below 512 UTF-16 code units, so EM_GETSEL's packed 16-bit endpoints are sufficient.
                        ulong selected = selectedRange.ToUInt64();
                        int selectionStart = (int)(selected & 0xFFFFUL);
                        int selectionEnd = (int)((selected >> 16) & 0xFFFFUL);
                        bool caretVerified = selectionOk != IntPtr.Zero && selectionReadOk != IntPtr.Zero
                            && selectionStart == insertionPoint && selectionEnd == insertionPoint;
                        report.Assert("synthetic_unicode_caret_at_known_end", caretVerified,
                            "expected=" + insertionPoint + "; selection_start=" + selectionStart + "; selection_end=" + selectionEnd);
                        if (!caretVerified || !IsInputTargetCurrent())
                        {
                            report.Assert("synthetic_input_target_unchanged", false,
                                "Dedicated TextBox selection or foreground changed before SendInput; no character was sent.");
                            Finish(); return;
                        }
                        Input[] keys = new Input[] { UnicodeKey('Z', false), UnicodeKey('Z', true) };
                        uint sent = SendInput((uint)keys.Length, keys, Marshal.SizeOf(typeof(Input)));
                        report.Assert("synthetic_unicode_events_accepted", sent == 2,
                            "sent=" + sent + "; expected=2; Unicode Z down/up; Win32Error=" + Marshal.GetLastWin32Error());
                        if (sent != 2)
                        {
                            if (sent == 1) SendInput(1, new Input[] { UnicodeKey('Z', true) }, Marshal.SizeOf(typeof(Input)));
                            Finish(); return;
                        }
                        Advance(21);
                    }
                    else if (stage == 21 && age >= 400)
                    {
                        if (!ConfirmInputTarget("synthetic_unicode_keeps_verified_input_target")) { Finish(); return; }
                        string actual = ReadFixtureText(inputEdit);
                        bool reached = actual == inputBaseline + "Z";
                        report.Assert("synthetic_unicode_reaches_allowed_textbox", reached,
                            "expected_suffix=Z; matches_full_expected_text=" + reached + "; observed_text=" + actual);
                        if (!reached) { Finish(); return; }
                        if (!IsInputTargetCurrent() || AnyInputModifierHeld())
                        {
                            report.Assert("synthetic_windows_key_target_precondition", false,
                                "Foreground, TextBox focus, or modifier state changed; no Windows key was sent.");
                            Finish(); return;
                        }
                        Input[] keys = new Input[] { VirtualKey(0x5B, false), VirtualKey(0x5B, true) };
                        uint sent = SendInput((uint)keys.Length, keys, Marshal.SizeOf(typeof(Input)));
                        report.Assert("synthetic_windows_key_events_accepted", sent == 2,
                            "sent=" + sent + "; expected=2; left-Win down/up; Win32Error=" + Marshal.GetLastWin32Error());
                        if (sent != 2)
                        {
                            if (sent == 1) SendInput(1, new Input[] { VirtualKey(0x5B, true) }, Marshal.SizeOf(typeof(Input)));
                            Finish(); return;
                        }
                        Advance(22);
                    }
                    else if (stage == 22 && age >= 400)
                    {
                        bool retained = IsInputTargetCurrent();
                        report.Assert("synthetic_windows_key_keeps_allowed_foreground", retained,
                            "foreground_hwnd=" + GetForegroundWindow().ToInt64() + "; expected_hwnd=" + allowedHandle.ToInt64());
                        if (!retained) { Finish(); return; }
                        string actual = ReadFixtureText(inputEdit);
                        bool textUnchanged = actual == inputBaseline + "Z";
                        report.Assert("windows_key_does_not_change_fixture_text", textUnchanged,
                            "observed_text=" + actual);
                        if (!textUnchanged) { Finish(); return; }
                        Rect rectangle;
                        bool rectOk = GetWindowRect(inputEdit, out rectangle);
                        Point target = new Point { x = rectangle.left + (rectangle.right - rectangle.left) / 2,
                            y = rectangle.top + (rectangle.bottom - rectangle.top) / 2 };
                        bool hitExact = rectOk && rectangle.right > rectangle.left && rectangle.bottom > rectangle.top
                            && WindowFromPoint(target) == inputEdit;
                        bool currentTarget = IsInputTargetCurrent();
                        report.Assert("synthetic_mouse_target_is_exact_fixture_textbox", hitExact && currentTarget,
                            "exact_textbox_hit=" + hitExact + "; allowed_foreground_and_focus=" + currentTarget
                            + "; hit_hwnd=" + WindowFromPoint(target).ToInt64() + "; expected_edit_hwnd=" + inputEdit.ToInt64());
                        if (!hitExact || !currentTarget) { Finish(); return; }
                        mouseCountBefore = ReadMouseCount(allowedHandle);
                        pointerSaved = GetCursorPos(out savedPointer);
                        bool mouseReady = pointerSaved && mouseCountBefore >= 0 && !AnyInputModifierHeld()
                            && !MouseButtonHeld();
                        report.Assert("synthetic_mouse_preconditions", mouseReady,
                            "pointer_saved=" + pointerSaved + "; fixture_mouse_down_count=" + mouseCountBefore
                            + "; no physical modifier/button may be held");
                        if (!mouseReady) { Finish(); return; }
                        Rectangle desktop = SystemInformation.VirtualScreen;
                        if (desktop.Width <= 1 || desktop.Height <= 1 || !desktop.Contains(target.x, target.y)
                            || !IsInputTargetCurrent() || WindowFromPoint(target) != inputEdit)
                        {
                            report.Assert("synthetic_mouse_target_unchanged", false,
                                "Target or desktop bounds changed before SendInput; no click was sent.");
                            Finish(); return;
                        }
                        int absoluteX = (int)Math.Round((target.x - desktop.Left) * 65535.0 / (desktop.Width - 1));
                        int absoluteY = (int)Math.Round((target.y - desktop.Top) * 65535.0 / (desktop.Height - 1));
                        Input[] pointerEvents = new Input[] {
                            MouseInputEvent(absoluteX, absoluteY, 0xC001),
                            MouseInputEvent(0, 0, 0x0002), MouseInputEvent(0, 0, 0x0004)
                        };
                        uint sent = SendInput((uint)pointerEvents.Length, pointerEvents, Marshal.SizeOf(typeof(Input)));
                        report.Assert("synthetic_mouse_events_accepted", sent == 3,
                            "sent=" + sent + "; expected=3; absolute-move/left-down/left-up; Win32Error=" + Marshal.GetLastWin32Error());
                        if (sent != 3)
                        {
                            if (sent >= 2) SendInput(1, new Input[] { MouseInputEvent(0, 0, 0x0004) }, Marshal.SizeOf(typeof(Input)));
                            Finish(); return;
                        }
                        Advance(23);
                    }
                    else if (stage == 23 && age >= 400)
                    {
                        int mouseCountAfter = ReadMouseCount(allowedHandle);
                        bool reached = mouseCountAfter == mouseCountBefore + 1;
                        report.Assert("synthetic_mouse_reaches_fixture_mousedown", reached,
                            "mouse_down_before=" + mouseCountBefore + "; mouse_down_after=" + mouseCountAfter);
                        bool targetStayed = IsInputTargetCurrent();
                        report.Assert("synthetic_mouse_keeps_allowed_foreground", targetStayed,
                            "foreground_hwnd=" + GetForegroundWindow().ToInt64() + "; expected_hwnd=" + allowedHandle.ToInt64());
                        RestorePointer();
                        if (!reached || !targetStayed) { Finish(); return; }
                        ShowWindowAsync(blockedHandle, 9);
                        Advance(2);
                    }
                    else if (stage == 2 && age >= 1200)
                    {
                        report.Assert("reappearing_unlisted_window_hidden_again", !IsWindowVisible(blockedHandle),
                            "visible_after_show=" + IsWindowVisible(blockedHandle));
                        report.Assert("guard_observed_a_block", guard.BlockedCount > 0, "blocked_count=" + guard.BlockedCount);
                        guard.Stop();
                        guardStarted = false;
                        status.Text = "锁定已结束\n正在核对原窗口恢复情况";
                        Advance(3);
                    }
                    else if (stage == 3 && age >= 700)
                    {
                        report.Assert("both_fixture_windows_restored", IsWindowVisible(allowedHandle) && IsWindowVisible(blockedHandle),
                            "allowed_visible=" + IsWindowVisible(allowedHandle) + "; blocked_visible=" + IsWindowVisible(blockedHandle));
                        WindowPlacement restoredAllowed = Placement(allowedHandle);
                        WindowPlacement restoredBlocked = Placement(blockedHandle);
                        report.Assert("fixture_window_placement_restored", SamePlacement(initialAllowed, restoredAllowed)
                            && SamePlacement(initialBlocked, restoredBlocked),
                            "allowed_showCmd=" + restoredAllowed.showCmd + "; blocked_showCmd=" + restoredBlocked.showCmd);
                        report.Assert("background_processes_survive_unlock", !allowedProcess.HasExited && !blockedProcess.HasExited,
                            "both_running=" + (!allowedProcess.HasExited && !blockedProcess.HasExited));
                        GuardEngine.RestoreJournal(journal);
                        report.Assert("journal_restore_is_repeatable", IsWindowVisible(allowedHandle) && IsWindowVisible(blockedHandle),
                            "Second restore did not hide either fixture.");
                        report.notices.Add("Only this run's two fixture processes are asked to close. All other processes are left running.");
                        report.notices.Add("An independent watchdog was ready before locking. This run does not simulate a parent-process crash.");
                        report.notices.Add("Keyboard and mouse checks use Windows SendInput synthetic events through the input/hook path. They are not physical-hardware tests.");
                        report.notices.Add("This run does not verify protected/elevated desktops, OS secure-attention keys, physical keyboard/mouse hardware, or multi-monitor hardware.");
                        Finish();
                    }
                }
                catch (Exception ex)
                {
                    report.Assert("integration_exception", false, ex.ToString());
                    Finish();
                }
            }

            private void Advance(int next) { stage = next; stageAt = elapsed.ElapsedMilliseconds; }

            private bool IsInputTargetCurrent()
            {
                if (GetForegroundWindow() != allowedHandle || inputEdit == IntPtr.Zero) return false;
                uint pid;
                uint threadId = GetWindowThreadProcessId(allowedHandle, out pid);
                var info = new GuiThreadInfo { cbSize = (uint)Marshal.SizeOf(typeof(GuiThreadInfo)) };
                return pid == (uint)allowedProcess.Id && GetGUIThreadInfo(threadId, ref info) && info.hwndFocus == inputEdit;
            }

            private bool ConfirmInputTarget(string checkName)
            {
                bool verified = IsInputTargetCurrent();
                report.Assert(checkName, verified,
                    "activate_request_accepted=" + activationAccepted + "; foreground_hwnd=" + GetForegroundWindow().ToInt64()
                    + "; expected_hwnd=" + allowedHandle.ToInt64() + "; dedicated_textbox_focused=" + verified);
                return verified;
            }

            private void RestorePointer()
            {
                if (!pointerSaved) return;
                Point restored;
                bool restoredOk = SetCursorPos(savedPointer.x, savedPointer.y) && GetCursorPos(out restored)
                    && restored.x == savedPointer.x && restored.y == savedPointer.y;
                pointerSaved = false;
                report.Assert("synthetic_mouse_pointer_restored", restoredOk,
                    "original_x=" + savedPointer.x + "; original_y=" + savedPointer.y + "; position_restored=" + restoredOk);
            }

            private void Finish()
            {
                if (finished) return;
                timer.Stop();
                RestorePointer();
                try
                {
                    if (guard != null)
                    {
                        guard.Stop();
                        guardStarted = false;
                        guard.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    report.Assert("guard_cleanup", false, ex.ToString());
                    try { if (!String.IsNullOrEmpty(journal)) GuardEngine.RestoreJournal(journal); }
                    catch (Exception recoveryError) { report.Assert("fallback_journal_restore", false, recoveryError.ToString()); }
                }
                SignalWatchdogStop();
                CloseFixture(allowedProcess, "allowed");
                CloseFixture(blockedProcess, "blocked");
                finished = true;
                BeginInvoke((Action)delegate { Close(); });
            }

            private void SignalWatchdogStop()
            {
                if (watchdogProcess == null) return;
                try
                {
                    File.WriteAllText(watchdogStopPath, DateTime.UtcNow.ToString("o"), new UTF8Encoding(false));
                    report.notices.Add("Watchdog stop signal written after window restoration; the process handle is disposed without terminating the process.");
                }
                catch (Exception ex) { report.Assert("watchdog_stop_signal", false, ex.ToString()); }
                finally
                {
                    watchdogProcess.Dispose();
                    watchdogProcess = null;
                }
            }

            private void CloseFixture(Process process, string label)
            {
                if (process == null) return;
                try
                {
                    process.Refresh();
                    if (!process.HasExited)
                    {
                        bool sent = process.CloseMainWindow();
                        report.notices.Add(label + " fixture close request sent=" + sent + "; it also self-closes after 60 seconds.");
                    }
                }
                catch (InvalidOperationException) { }
                catch (Exception ex) { report.notices.Add(label + " fixture close: " + ex.Message); }
                finally { process.Dispose(); }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    timer.Dispose();
                    RestorePointer();
                    if (guardStarted && guard != null)
                    {
                        try { guard.Stop(); } catch { }
                    }
                    SignalWatchdogStop();
                }
                base.Dispose(disposing);
            }
        }

        [StructLayout(LayoutKind.Sequential)] private struct Point { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct WindowPlacement
        {
            public int length, flags, showCmd;
            public Point minPosition, maxPosition;
            public Rect normalPosition;
        }
        [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
        {
            public uint cbSize, flags;
            public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
            public Rect rcCaret;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Input
        {
            public uint type;
            public InputUnion data;
        }
        [StructLayout(LayoutKind.Explicit)] private struct InputUnion
        {
            [FieldOffset(0)] public KeyboardInput keyboard;
            [FieldOffset(0)] public MouseInput mouse;
            [FieldOffset(0)] public HardwareInput hardware;
        }
        [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
        {
            public ushort virtualKey, scanCode;
            public uint flags, time;
            public UIntPtr extraInfo;
        }
        [StructLayout(LayoutKind.Sequential)] private struct MouseInput
        {
            public int dx, dy;
            public uint mouseData, flags, time;
            public UIntPtr extraInfo;
        }
        [StructLayout(LayoutKind.Sequential)] private struct HardwareInput
        {
            public uint message;
            public ushort parameterLow, parameterHigh;
        }
        private static Input UnicodeKey(char value, bool keyUp)
        {
            return new Input { type = 1, data = new InputUnion {
                keyboard = new KeyboardInput { scanCode = value, flags = keyUp ? 0x0006u : 0x0004u }
            } };
        }
        private static Input VirtualKey(ushort value, bool keyUp)
        {
            return new Input { type = 1, data = new InputUnion {
                keyboard = new KeyboardInput { virtualKey = value,
                    flags = ((value == 0x5B || value == 0x5C) ? 0x0001u : 0u) | (keyUp ? 0x0002u : 0u) }
            } };
        }
        private static Input MouseInputEvent(int x, int y, uint flags)
        {
            return new Input { type = 0, data = new InputUnion {
                mouse = new MouseInput { dx = x, dy = y, flags = flags }
            } };
        }
        private static bool AnyInputModifierHeld()
        {
            int[] modifiers = { 0x10, 0x11, 0x12, 0x5B, 0x5C };
            foreach (int key in modifiers) if ((GetAsyncKeyState(key) & 0x8000) != 0) return true;
            return false;
        }
        private static bool MouseButtonHeld()
        {
            int[] buttons = { 0x01, 0x02, 0x04, 0x05, 0x06 };
            foreach (int button in buttons) if ((GetAsyncKeyState(button) & 0x8000) != 0) return true;
            return false;
        }
        private static string ReadFixtureText(IntPtr edit)
        {
            var buffer = new StringBuilder(512);
            UIntPtr result;
            if (SendMessageBuffer(edit, 0x000D, new IntPtr(buffer.Capacity), buffer, 2, 500, out result) == IntPtr.Zero)
                throw new InvalidOperationException("Dedicated fixture text read timed out.");
            return buffer.ToString();
        }
        private static int ReadMouseCount(IntPtr window)
        {
            var title = new StringBuilder(512);
            GetWindowText(window, title, title.Capacity);
            const string marker = " · mouseDown=";
            string value = title.ToString();
            int position = value.LastIndexOf(marker, StringComparison.Ordinal);
            int count;
            return position >= 0 && Int32.TryParse(value.Substring(position + marker.Length), out count) ? count : -1;
        }
        private static WindowPlacement Placement(IntPtr handle)
        {
            var p = new WindowPlacement { length = Marshal.SizeOf(typeof(WindowPlacement)) };
            if (!GetWindowPlacement(handle, ref p)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return p;
        }
        private static bool SamePlacement(WindowPlacement a, WindowPlacement b)
        {
            return a.showCmd == b.showCmd && a.normalPosition.left == b.normalPosition.left && a.normalPosition.top == b.normalPosition.top
                && a.normalPosition.right == b.normalPosition.right && a.normalPosition.bottom == b.normalPosition.bottom;
        }
        private delegate bool EnumChildProc(IntPtr handle, IntPtr parameter);
        private static IntPtr FindFixtureEdit(IntPtr parent)
        {
            IntPtr found = IntPtr.Zero;
            EnumChildWindows(parent, delegate(IntPtr h, IntPtr parameter) {
                var className = new StringBuilder(256);
                GetClassName(h, className, className.Capacity);
                string name = className.ToString();
                if (String.Equals(name, "EDIT", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("WindowsForms10.EDIT.", StringComparison.OrdinalIgnoreCase))
                { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo information);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out Rect rectangle);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr handle, int command);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowPlacement(IntPtr handle, ref WindowPlacement placement);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder buffer, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder buffer, int count);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageValue(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageText(IntPtr handle, uint message, IntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageBuffer(IntPtr handle, uint message, IntPtr wParam, StringBuilder lParam, uint flags, uint timeout, out UIntPtr result);
    }
}