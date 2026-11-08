using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AiTime
{
    public sealed class SessionRunner
    {
        private readonly AppStore store;
        private readonly Form main;
        public SessionRunner(AppStore store, Form main) { this.store = store; this.main = main; }
        public void Begin(int seconds, bool isTest)
        {
            Begin(seconds, isTest, store.Config.Cycles, null, null);
        }
        /// <summary>Runs one locked session. Returns true when every focus round finished.</summary>
        public bool Begin(int seconds, bool isTest, int cycles, string[] cycleLabels, string subject)
        {
            if (seconds < 1 || seconds > 43200) throw new ArgumentOutOfRangeException("seconds");
            var allowed = store.Config.Allowed.Select(a => new AllowedApp { Name = a.Name, Path = a.Path, Arguments = a.Arguments }).ToList();
            foreach (AllowedApp a in allowed)
                if (!NativeWindows.IsAllowedExecutablePath(a.Path)) throw new FileNotFoundException("这个允许应用已不存在，请先更新白名单：" + a.Name, a.Path);
            var session = new SessionForm(store, allowed, seconds, isTest, isTest ? 1 : Math.Max(1, cycles), isTest ? 0 : store.Config.BreakMinutes * 60,
                String.IsNullOrWhiteSpace(subject) ? store.Config.Subject : subject, false, isTest ? null : cycleLabels);
            main.Hide();
            try { using (session) session.ShowDialog(); }
            finally { main.Show(); main.WindowState = FormWindowState.Normal; main.Activate(); }
            if (session.Failure != null) MessageBox.Show(main, session.Failure, "专注未能完成 · 窗口已恢复", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return session.Failure == null && session.Completed;
        }
    }
    internal sealed class SessionForm : Form
    {
        private readonly AppStore store;
        private readonly List<AllowedApp> apps;
        private readonly int duration, cycles, rest;
        private readonly bool test;
        private readonly bool automatedSmoke;
        private readonly string subject, id, journal, stopFile, readyFile;
        private readonly List<Form> covers = new List<Form>();
        private GuardEngine guard;
        private Process watchdog;
        private ActiveSession active;
        private System.Windows.Forms.Timer timer;
        private ulong phaseStart, lastSave, lastWindowRefresh;
        private int cycle = 1, phaseDuration;
        private bool isBreak, finishing, started;
        private DateTime phaseWall;
        private Label infoLabel, noticeLabel, countLabel;
        private FocusRing ring;
        private FlowLayoutPanel blockedFlow;
        private Pill roundPill;
        private readonly List<AppTile> tiles = new List<AppTile>();
        private bool previewMode, fadeDone, celebrating;
        private System.Windows.Forms.Timer prepTimer;
        private Stopwatch prepWatch;
        private string blockedSignature = "";
        private FlowLayoutPanel windowList;
        private SessionBadge badge;
        private readonly string[] cycleLabels;
        public string Failure;
        public bool Completed;
        public SessionForm(AppStore store, List<AllowedApp> apps, int seconds, bool test, int cycles, int rest, string subject, bool automatedSmoke = false, string[] cycleLabels = null)
        {
            this.automatedSmoke = automatedSmoke;
            this.cycleLabels = cycleLabels ?? new string[0];
            this.store = store; this.apps = apps; this.duration = seconds; this.test = test; this.cycles = Math.Max(1, cycles); this.rest = rest;
            this.subject = String.IsNullOrWhiteSpace(subject) ? "专注学习" : subject;
            id = Guid.NewGuid().ToString("N");
            string recovery = Path.Combine(store.Root, "recovery"); Directory.CreateDirectory(recovery);
            journal = Path.Combine(recovery, id + ".windows.json");
            stopFile = Path.Combine(recovery, id + ".stop"); readyFile = Path.Combine(recovery, id + ".ready");
            Text = "爱时间 · 专注锁定中"; FormBorderStyle = FormBorderStyle.None; BackColor = Theme.FocusBg; ForeColor = Theme.FocusText;
            StartPosition = FormStartPosition.Manual; Bounds = Screen.PrimaryScreen.Bounds; ShowInTaskbar = true; KeyPreview = true;
            BuildUi();
            // Paint the complete "preparing" screen first, then do the heavy work in short steps.
            if (!automatedSmoke) Opacity = 0.01; else fadeDone = true;
            Shown += delegate {
                if (previewMode) return;
                Refresh();
                // Fade the focus screen in while the recovery process starts; the guard waits for the fade.
                if (!fadeDone) Anim.Run(this, 260, delegate(float t) { Opacity = Math.Max(0.01, t); }, delegate { Opacity = 1; fadeDone = true; });
                BeginInvoke(new Action(StartSession));
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (!finishing && e.CloseReason != CloseReason.WindowsShutDown && e.CloseReason != CloseReason.TaskManagerClosing) { e.Cancel = true; return; }
                if (!finishing) Finish("系统结束", null);
            };
        }
        private static string Clock(int seconds)
        {
            return seconds >= 3600 ? TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss") : TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss");
        }
        private Label L(string text, float size, Color color, bool bold)
        { return new Label { Text = text, AutoSize = false, Font = Theme.Font(size, bold), ForeColor = color, BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleCenter }; }
        private string CycleLabel(int n)
        {
            return n >= 1 && n <= cycleLabels.Length && !String.IsNullOrWhiteSpace(cycleLabels[n - 1]) ? cycleLabels[n - 1] : null;
        }
        private static Control Centered(Control inner, int maxWidth)
        {
            // Keeps content in a readable centre column on wide screens.
            var holder = new Panel { Dock = DockStyle.Fill, BackColor = Theme.FocusBg, Margin = new Padding(0) };
            inner.Dock = DockStyle.None;
            holder.Controls.Add(inner);
            FlowLayoutPanel flow = inner as FlowLayoutPanel;
            Action relayout = delegate {
                int w = Math.Min(maxWidth, holder.ClientSize.Width);
                if (flow != null)
                {
                    // Shrink a row of tiles/chips to its content so it sits in the middle of the screen.
                    int content = flow.Padding.Horizontal + 2;
                    foreach (Control c in flow.Controls) content += c.Width + c.Margin.Horizontal;
                    w = Math.Min(w, content);
                }
                inner.SetBounds((holder.ClientSize.Width - w) / 2, 0, w, holder.ClientSize.Height);
            };
            holder.Resize += delegate { relayout(); };
            if (flow != null)
            {
                flow.ControlAdded += delegate { relayout(); };
                flow.ControlRemoved += delegate { relayout(); };
            }
            return holder;
        }
        private void BuildUi()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = Theme.S(48, 26, 48, 18), RowCount = 9, ColumnCount = 1, BackColor = Theme.FocusBg };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(58)));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            foreach (int h in new[] { 30, 34, 92, 30, 52, 40, 30 }) root.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(h)));

            // header: subject on the left, status pills on the right
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, BackColor = Theme.FocusBg, Margin = new Padding(0) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(46)));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(520)));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
            var lockIcon = new Label { Text = Glyph.Lock, Font = Theme.Icon(16), ForeColor = Theme.FocusAccent, BackColor = Theme.FocusBg, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };
            header.Controls.Add(lockIcon, 0, 0); header.SetRowSpan(lockIcon, 2);
            var title = L(subject, 14, Theme.FocusText, true); title.Dock = DockStyle.Fill; title.TextAlign = ContentAlignment.BottomLeft; title.AutoEllipsis = true;
            header.Controls.Add(title, 1, 0);
            var sub = L("爱时间 · 专注模式", 9, Theme.FocusMuted, false); sub.Dock = DockStyle.Fill; sub.TextAlign = ContentAlignment.TopLeft;
            header.Controls.Add(sub, 1, 1);
            var pills = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Theme.FocusBg, Margin = new Padding(0), Padding = Theme.S(0, 8, 0, 0) };
            roundPill = new Pill(cycles > 1 ? "第 1 / " + cycles + " 轮" : "单轮专注", Theme.FocusText, Theme.FocusCard, Glyph.Stopwatch) { Margin = Theme.S(8, 0, 0, 0) };
            pills.Controls.Add(roundPill);
            pills.Controls.Add(new Pill(apps.Count + " 个应用可用", Theme.FocusGreen, Color.FromArgb(20, 83, 45), Glyph.Shield) { Margin = Theme.S(8, 0, 0, 0) });
            if (test) pills.Controls.Add(new Pill("试锁 · 不计入统计", Color.FromArgb(253, 230, 138), Color.FromArgb(66, 52, 16), Glyph.Info) { Margin = Theme.S(8, 0, 0, 0) });
            header.Controls.Add(pills, 2, 0); header.SetRowSpan(pills, 2);
            root.Controls.Add(header, 0, 0);

            ring = new FocusRing { Dock = DockStyle.Fill, TimeText = Clock(duration), Caption = "正在准备专注环境",
                SubCaption = "启动恢复进程与窗口守卫 · 计时尚未开始" };
            root.Controls.Add(Centered(ring, Theme.S(560)), 0, 1);

            infoLabel = L("计时开始后，允许范围、时长和轮数固定", 9.5f, Theme.FocusMuted, false); infoLabel.Dock = DockStyle.Fill; root.Controls.Add(infoLabel, 0, 2);
            var appsCaption = L("可以使用  ·  点击打开或切换，也可以用 Alt+Tab 或右下角计时条", 9.5f, Theme.FocusGreen, true); appsCaption.Dock = DockStyle.Fill; appsCaption.TextAlign = ContentAlignment.BottomCenter; root.Controls.Add(appsCaption, 0, 3);
            int tileW = Theme.S(236), tileH = Theme.S(80);
            var launches = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = false, Padding = new Padding(0), WrapContents = false, BackColor = Theme.FocusBg };
            foreach (AllowedApp a in apps)
            {
                AllowedApp current = a;
                var tile = new AppTile { ExePath = a.Path, Title = ShortName(a), Size = new Size(tileW, tileH), Margin = Theme.S(6, 4, 6, 4) };
                tile.Click += delegate { Launch(current); };
                tiles.Add(tile);
                launches.Controls.Add(tile);
            }
            if (apps.Count == 0) { var empty = L("本轮未选择应用，可进行纸笔学习。", 11, Theme.FocusMuted, false); empty.Width = Theme.S(560); empty.Height = Theme.S(48); launches.Controls.Add(empty); }
            int launchesWidth = Math.Max(Theme.S(560), Math.Min(apps.Count, 5) * (tileW + Theme.S(12)));
            root.Controls.Add(Centered(launches, launchesWidth), 0, 4);
            countLabel = L("已打开的窗口", 9, Theme.FocusMuted, true); countLabel.Dock = DockStyle.Fill; countLabel.TextAlign = ContentAlignment.BottomCenter; root.Controls.Add(countLabel, 0, 5);
            windowList = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = false, WrapContents = false, Padding = Theme.S(0, 4, 0, 0), BackColor = Theme.FocusBg };
            root.Controls.Add(Centered(windowList, Theme.S(1080)), 0, 6);
            blockedFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = Theme.S(0, 6, 0, 0), BackColor = Theme.FocusBg };
            root.Controls.Add(Centered(blockedFlow, Theme.S(1080)), 0, 7);
            UpdateBlocked(new string[0]);
            noticeLabel = L("输入法、剪贴板始终可用（Shift / Ctrl+Space / Win+Space 切换）· 计时不受改系统时间影响 · 睡眠期间不累计", 8.5f, Theme.FocusMuted, false); noticeLabel.Dock = DockStyle.Fill; root.Controls.Add(noticeLabel, 0, 8);
            Controls.Add(root);
        }
        private void UpdateBlocked(string[] blocked)
        {
            blockedFlow.SuspendLayout();
            while (blockedFlow.Controls.Count > 0) blockedFlow.Controls[0].Dispose();
            if (blocked.Length == 0)
                blockedFlow.Controls.Add(new Pill("其他窗口会被隐藏，程序在后台继续运行，结束后自动恢复", Theme.FocusMuted, Theme.FocusCard, Glyph.Hide));
            else
            {
                blockedFlow.Controls.Add(new Pill("已隐藏 · 后台运行", Theme.FocusDanger, Color.FromArgb(69, 26, 32), Glyph.Hide) { Margin = Theme.S(0, 0, 8, 0) });
                foreach (string name in blocked.Take(8))
                    blockedFlow.Controls.Add(new Pill(Path.GetFileNameWithoutExtension(name), Color.FromArgb(254, 202, 202), Theme.FocusCard) { Margin = Theme.S(0, 0, 6, 0) });
                if (blocked.Length > 8) blockedFlow.Controls.Add(new Pill("+" + (blocked.Length - 8), Theme.FocusMuted, Theme.FocusCard));
            }
            blockedFlow.ResumeLayout();
        }
        /// <summary>Renders the focus screen without starting a session (used by --snapshot).</summary>
        internal static void RenderPreview(AppStore store, string png, bool preparing)
        {
            var list = store.Config.Allowed.Select(a => new AllowedApp { Name = a.Name, Path = a.Path, Arguments = a.Arguments }).ToList();
            using (var f = new SessionForm(store, list, 60 * 60, false, 3, 300, "周日复盘与补强", false, new[] { "知识学习", "实操 1 / 2", "实操 2 / 2" }))
            {
                f.previewMode = true; f.finishing = true; f.Opacity = 1;
                f.StartPosition = FormStartPosition.Manual;
                f.Bounds = new Rectangle(-30000, -30000, Theme.S(1536), Theme.S(864));
                if (!preparing)
                {
                    f.ring.Progress = delegate { return 0.38f; };
                    f.ring.TimeText = "37:12"; f.ring.Caption = "知识学习"; f.ring.SubCaption = "第 1 / 3 轮 · 结束后自动恢复所有窗口";
                    f.infoLabel.Text = "本轮范围已固定 · 允许应用、它们安装目录里的组件、由它们启动的 Python / 终端可用";
                    f.UpdateBlocked(new[] { "msedge.exe", "WeChat.exe", "Codex.exe" });
                    if (f.tiles.Count > 0) f.tiles[0].Running = true;
                    var chip = Theme.Button("day2.py – PyCharm"); chip.Kind = ButtonKind.Dark; chip.Glyph = Glyph.Switch; chip.Font = Theme.F(9.5f, true);
                    chip.Size = new Size(Theme.S(300), Theme.S(40)); chip.Margin = Theme.S(6, 0, 6, 0); f.windowList.Controls.Add(chip);
                }
                f.Show();
                Application.DoEvents();
                using (Bitmap bmp = new Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.Size));
                    bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                }
                f.Close();
            }
        }        private static string ShortName(AllowedApp a)
        {
            string n = String.IsNullOrWhiteSpace(a.Name) ? Path.GetFileNameWithoutExtension(a.Path) : a.Name;
            return n.Length > 20 ? n.Substring(0, 19) + "…" : n;
        }
        private void StartSession()
        {
            try
            {
                if (automatedSmoke)
                {
                    string preparation;
                    if (!TestSuite.TryPrepareForeground(this, out preparation)) throw new InvalidOperationException("自动验证无法安全取得专注窗口前台：" + preparation);
                }
                active = new ActiveSession { Id = id, ParentPid = Process.GetCurrentProcess().Id, ParentStartTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
                    Subject = subject, JournalPath = journal, StopPath = stopFile, IsTest = test, Cycles = cycles };
                StartPhase(false);
                active.HeartbeatTick = 0; JsonFile.Write(store.ActivePath, active);
                watchdog = Program.StartWatchdog(active.ParentPid, active.ParentStartTicks, journal, stopFile, readyFile, store.ActivePath);
                // Wait for the recovery process without blocking the UI thread.
                prepWatch = Stopwatch.StartNew();
                prepTimer = new System.Windows.Forms.Timer { Interval = 40 };
                prepTimer.Tick += PrepTick;
                prepTimer.Start();
            }
            catch (OperationCanceledException) { Finish("已取消", null); }
            catch (Exception ex) { StartFailed(ex); }
        }
        private void StopPrep()
        {
            if (prepTimer == null) return;
            prepTimer.Stop(); prepTimer.Dispose(); prepTimer = null;
        }
        private void PrepTick(object sender, EventArgs e)
        {
            if (finishing) { StopPrep(); return; }
            try
            {
                if (File.Exists(readyFile) && fadeDone) { StopPrep(); StartGuard(); return; }
                if (watchdog.HasExited) throw new InvalidOperationException("恢复进程无法启动，本轮未启用锁定。");
                if (prepWatch.ElapsedMilliseconds >= 4000) throw new InvalidOperationException("恢复进程未就绪，本轮未启用锁定。");
            }
            catch (Exception ex) { StopPrep(); StartFailed(ex); }
        }
        private void StartGuard()
        {
            try
            {
                AddCovers(); SystemEvents.DisplaySettingsChanged += DisplaysChanged;
                guard = new GuardEngine(this, apps.Select(a => a.Path), journal, id);
                guard.Notice += delegate(string message) { if (!IsDisposed && noticeLabel != null) { noticeLabel.Text = message; noticeLabel.ForeColor = Theme.FocusAccent; } };
                guard.Start(); StartPhase(false); started = true;
                NativeWindows.SetThreadExecutionState(0x80000003); // process-scoped request only; no power policy changes
                ring.Progress = delegate { return phaseDuration <= 0 ? 1f : Math.Min(1f, (NativeClock.NowMilliseconds - phaseStart) / (phaseDuration * 1000f)); };
                infoLabel.Text = "本轮范围已固定 · 允许应用、它们安装目录里的组件、由它们启动的 Python/终端可用 · 时间到自动恢复";
                badge = new SessionBadge(delegate { Show(); Activate(); RefreshWindows(); }, BuildSwitchMenu); badge.Show();
                timer = new System.Windows.Forms.Timer { Interval = 200 }; timer.Tick += Tick; timer.Start();
                Tick(this, EventArgs.Empty);
            }
            catch (OperationCanceledException) { Finish("已取消", null); }
            catch (Exception ex) { StartFailed(ex); }
        }
        private void StartFailed(Exception ex)
        {
            try { File.AppendAllText(Path.Combine(store.Root, "diagnostics.log"), DateTime.Now.ToString("s") + " " + ex.ToString() + Environment.NewLine); } catch (IOException) { }
            string[] lines = ex.Message.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Finish("启动失败", lines.Length == 0 ? "无法开始本轮专注。" : lines[0]);
        }
        /// <summary>Quick switcher shown from the floating timer: open windows, launchers, back to the timer.</summary>
        private ContextMenuStrip BuildSwitchMenu()
        {
            var menu = new ContextMenuStrip { Font = Theme.Font(10), ShowImageMargin = false };
            menu.Items.Add(new ToolStripLabel("已打开的窗口") { ForeColor = Theme.Muted, Font = Theme.Font(8.5f, true) });
            IList<AllowedWindow> windows = guard == null ? new List<AllowedWindow>() : guard.GetAllowedWindows();
            foreach (AllowedWindow w in windows)
            {
                long handle = w.Handle;
                string title = w.Title.Length > 48 ? w.Title.Substring(0, 47) + "…" : w.Title;
                menu.Items.Add(title, null, delegate { if (guard != null) guard.ActivateWindow(handle); });
            }
            if (windows.Count == 0) menu.Items.Add(new ToolStripMenuItem("（还没有打开的允许窗口）") { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripLabel("打开应用") { ForeColor = Theme.Muted, Font = Theme.Font(8.5f, true) });
            foreach (AllowedApp a in apps)
            {
                AllowedApp current = a;
                menu.Items.Add("打开 " + ShortName(a), null, delegate { Launch(current); });
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("回到计时面板", null, delegate { Show(); Activate(); RefreshWindows(); });
            menu.Closed += delegate { BeginInvoke(new Action(menu.Dispose)); };
            return menu;
        }        private void StartPhase(bool nextBreak)
        {
            isBreak = nextBreak; phaseDuration = isBreak ? rest : duration; phaseStart = NativeClock.NowMilliseconds; phaseWall = DateTime.Now;
            active.IsBreak = isBreak; active.Cycle = cycle; active.PlannedSeconds = phaseDuration; active.StartedAt = phaseWall;
            active.LastHeartbeat = phaseWall; active.HeartbeatTick = phaseStart; active.ActualSeconds = 0; JsonFile.Write(store.ActivePath, active); lastSave = phaseStart;
        }
        private void Tick(object sender, EventArgs e)
        {
            if (finishing || celebrating) return;
            try
            {
                if (guard != null && !guard.IsRunning) { Finish("异常中断", "窗口守卫已停止，本轮已结束并恢复窗口。"); return; }
                ulong now = NativeClock.NowMilliseconds;
                int elapsed = (int)Math.Min((ulong)phaseDuration, (now - phaseStart) / 1000);
                int remaining = Math.Max(0, phaseDuration - elapsed);
                string clock = Clock(remaining);
                string label = CycleLabel(cycle);
                string next = CycleLabel(cycle + 1);
                float fraction = phaseDuration <= 0 ? 1f : Math.Min(1f, (now - phaseStart) / (phaseDuration * 1000f));
                ring.IsBreak = isBreak;
                ring.TimeText = clock;
                ring.Caption = (test ? "试锁 · " : "") + (isBreak ? "休息一下" : label ?? "专注进行中");
                ring.SubCaption = isBreak ? (next == null ? "休息结束后继续" : "下一轮：" + next)
                    : (cycles > 1 ? "第 " + cycle + " / " + cycles + " 轮 · " : "") + "结束后自动恢复所有窗口";
                string round = cycles > 1 ? "第 " + cycle + " / " + cycles + " 轮" : "单轮专注";
                if (roundPill.Text != round) { roundPill.Text = round; roundPill.Invalidate(); }
                if (badge != null) { badge.UpdateClock(clock, isBreak, test); badge.SetProgress(fraction); }
                string[] blocked = guard == null ? new string[0] : guard.BlockedApps;
                string signature = String.Join(",", blocked);
                if (signature != blockedSignature)
                {
                    blockedSignature = signature;
                    UpdateBlocked(blocked);
                }
                if (now - lastSave >= 5000) { active.ActualSeconds = elapsed; active.LastHeartbeat = DateTime.Now; active.HeartbeatTick = now; JsonFile.Write(store.ActivePath, active); lastSave = now; }
                if (now - lastWindowRefresh >= 2500) { RefreshWindows(); lastWindowRefresh = now; }
                if (remaining != 0) return;
                if (!isBreak) { Record("已完成", phaseDuration); ring.Celebrate(); }
                if (!isBreak && cycle >= cycles)
                {
                    // Let the finishing burst play before the windows come back.
                    Completed = true; celebrating = true;
                    ring.Caption = "完成！"; ring.TimeText = "00:00"; ring.SubCaption = "做得好 · 正在恢复所有窗口";
                    if (badge != null) badge.UpdateClock("完成", false, test);
                    Anim.Run(this, 1700, null, delegate { Finish("已完成", null); }, true);
                    return;
                }
                if (isBreak) { cycle++; StartPhase(false); }
                else if (rest > 0) StartPhase(true);
                else { cycle++; StartPhase(false); }
            }
            catch (Exception ex) { Finish("异常中断", ex.Message); }
        }
        private void Record(string status, int elapsed)
        {
            string label = CycleLabel(cycle);
            store.AddRecord(new FocusRecord { Id = id + "-" + cycle, Subject = label == null ? subject : subject + " · " + label, Status = status, StartedAt = phaseWall, EndedAt = DateTime.Now,
                PlannedSeconds = duration, ActualSeconds = Math.Min(duration, Math.Max(0, elapsed)), IsTest = test });
        }
        private void RefreshWindows()
        {
            if (guard == null || finishing) return;
            IList<AllowedWindow> windows = guard.GetAllowedWindows();
            foreach (AppTile tile in tiles)
            {
                bool running = windows.Any(w => String.Equals(NativeWindows.NormalizedPath(w.Path), NativeWindows.NormalizedPath(tile.ExePath), StringComparison.OrdinalIgnoreCase));
                if (tile.Running != running) { tile.Running = running; tile.Invalidate(); }
            }
            string signature = String.Join("|", windows.Select(w => w.Handle + ":" + w.Title));
            if ((string)windowList.Tag == signature) return;
            windowList.Tag = signature;
            windowList.SuspendLayout();
            while (windowList.Controls.Count > 0) windowList.Controls[0].Dispose();
            foreach (AllowedWindow w in windows)
            {
                long handle = w.Handle;
                var b = Theme.Button(w.Title.Length > 34 ? w.Title.Substring(0, 33) + "…" : w.Title, false);
                b.Kind = ButtonKind.Dark; b.Glyph = Glyph.Switch; b.Font = Theme.F(9.5f, true);
                b.Size = new Size(Theme.S(300), Theme.S(40)); b.Margin = Theme.S(6, 0, 6, 0); b.Click += delegate { guard.ActivateWindow(handle); }; windowList.Controls.Add(b);
            }
            windowList.ResumeLayout();
        }
        private void Launch(AllowedApp app)
        {
            try
            {
                AllowedWindow window = guard.GetAllowedWindows().FirstOrDefault(w => String.Equals(NativeWindows.NormalizedPath(w.Path), NativeWindows.NormalizedPath(app.Path), StringComparison.OrdinalIgnoreCase));
                if (window != null && guard.ActivateWindow(window.Handle)) return;
                if (!NativeWindows.IsAllowedExecutablePath(app.Path)) throw new FileNotFoundException("应用路径失效，请在本轮结束后更新。", app.Path);
                if (IsConsoleProgram(app.Path))
                {
                    // Windows Terminal would host the console in a window that is not part of this session.
                    // A classic console started by AiTime stays usable, in today's task folder.
                    string conhost = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "conhost.exe");
                    string workDir = TaskFolder() ?? Path.GetDirectoryName(app.Path);
                    Process.Start(new ProcessStartInfo(conhost, NativeWindows.QuoteArg(app.Path) + (String.IsNullOrWhiteSpace(app.Arguments) ? "" : " " + app.Arguments)) { UseShellExecute = false, WorkingDirectory = workDir });
                    noticeLabel.Text = "已在 " + workDir + " 打开 " + ShortName(app) + " 控制台。";
                    return;
                }
                Process.Start(new ProcessStartInfo(app.Path, app.Arguments ?? "") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(app.Path) });
                noticeLabel.Text = "已打开 " + ShortName(app) + "；如应用另有主程序，请在下轮将实际窗口的 exe 加入白名单。";
            }
            catch (Exception ex) { noticeLabel.Text = "打开失败：" + ex.Message; }
        }
        private string TaskFolder()
        {
            try
            {
                StudyPlan plan = StudyPlanner.Today(store.Config.LearningDay);
                return plan.HasTaskFile ? Path.GetDirectoryName(plan.TaskFile) : null;
            }
            catch (Exception) { return null; }
        }
        /// <summary>True for console-subsystem exes (python.exe, gcc.exe ...), read from the PE header.</summary>
        internal static bool IsConsoleProgram(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new BinaryReader(stream))
                {
                    if (reader.ReadUInt16() != 0x5A4D) return false;
                    stream.Position = 0x3C;
                    int pe = reader.ReadInt32();
                    if (pe <= 0 || pe > stream.Length - 0x60) return false;
                    stream.Position = pe;
                    if (reader.ReadUInt32() != 0x00004550) return false;
                    stream.Position = pe + 0x18 + 0x44; // optional header Subsystem (same offset for PE32 and PE32+)
                    return reader.ReadUInt16() == 3;
                }
            }
            catch (Exception) { return false; }
        }
        private void AddCovers()
        {
            foreach (Form c in covers) c.Dispose(); covers.Clear();
            Bounds = Screen.PrimaryScreen.Bounds;
            foreach (Screen screen in Screen.AllScreens)
            {
                if (screen.Primary) continue;
                var cover = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = screen.Bounds,
                    BackColor = Theme.FocusBg, ShowInTaskbar = false, Text = "爱时间 · 专注背景" };
                var l = L("专注进行中\n\n此屏幕可显示本轮允许的应用", 18, Theme.FocusMuted, false); l.Dock = DockStyle.Fill; cover.Controls.Add(l);
                cover.FormClosing += delegate(object s, FormClosingEventArgs e) { if (!finishing) e.Cancel = true; };
                cover.Show(); cover.SendToBack(); covers.Add(cover);
            }
        }
        private void DisplaysChanged(object sender, EventArgs e)
        { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(delegate { if (!finishing) { AddCovers(); if (badge != null) badge.Place(); } })); }
        private void Finish(string status, string error)
        {
            if (finishing) return; finishing = true; Failure = error;
            StopPrep();
            if (timer != null) { timer.Stop(); timer.Dispose(); }
            SystemEvents.DisplaySettingsChanged -= DisplaysChanged;
            try
            {
                if (started && !isBreak && status != "已完成") Record(status, (int)Math.Min((ulong)duration, (NativeClock.NowMilliseconds - phaseStart) / 1000));
            }
            catch (Exception ex) { Failure = (Failure ?? "") + "\n保存记录失败：" + ex.Message; }
            try { if (guard != null) guard.Stop(); }
            catch (Exception ex) { Failure = (Failure ?? "") + "\n窗口恢复需要辅助进程处理：" + ex.Message; }
            finally
            {
                try { File.WriteAllText(stopFile, "finished"); } catch (Exception ex) { Failure = (Failure ?? "") + "\n恢复信号写入失败：" + ex.Message; }
                NativeWindows.SetThreadExecutionState(0x80000000);
                if (badge != null) { badge.AllowClose = true; badge.Close(); badge.Dispose(); }
                foreach (Form c in covers) c.Dispose(); covers.Clear();
                try { JsonFile.Write(store.ActivePath, null); } catch (Exception ex) { Failure = (Failure ?? "") + "\n会话状态未保存：" + ex.Message; }
                Close();
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { if (!finishing) Finish("异常中断", "专注窗口提前结束。"); if (guard != null) guard.Dispose(); if (watchdog != null) watchdog.Dispose(); }
            base.Dispose(disposing);
        }
    }
    internal sealed class SessionBadge : Form
    {
        private readonly Label clock;
        private static Point? remembered;
        private float fraction;
        private bool rest;
        public bool AllowClose;
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        public SessionBadge(Action open, Func<ContextMenuStrip> buildMenu)
        {
            Text = "爱时间 · 专注计时"; BackColor = Theme.FocusCard; ForeColor = Theme.FocusText; FormBorderStyle = FormBorderStyle.None; DoubleBuffered = true;
            ShowInTaskbar = false; TopMost = true; Size = new Size(Theme.S(264), Theme.S(56)); StartPosition = FormStartPosition.Manual; Padding = Theme.S(8, 7, 8, 11);
            using (GraphicsPath shape = Theme.RoundRect(new RectangleF(0, 0, Width, Height), Theme.S(16))) Region = new Region(shape);
            clock = new Label { Dock = DockStyle.Left, Width = Theme.S(118), TextAlign = ContentAlignment.MiddleCenter, Font = Theme.Num(15), ForeColor = Theme.FocusText, BackColor = Theme.FocusCard, Cursor = Cursors.SizeAll };
            Controls.Add(clock);
            var b = Theme.Button("切换应用", false); b.Kind = ButtonKind.Dark; b.Glyph = Glyph.Switch; b.Dock = DockStyle.Fill; b.Font = Theme.F(9.5f, true);
            b.Click += delegate {
                ContextMenuStrip menu = buildMenu != null ? buildMenu() : null;
                if (menu == null) { open(); return; }
                menu.Show(b, new Point(0, 0), ToolStripDropDownDirection.AboveRight);
            };
            Controls.Add(b); b.BringToFront();
            // Drag by the clock; double-click the clock to open the full timer screen.
            clock.MouseDown += delegate(object s, MouseEventArgs e) {
                if (e.Button != MouseButtons.Left) return;
                ReleaseCapture(); SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero);
                remembered = Location;
            };
            clock.DoubleClick += delegate { open(); };
            FormClosing += delegate(object s, FormClosingEventArgs e) { if (!AllowClose && e.CloseReason != CloseReason.WindowsShutDown) e.Cancel = true; };
            Place();
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams { get { CreateParams p = base.CreateParams; p.ExStyle |= 0x80; return p; } }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath border = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Theme.S(16)))
            using (Pen pen = new Pen(Theme.FocusStroke)) g.DrawPath(pen, border);
            float h = Theme.S(3), w = Width - Theme.S(32);
            RectangleF track = new RectangleF(Theme.S(16), Height - Theme.S(7), w, h);
            using (GraphicsPath p = Theme.RoundRect(track, h / 2)) using (SolidBrush b = new SolidBrush(Color.FromArgb(40, 54, 80))) g.FillPath(b, p);
            if (fraction > 0.003f)
                using (GraphicsPath p = Theme.RoundRect(new RectangleF(track.X, track.Y, Math.Max(h, w * fraction), h), h / 2))
                using (SolidBrush b = new SolidBrush(rest ? Theme.FocusGreen : Theme.FocusAccent)) g.FillPath(b, p);
        }
        public void Place()
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            if (remembered.HasValue && area.Contains(new Rectangle(remembered.Value, Size))) { Location = remembered.Value; return; }
            // Bottom-right above the taskbar: away from title-bar buttons and IDE toolbars.
            Location = new Point(area.Right - Width - Theme.S(16), area.Bottom - Height - Theme.S(36));
        }
        public void UpdateClock(string value, bool isRest, bool test)
        {
            if (clock.Text != value) clock.Text = value;
            clock.ForeColor = isRest ? Theme.FocusGreen : Theme.FocusText;
            rest = isRest;
        }
        public void SetProgress(float value)
        {
            float v = Math.Max(0, Math.Min(1, value));
            if (Math.Abs(v - fraction) < 0.002f) return;
            fraction = v;
            Invalidate(new Rectangle(0, Height - Theme.S(10), Width, Theme.S(10)));
        }
    }    internal sealed class ProgressStrip : Control
    {
        private float value;
        private bool rest;
        public ProgressStrip()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        public void SetValue(float v, bool isRest)
        {
            v = Math.Max(0, Math.Min(1, v));
            if (Math.Abs(v - value) < 0.001f && isRest == rest) return;
            value = v; rest = isRest; Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Bg);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float h = Math.Max(4, Math.Min(Height, Theme.S(8)));
            var track = new RectangleF(0, (Height - h) / 2, Width - 1, h);
            using (var p = Theme.RoundRect(track, h / 2)) using (var b = new SolidBrush(Color.FromArgb(226, 232, 239))) e.Graphics.FillPath(b, p);
            if (value <= 0) return;
            var fill = new RectangleF(track.X, track.Y, Math.Max(h, track.Width * value), h);
            using (var p = Theme.RoundRect(fill, h / 2)) using (var b = new SolidBrush(rest ? Theme.Green : Theme.Accent)) e.Graphics.FillPath(b, p);
        }
    }
}