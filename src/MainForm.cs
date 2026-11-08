using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AiTime
{
    public sealed class MainForm : Form
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "AiTimeWindows";
        private const string ReviewKey = "review", NewKey = "new", ExtKey = "ext";

        private readonly AppStore store;
        private readonly Panel host;
        private readonly Label pageTitle;
        private readonly Label pageSubtitle;
        private readonly Label sideMinutes, sideGoal, sideReminder;
        private readonly List<RoundButton> navButtons = new List<RoundButton>();
        private readonly NavList nav;
        private readonly MiniBar sideBar;
        private readonly Panel contentPanel;
        private readonly ToolTip tips = new ToolTip { AutoPopDelay = 20000, InitialDelay = 350, ReshowDelay = 100 };
        private readonly NotifyIcon tray;
        private readonly System.Windows.Forms.Timer clock;
        private readonly bool snapshotMode;
        private string currentPage = "today";
        private StudyPlan currentPlan;
        private Control pageBody;
        private int pageMinHeight;
        private Label heroStatus, heroProgress;
        private bool sessionRunning, allowExit, startHidden;

        public MainForm(AppStore store) : this(store, false, false) { }

        public MainForm(AppStore store, bool startHidden, bool snapshotMode = false)
        {
            this.store = store;
            this.startHidden = startHidden;
            this.snapshotMode = snapshotMode;
            Ambient.Enabled = store.Config.Effects;
            currentPlan = StudyPlanner.Today(store.Config.LearningDay);
            EnsureTodayProfile();

            Text = "爱时间 · Windows";
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
            AutoScaleMode = AutoScaleMode.None;
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(Theme.S(1180), work.Width - 40), Math.Min(Theme.S(780), work.Height - 40));
            MinimumSize = new Size(Math.Min(Theme.S(1000), work.Width - 40), Math.Min(Theme.S(660), work.Height - 40));
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Font(10);
            DoubleBuffered = true;

            TableLayoutPanel root = new TableLayoutPanel {
                Dock = DockStyle.Fill, RowCount = 1, ColumnCount = 2,
                BackColor = Theme.Bg, Margin = new Padding(0), Padding = new Padding(0)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(228)));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);

            // ---- sidebar
            SidePanel side = new SidePanel { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = Theme.S(14, 20, 14, 16) };
            TableLayoutPanel sideRows = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Theme.Surface, Margin = new Padding(0), Padding = new Padding(0) };
            sideRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int h in new[] { 70, 22, 18 }) sideRows.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(h)));
            sideRows.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(44) * 4));
            sideRows.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            sideRows.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(170)));
            side.Controls.Add(sideRows);
            sideRows.Controls.Add(new BrandMark { Dock = DockStyle.Fill, Margin = new Padding(0) }, 0, 0);
            Label section = FillLabel("工作台", 8, true, Color.FromArgb(148, 163, 184));
            section.Padding = Theme.S(14, 0, 0, 0);
            sideRows.Controls.Add(section, 0, 2);
            nav = new NavList { Dock = DockStyle.Fill, Margin = new Padding(0) };
            nav.Add("today", "今天", Glyph.Home);
            nav.Add("plan", "编程计划", Glyph.Calendar);
            nav.Add("tasks", "待办", Glyph.Tasks);
            nav.Add("apps", "应用范围", Glyph.Shield);
            nav.SelectedChanged += delegate(string key) { ShowPage(key); };
            sideRows.Controls.Add(nav, 0, 3);

            RoundPanel summary = new RoundPanel { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = Theme.S(16, 14, 16, 14), FillColor = Theme.HeroA, FillColor2 = Theme.HeroB, GradientAngle = 35f, Radius = Theme.S(14) };
            TableLayoutPanel sumRows = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Color.Transparent, Margin = new Padding(0) };
            sumRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int h in new[] { 20, 40, 14, 22 }) sumRows.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(h)));
            sumRows.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            sumRows.Controls.Add(FillLabel("今日已专注", 8.5f, false, Theme.OnHero), 0, 0);
            sideMinutes = FillLabel("0 分钟", 10, false, Color.White); sideMinutes.Font = Theme.Num(19); sumRows.Controls.Add(sideMinutes, 0, 1);
            sideBar = new MiniBar { Dock = DockStyle.Fill, Margin = Theme.S(0, 3, 0, 3), BarColor = Color.White, TrackColor = Color.FromArgb(70, 255, 255, 255), Shimmer = true };
            sumRows.Controls.Add(sideBar, 0, 2);
            sideGoal = FillLabel("", 8.5f, false, Theme.OnHero); sumRows.Controls.Add(sideGoal, 0, 3);
            sideReminder = FillLabel("", 8.5f, true, Color.White); sideReminder.TextAlign = ContentAlignment.TopLeft; sumRows.Controls.Add(sideReminder, 0, 4);
            summary.Controls.Add(sumRows);
            sideRows.Controls.Add(summary, 0, 5);
            root.Controls.Add(side, 0, 0);

            // ---- content
            Panel content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = Theme.S(30, 22, 30, 22), Margin = new Padding(0) };
            contentPanel = content;
            Panel heading = new Panel { Dock = DockStyle.Top, Height = Theme.S(78), BackColor = Theme.Bg };
            pageTitle = Theme.Label("今天", 19, true);
            pageTitle.SetBounds(0, 0, Theme.S(700), Theme.S(42));
            pageSubtitle = Theme.Label("", 9.5f, false, Theme.Muted);
            pageSubtitle.SetBounds(Theme.S(1), Theme.S(42), Theme.S(1000), Theme.S(24));
            RoundButton quick = Theme.Button("开始专注", true);
            quick.Glyph = Glyph.Play;
            quick.Shimmer = true;
            quick.Size = new Size(Theme.S(124), Theme.S(40));
            quick.Click += delegate { StartPlan(currentPlan, false, false); };
            tips.SetToolTip(quick, "按今天的计划开始锁定专注");
            RoundButton folder = Theme.Button("");
            folder.Glyph = Glyph.Folder;
            folder.Size = new Size(Theme.S(40), Theme.S(40));
            folder.Click += delegate { TryAction(delegate { OpenTaskFile(currentPlan); }); };
            tips.SetToolTip(folder, "打开今天的题目");
            heading.Controls.Add(pageTitle);
            heading.Controls.Add(pageSubtitle);
            heading.Controls.Add(quick);
            heading.Controls.Add(folder);
            heading.Resize += delegate {
                int w = heading.ClientSize.Width;
                quick.Location = new Point(w - quick.Width, Theme.S(6));
                folder.Location = new Point(quick.Left - folder.Width - Theme.S(8), Theme.S(6));
                pageTitle.Width = Math.Max(Theme.S(200), folder.Left - Theme.S(16));
                pageSubtitle.Width = pageTitle.Width;
            };
            host = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg };
            host.Resize += delegate { FitBody(); };
            content.Controls.Add(host);
            content.Controls.Add(heading);
            root.Controls.Add(content, 1, 0);

            // ---- tray + reminders
            tray = new NotifyIcon { Icon = Icon ?? SystemIcons.Application, Text = "爱时间 · Windows" };
            ContextMenuStrip menu = new ContextMenuStrip { Font = Theme.Font(9.5f) };
            menu.Items.Add("打开爱时间", null, delegate { ShowFromTray(); });
            ToolStripMenuItem startItem = new ToolStripMenuItem("开始今日计划", null, delegate { StartPlan(currentPlan, false, false); });
            menu.Items.Add(startItem);
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出", null, delegate { ExitApp(); });
            menu.Items.Add(exitItem);
            menu.Opening += delegate { startItem.Enabled = !sessionRunning; exitItem.Enabled = !sessionRunning; };
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowFromTray(); };
            tray.BalloonTipClicked += delegate { ShowFromTray(); };
            tray.Visible = !snapshotMode && store.Config.BackgroundReminder;

            clock = new System.Windows.Forms.Timer { Interval = 20000 };
            clock.Tick += delegate { OnClock(); };
            if (!snapshotMode) clock.Start();

            if (!snapshotMode && !startHidden) Opacity = 0;
            Shown += delegate {
                Theme.LightTitleBar(this);
                OnClock();
                if (Opacity < 1) Anim.Run(this, 260, delegate(float t) { Opacity = Math.Max(0.01, t); }, delegate { Opacity = 1; });
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (e.CloseReason == CloseReason.UserClosing && !allowExit && !snapshotMode && store.Config.BackgroundReminder)
                {
                    e.Cancel = true;
                    Hide();
                    if (!store.Config.TrayHintShown)
                    {
                        store.Config.TrayHintShown = true;
                        TrySaveConfig();
                        tray.ShowBalloonTip(6000, "爱时间仍在后台运行", "会在 " + NextReminderText() + " 提醒你。右键托盘图标可以退出。", ToolTipIcon.Info);
                    }
                    return;
                }
                if (!snapshotMode) TrySaveConfig();
            };
            FormClosed += delegate { clock.Stop(); tray.Visible = false; tray.Dispose(); };
            RefreshData();
        }

        protected override void SetVisibleCore(bool value)
        {
            // Started by the Windows "Run" entry: create the window but stay in the tray.
            if (startHidden)
            {
                startHidden = false;
                if (!IsHandleCreated) CreateHandle();
                value = false;
            }
            base.SetVisibleCore(value);
        }

        public void ShowFromTray()
        {
            if (sessionRunning) return;
            if (DateTime.Today != currentPlan.Date) RefreshData();
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        private void ExitApp()
        {
            if (sessionRunning) return;
            allowExit = true;
            Close();
        }

        private void EnsureTodayProfile()
        {
            string key = currentPlan.PlanKey + "-core";
            if (String.Equals(store.Config.LastPlanKey, key, StringComparison.Ordinal)) return;
            try { StudyPlanner.ApplyCoreProfile(store, currentPlan); }
            catch (Exception) { }
        }

        private void AddNav(TableLayoutPanel parent, int row, string key, string text)
        {
            RoundButton b = Theme.Button(text);
            b.NavStyle = true;
            b.Font = Theme.Font(10.5f, true);
            b.Tag = key;
            b.Dock = DockStyle.Fill;
            b.Margin = Theme.S(0, 2, 0, 2);
            b.Click += delegate { ShowPage(key); };
            parent.Controls.Add(b, 0, row);
            navButtons.Add(b);
        }

        public void RefreshData()
        {
            currentPlan = StudyPlanner.Today(store.Config.LearningDay);
            EnsureTodayProfile();
            ShowPage(currentPage);
            UpdateStatus();
        }

        // ------------------------------------------------------------ clock, status, reminders

        private void OnClock()
        {
            if (sessionRunning) return;
            if (DateTime.Today != currentPlan.Date) RefreshData();
            UpdateStatus();
            CheckReminders();
        }

        private static string TaskKey(StudyPlan plan,string key)
        {
            return key == ExtKey ? key : "day" + plan.LearningDay.ToString("D3") + "-" + key;
        }
        private bool CoreDone(StudyPlan plan)
        {
            return store.IsDone(plan.Date, TaskKey(plan, ReviewKey)) && store.IsDone(plan.Date, TaskKey(plan, NewKey));
        }
        private void OfferAdvance(StudyPlan plan)
        {
            if (store.Config.LearningDay != plan.LearningDay || !CoreDone(plan)) return;
            int next = plan.LearningDay + 1;
            if (!StudyPlanner.HasTask(next)) return;
            if (MessageBox.Show(this, "旧题、新题已勾选。确认独立完成并验收通过，进入 Day " + next.ToString("00") + "？", "学习进度", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            store.Config.LearningDay = next;
            store.SaveConfig();
            RefreshData();
            Confetti.Burst(this, PointToScreen(new Point(ClientSize.Width / 2, ClientSize.Height / 3)), 110);
            Toast.Pop(this, "进入 Day " + next.ToString("00") + "，继续加油");
        }

        private static string Span(TimeSpan t)
        {
            int h = (int)t.TotalHours, m = Math.Max(1, t.Minutes + (t.Seconds > 0 ? 1 : 0));
            if (m == 60) { h++; m = 0; }
            return h > 0 ? h + " 小时" + (m > 0 ? " " + m + " 分" : "") : m + " 分钟";
        }

        private string StatusText(StudyPlan plan, out Color color)
        {
            if (CoreDone(plan)) { color = Theme.Green; return plan.IsWeekend ? "今天的周末计划已完成" : "今日两题已完成"; }
            DateTime start = plan.Date.AddHours(plan.StartHour);
            DateTime now = DateTime.Now;
            if (now < start)
            {
                color = Theme.AccentStrong;
                return plan.StartHour.ToString("00") + ":00 开始  ·  还有 " + Span(start - now);
            }
            color = Theme.Amber;
            return "已到开始时间  ·  现在就可以开始";
        }

        private string NextReminderText()
        {
            DateTime now = DateTime.Now;
            StudyPlan plan = currentPlan;
            if (!plan.IsWeekend && now.Hour < StudyPlanner.UpdateHour) return "10:00";
            if (now.Hour < plan.StartHour && !CoreDone(plan)) return plan.StartHour.ToString("00") + ":00";
            DayOfWeek next = plan.Date.AddDays(1).DayOfWeek;
            bool weekend = next == DayOfWeek.Saturday || next == DayOfWeek.Sunday;
            return "明天 " + (weekend ? "12:00" : "10:00");
        }

        private void UpdateStatus()
        {
            StudyPlan plan = currentPlan;
            int minutes = store.GetTodaySeconds() / 60;
            sideMinutes.Text = minutes + " 分钟";
            sideBar.Value = plan.DailyGoalMinutes <= 0 ? 0 : minutes / (float)plan.DailyGoalMinutes;
            sideGoal.Text = "目标 " + plan.DailyGoalMinutes + " 分钟  ·  " + plan.Reminder;
            sideReminder.Text = store.Config.BackgroundReminder ? "下次提醒  " + NextReminderText() : "后台提醒已关闭";
            sideReminder.ForeColor = store.Config.BackgroundReminder ? Color.White : Theme.OnHero;
            if (heroStatus != null && !heroStatus.IsDisposed)
            {
                Color c;
                heroStatus.Text = StatusText(plan, out c);
                // The hero is a dark gradient: use light tints of the status colours.
                heroStatus.ForeColor = c == Theme.Green ? Color.FromArgb(187, 247, 208) : c == Theme.Amber ? Color.FromArgb(253, 230, 138) : Color.FromArgb(191, 219, 254);
            }
            if (heroProgress != null && !heroProgress.IsDisposed)
            {
                int done = new[] { ReviewKey, NewKey, ExtKey }.Count(k => store.IsDone(plan.Date, TaskKey(plan, k)));
                heroProgress.Text = "今日清单 " + done + " / 3  ·  已专注 " + minutes + " / " + plan.DailyGoalMinutes + " 分钟  ·  开始后范围与时长固定，到时自动恢复";
            }
        }

        private void CheckReminders()
        {
            if (sessionRunning || !tray.Visible) return;
            StudyPlan plan = currentPlan;
            DateTime now = DateTime.Now;
            var due = new List<Tuple<int, string, string>>();
            if (!plan.IsWeekend)
                due.Add(Tuple.Create(StudyPlanner.UpdateHour, "今日题目已更新",
                    (String.IsNullOrWhiteSpace(plan.DayLabel) ? "" : plan.DayLabel + " · ") + plan.NewTitle + "\n18:00 开始，约 15 分钟。"));
            if (!CoreDone(plan))
                due.Add(Tuple.Create(plan.StartHour, plan.IsWeekend ? "周末计划开始了" : "该开始今日两题了",
                    plan.IsWeekend ? "60 分钟知识学习 + 120 分钟实操。点这里打开爱时间。" : "旧题：" + plan.ReviewTitle + "\n新题：" + plan.NewTitle));
            foreach (var item in due)
            {
                DateTime at = plan.Date.AddHours(item.Item1);
                if (now < at || now >= at.AddHours(3)) continue;
                string key = plan.Date.ToString("yyyyMMdd") + "-" + item.Item1.ToString("00") + "00";
                if (store.Config.ReminderMarks.Contains(key)) continue;
                store.Config.ReminderMarks.Add(key);
                try { store.SaveConfig(); } catch (Exception) { }
                tray.ShowBalloonTip(10000, item.Item2, item.Item3, ToolTipIcon.Info);
                break;
            }
        }

        // ------------------------------------------------------------ pages

        private Bitmap CaptureHost()
        {
            Bitmap bmp = new Bitmap(Math.Max(1, host.Width), Math.Max(1, host.Height), PixelFormat.Format32bppPArgb);
            host.DrawToBitmap(bmp, new Rectangle(Point.Empty, host.Size));
            return bmp;
        }

        /// <summary>Top-level cards of the current page in host coordinates, in reading order (for the staggered entrance).</summary>
        private List<Rectangle> CardRects()
        {
            List<Rectangle> list = new List<Rectangle>();
            CollectCards(host, list);
            int row = Math.Max(1, Theme.S(40));
            return list.OrderBy(r => r.Y / row).ThenBy(r => r.X).ToList();
        }

        private void CollectCards(Control parent, List<Rectangle> list)
        {
            foreach (Control c in parent.Controls)
            {
                RoundPanel card = c as RoundPanel;
                if (card != null && card.Shadow && card.Visible) { list.Add(host.RectangleToClient(card.RectangleToScreen(card.ClientRectangle))); continue; }
                CollectCards(c, list);
            }
        }

        private void ShowPage(string page)
        {
            PageFade fade = null;
            if (!Anim.Disabled && Visible && Opacity >= 1 && pageBody != null && host.Width > 10 && host.Height > 10)
            {
                try
                {
                    fade = new PageFade(CaptureHost()) { Bounds = host.Bounds };
                    contentPanel.Controls.Add(fade);
                    fade.BringToFront();
                    fade.Update();
                }
                catch (Exception) { fade = null; }
            }
            currentPage = page;
            nav.Select(page);
            host.SuspendLayout();
            while (host.Controls.Count > 0)
            {
                Control c = host.Controls[0];
                host.Controls.RemoveAt(0);
                c.Dispose();
            }
            pageBody = null;
            heroStatus = null;
            heroProgress = null;
            host.AutoScrollPosition = Point.Empty;
            if (page == "today") { pageTitle.Text = "今天"; BuildToday(); }
            else if (page == "plan") { pageTitle.Text = "编程计划"; BuildPlan(); }
            else if (page == "tasks") { pageTitle.Text = "待办"; BuildTasks(); }
            else { pageTitle.Text = "应用范围"; BuildApps(); }
            FitBody();
            host.ResumeLayout(true);
            UpdateStatus();
            if (fade != null)
            {
                PageFade overlay = fade;
                Bitmap after = null;
                try { after = CaptureHost(); } catch (Exception) { }
                if (after == null) { contentPanel.Controls.Remove(overlay); overlay.Dispose(); return; }
                overlay.Play(after, CardRects(), delegate { contentPanel.Controls.Remove(overlay); overlay.Dispose(); });
            }
        }

        private void SetBody(Control body, int minHeight)
        {
            body.Dock = DockStyle.Top;
            pageBody = body;
            pageMinHeight = minHeight;
            host.Controls.Add(body);
        }

        private void FitBody()
        {
            if (pageBody == null || pageBody.IsDisposed) return;
            pageBody.Height = Math.Max(pageMinHeight, host.ClientSize.Height);
        }

        private void BuildToday()
        {
            StudyPlan plan = currentPlan;
            pageSubtitle.Text = plan.Date.ToString("yyyy 年 M 月 d 日") + "  ·  " + StudyPlanner.DayName(plan.Date.DayOfWeek) + "  ·  " + plan.Track;

            TableLayoutPanel body = Grid(2, 61);
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(248)));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            SetBody(body, Theme.S(600));

            // hero
            RoundPanel hero = Card(body, 0, 0, Theme.S(0, 0, 14, 14));
            hero.FillColor = Theme.HeroA; hero.FillColor2 = Theme.HeroB; hero.GradientAngle = 18f;
            hero.Padding = Theme.S(24, 18, 24, 18);
            TableLayoutPanel h = Rows(hero, 28, 46, 24, 28, -1, 44, 26);
            // Animated art sits beside the title rows; the button row below uses the full width.
            h.ColumnCount = 2;
            h.ColumnStyles.Clear();
            h.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            h.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(140)));
            HeroArt art = new HeroArt { Dock = DockStyle.Fill, Margin = new Padding(0), CenterGlyph = CoreDone(plan) ? Glyph.Check : Glyph.Play };
            art.Progress = plan.DailyGoalMinutes <= 0 ? 0 : Math.Min(1f, store.GetTodaySeconds() / 60f / plan.DailyGoalMinutes);
            h.Controls.Add(art, 1, 0);
            h.SetRowSpan(art, 5);
            FlowLayoutPanel tags = Flow();
            tags.Dock = DockStyle.Fill;
            tags.BackColor = Color.Transparent;
            tags.Controls.Add(MakeTag(String.IsNullOrWhiteSpace(plan.DayLabel) ? "Python 主线" : plan.DayLabel, Color.White, Color.FromArgb(56, 255, 255, 255), Glyph.Flag));
            tags.Controls.Add(MakeTag(plan.IsWeekend ? "周末计划 · 3 × 60 分钟" : "工作日训练 · 一旧一新", Color.White, Color.FromArgb(34, 255, 255, 255), Glyph.Calendar));
            h.Controls.Add(tags, 0, 0);
            string heroTitle = plan.IsWeekend || String.IsNullOrWhiteSpace(plan.TaskTitle) ? plan.Title : plan.TaskTitle;
            Label title = FillLabel(heroTitle, 18, true, Color.White);
            tips.SetToolTip(title, heroTitle);
            h.Controls.Add(title, 0, 1);
            h.Controls.Add(FillLabel(plan.Subtitle, 9.5f, false, Theme.OnHero), 0, 2);
            heroStatus = FillLabel("", 10, true, Color.White);
            h.Controls.Add(heroStatus, 0, 3);

            int cycles = StudyPlanner.CoreCycles(store, plan);
            TableLayoutPanel actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Color.Transparent, Margin = new Padding(0), Padding = new Padding(0) };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, plan.IsWeekend ? 0 : Theme.S(132)));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(112)));
            string startText = plan.IsWeekend
                ? "开始周末计划  ·  " + cycles + " × " + plan.FocusMinutes + " 分钟"
                : (CoreDone(plan) ? "再练一轮" : "开始今日两题") + "  ·  " + (cycles > 1 ? cycles + " × " : "") + plan.FocusMinutes + " 分钟";
            RoundButton start = Theme.Button(startText, true);
            start.Kind = ButtonKind.Inverse;
            start.Shimmer = true;
            start.Glyph = Glyph.Play;
            start.Dock = DockStyle.Fill;
            start.Margin = Theme.S(0, 0, 8, 0);
            start.Click += delegate { StartPlan(plan, false, false); };
            actions.Controls.Add(start, 0, 0);
            if (!plan.IsWeekend)
            {
                RoundButton extension = Theme.Button("扩展 " + plan.ExtensionMinutes + " 分钟");
                extension.Kind = ButtonKind.OnHero;
                extension.Glyph = Glyph.Code;
                extension.Dock = DockStyle.Fill;
                extension.Margin = Theme.S(0, 0, 8, 0);
                extension.Click += delegate { StartPlan(plan, true, false); };
                tips.SetToolTip(extension, plan.ExtensionTitle + " · 使用扩展课程的应用范围");
                actions.Controls.Add(extension, 1, 0);
            }
            RoundButton test = Theme.Button("试锁 15 秒");
            test.Kind = ButtonKind.OnHero;
            test.Glyph = Glyph.Lock;
            test.Dock = DockStyle.Fill;
            test.Click += delegate { StartPlan(plan, false, true); };
            tips.SetToolTip(test, "检查当前桌面能否正常锁定与恢复；不计入学习统计");
            actions.Controls.Add(test, 2, 0);
            h.Controls.Add(actions, 0, 5);
            h.SetColumnSpan(actions, 2);
            heroProgress = FillLabel("", 8.5f, false, Theme.OnHero);
            heroProgress.TextAlign = ContentAlignment.BottomLeft;
            h.Controls.Add(heroProgress, 0, 6);
            h.SetColumnSpan(heroProgress, 2);

            // time ring
            RoundPanel ringCard = Card(body, 1, 0, Theme.S(0, 0, 0, 14));
            ringCard.Padding = Theme.S(18, 14, 18, 12);
            TimeRing ring = new TimeRing {
                Dock = DockStyle.Fill, Records = store.Records.ToList(),
                TodaySeconds = store.GetTodaySeconds(), GoalMinutes = plan.DailyGoalMinutes
            };
            ringCard.Controls.Add(ring);
            ringCard.Controls.Add(CardHeader("今天的时间", null, Glyph.Clock));

            // checklist
            RoundPanel list = Card(body, 0, 1, Theme.S(0, 0, 14, 0));
            Panel listHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, AutoScroll = true };
            list.Controls.Add(listHost);
            RoundButton open = Theme.Button("打开题目");
            open.Glyph = Glyph.Open;
            open.Width = Theme.S(112); open.Height = Theme.S(32); open.Font = Theme.F(9, true);
            open.Enabled = plan.HasTaskFile;
            open.Click += delegate { TryAction(delegate { OpenTaskFile(plan); }); };
            tips.SetToolTip(open, plan.HasTaskFile ? plan.TaskFile : "未找到 D:\\PythonPractice\\DayNN_…\\task.txt");
            list.Controls.Add(CardHeader("今日清单", open, Glyph.Tasks));
            TableLayoutPanel rows = Vertical(3);
            rows.Dock = DockStyle.Top;
            rows.Height = Theme.S(3 * 98);
            for (int i = 0; i < 3; i++) rows.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(98)));
            string acceptance = String.IsNullOrWhiteSpace(plan.Acceptance) ? "" : "\n\n验收：\n" + plan.Acceptance;
            rows.Controls.Add(TaskBlock(plan, ReviewKey, "01  旧题复习", plan.ReviewTitle, plan.ReviewBody, plan.ReviewBody, Theme.Accent), 0, 0);
            rows.Controls.Add(TaskBlock(plan, NewKey, "02  新题进阶", plan.NewTitle, plan.NewBody, plan.NewBody + acceptance, Theme.Green), 0, 1);
            rows.Controls.Add(TaskBlock(plan, ExtKey, "03  当天扩展 · 可选 " + plan.ExtensionMinutes + " 分钟", plan.ExtensionTitle, plan.ExtensionBody,
                plan.ExtensionBody + "\n\n应用：" + AppSummary(plan.ExtensionApps), Theme.Amber), 0, 2);
            listHost.Controls.Add(rows);

            // apps
            RoundPanel apps = Card(body, 1, 1, new Padding(0));
            Panel appHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, AutoScroll = true };
            apps.Controls.Add(appHost);
            Panel footer = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(52), BackColor = Theme.Surface, Padding = Theme.S(0, 10, 0, 0) };
            RoundButton edit = Theme.Button("管理应用范围");
            edit.Glyph = Glyph.Settings;
            edit.Dock = DockStyle.Fill;
            edit.Click += delegate { ShowPage("apps"); };
            footer.Controls.Add(edit);
            apps.Controls.Add(footer);
            apps.Controls.Add(CardHeader("本轮允许应用", null, Glyph.Shield));
            AddAppRows(appHost, TodayApps(plan));
        }

        private Control TaskBlock(StudyPlan plan, string key, string eyebrow, string title, string body, string tooltip, Color accent)
        {
            bool done = store.IsDone(plan.Date, TaskKey(plan, key));
            Color fill = Color.FromArgb(249, 250, 252);
            RoundPanel block = new RoundPanel {
                Dock = DockStyle.Fill, Margin = Theme.S(0, 3, 0, 3), Padding = Theme.S(16, 8, 14, 6),
                Radius = Theme.S(10), FillColor = fill, BorderColor = Theme.Stroke, AccentBar = accent
            };
            TableLayoutPanel grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, BackColor = fill, Margin = new Padding(0), Padding = new Padding(0) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(86)));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(22)));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(26)));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Label e = FillLabel(eyebrow, 8.5f, true, accent);
            Label t = FillLabel(title, 10.5f, true, done ? Theme.Muted : Theme.Text);
            Label b = FillLabel(body, 9, false, Theme.Muted);
            b.TextAlign = ContentAlignment.TopLeft;
            CheckDot check = new CheckDot { Checked = done, Caption = done ? "已完成" : "完成", Dock = DockStyle.Fill, Margin = new Padding(0) };
            check.CheckedChanged += delegate {
                TryAction(delegate {
                    store.SetDone(plan.Date, TaskKey(plan, key), check.Checked);
                    check.Caption = check.Checked ? "已完成" : "完成";
                    t.ForeColor = check.Checked ? Theme.Muted : Theme.Text;
                    check.Invalidate();
                    UpdateStatus();
                    if (check.Checked)
                    {
                        Confetti.Burst(this, check.PointToScreen(new Point(Theme.S(11), check.Height / 2)), 44);
                        Toast.Pop(this, "已标记完成：" + title);
                    }
                    if (check.Checked && key != ExtKey) OfferAdvance(plan);
                });
            };
            tips.SetToolTip(check, "通过验收后再勾选；只记录在本机");
            grid.Controls.Add(e, 0, 0);
            grid.Controls.Add(check, 1, 0);
            grid.SetRowSpan(check, 2);
            grid.Controls.Add(t, 0, 1);
            grid.Controls.Add(b, 0, 2);
            grid.SetColumnSpan(b, 2);
            foreach (Control c in new Control[] { e, t, b }) tips.SetToolTip(c, title + "\n\n" + tooltip);
            block.Controls.Add(grid);
            return block;
        }

        private List<PlanApp> TodayApps(StudyPlan plan)
        {
            return plan.Apps.Concat(plan.IsWeekend ? plan.ExtensionApps : new List<PlanApp>()).ToList();
        }

        private void AddAppRows(Panel parent, IEnumerable<PlanApp> specs)
        {
            List<PlanApp> list = specs.GroupBy(a => (a.Path ?? "") + "|" + a.Name).Select(g => g.First()).ToList();
            int rowH = Theme.S(58);
            TableLayoutPanel rows = Vertical(Math.Max(1, list.Count));
            rows.Dock = DockStyle.Top;
            rows.Height = Math.Max(1, list.Count) * rowH;
            if (list.Count == 0)
            {
                rows.RowStyles.Add(new RowStyle(SizeType.Absolute, rowH));
                rows.Controls.Add(FillLabel("今天没有检测到可自动绑定的应用。", 9, false, Theme.Muted), 0, 0);
            }
            int index = 0;
            foreach (PlanApp app in list)
            {
                rows.RowStyles.Add(new RowStyle(SizeType.Absolute, rowH));
                TableLayoutPanel row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, BackColor = Theme.Surface, Margin = new Padding(0), Padding = Theme.S(0, 4, 0, 4) };
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(50)));
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(60)));
                AppIcon icon = new AppIcon { ExePath = app.Path, Dock = DockStyle.Fill, Margin = Theme.S(0, 2, 10, 2) };
                row.Controls.Add(icon, 0, 0);
                row.SetRowSpan(icon, 2);
                row.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
                row.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
                string process = String.IsNullOrWhiteSpace(app.Path) ? "未检测到进程" : Path.GetFileName(app.Path);
                Label name = FillLabel(app.Name, 10, true);
                Label detail = FillLabel(app.Role + "  ·  " + process, 8.5f, false, app.Exists ? Theme.Muted : Theme.Danger);
                Pill state = new Pill(app.Exists ? "可用" : "缺失", app.Exists ? Theme.Green : Theme.Danger, app.Exists ? Theme.GreenSoft : Theme.DangerSoft);
                state.Anchor = AnchorStyles.Right;
                state.Margin = new Padding(0);
                name.TextAlign = ContentAlignment.BottomLeft;
                detail.TextAlign = ContentAlignment.TopLeft;
                row.Controls.Add(name, 1, 0);
                row.Controls.Add(detail, 1, 1);
                row.Controls.Add(state, 2, 0);
                row.SetRowSpan(state, 2);
                tips.SetToolTip(detail, String.IsNullOrWhiteSpace(app.Path) ? "未在常见路径找到" : app.Path);
                rows.Controls.Add(row, 0, index++);
            }
            parent.Controls.Add(rows);
        }

        private string AppSummary(IEnumerable<PlanApp> apps)
        {
            string[] parts = apps.Where(a => a.Exists).Select(a => a.Name + " · " + Path.GetFileName(a.Path)).Distinct().ToArray();
            if (parts.Length == 0) return "未检测到所需进程";
            return String.Join("  +  ", parts);
        }

        private void OpenTaskFile(StudyPlan plan)
        {
            if (!plan.HasTaskFile) throw new FileNotFoundException("没有找到今天的 task.txt。", plan.TaskFile);
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + plan.TaskFile + "\"") { UseShellExecute = true });
        }

        private bool RunSession(int seconds, bool test, int cycles, string[] labels, string subject)
        {
            sessionRunning = true;
            tray.Text = "爱时间 · 专注中";
            try { return new SessionRunner(store, this).Begin(seconds, test, cycles, labels, subject); }
            finally { sessionRunning = false; tray.Text = "爱时间 · Windows"; }
        }

        private void StartPlan(StudyPlan plan, bool extension, bool test)
        {
            if (sessionRunning) return;
            TryAction(delegate
            {
                if (extension) StudyPlanner.ApplyExtensionProfile(store, plan);
                else StudyPlanner.ApplyCoreProfile(store, plan);
                int minutes = extension ? Math.Max(1, plan.ExtensionMinutes) : plan.FocusMinutes;
                int cycles = extension ? 1 : StudyPlanner.CoreCycles(store, plan);
                string subject = extension ? "扩展 · " + plan.ExtensionTitle : plan.Title;
                bool completed = RunSession(test ? 15 : minutes * 60, test, cycles, extension ? null : plan.CycleLabels, subject);
                RefreshData();
                if (completed && !test) AskMarkDone(plan, extension);
            });
        }

        private void AskMarkDone(StudyPlan plan, bool extension)
        {
            string[] keys = extension ? new[] { ExtKey } : new[] { ReviewKey, NewKey };
            if (keys.All(k => store.IsDone(plan.Date, TaskKey(plan, k)))) return;
            string what = extension ? "「当天扩展」" : "「旧题复习」和「新题进阶」";
            DialogResult answer = MessageBox.Show(this, "本轮专注已完成。\n\n如果已经通过验收，可以把" + what + "标记为完成。\n没有通过就选“否”，明天继续这一 Day。",
                "标记今日清单", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
            foreach (string k in keys) store.SetDone(plan.Date, TaskKey(plan, k), true);
            RefreshData();
            Confetti.Burst(this, PointToScreen(new Point(ClientSize.Width / 2, ClientSize.Height / 3)), 90);
            Toast.Pop(this, "今日清单已更新");
            if (!extension) OfferAdvance(plan);
        }

        private void StartAdHoc(TaskItem task)
        {
            if (sessionRunning) return;
            TryAction(delegate
            {
                StudyPlanner.ApplyCoreProfile(store, currentPlan);
                bool completed = RunSession(store.Config.TaskMinutes * 60, false, 1, null, task.Title);
                if (completed && !task.Done &&
                    MessageBox.Show(this, "「" + task.Title + "」的专注已完成。要把这项待办标记为完成吗？", "待办", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    task.Done = true;
                    store.SaveTasks();
                }
                RefreshData();
            });
        }

        private void BuildPlan()
        {
            StudyPlan plan = currentPlan;
            pageSubtitle.Text = "题目 10:00 更新，工作日 18:00 开始；周六、周日 12:00 使用独立的周末计划。";
            TableLayoutPanel body = Grid(2, 62);
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(128)));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            SetBody(body, Theme.S(740));
            AddKpis(body, plan);

            RoundPanel week = Card(body, 0, 1, Theme.S(0, 0, 14, 0));
            Panel weekHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, AutoScroll = true };
            week.Controls.Add(weekHost);
            week.Controls.Add(CardHeader("每周节奏 · 本周专注", null, Glyph.Calendar));
            int rowH = Theme.S(66);
            TableLayoutPanel rows = Vertical(7); rows.Dock = DockStyle.Top; rows.Height = 7 * rowH;
            DateTime monday = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));
            for (int i = 0; i < 7; i++)
            {
                DateTime date = monday.AddDays(i);
                DayOfWeek day = date.DayOfWeek;
                bool today = date == DateTime.Today;
                bool weekend = day == DayOfWeek.Saturday || day == DayOfWeek.Sunday;
                rows.RowStyles.Add(new RowStyle(SizeType.Absolute, rowH));
                Color fill = today ? Theme.AccentSoft : Theme.Surface2;
                RoundPanel row = new RoundPanel {
                    Dock = DockStyle.Fill, Margin = Theme.S(0, 4, 0, 4), Padding = Theme.S(14, 4, 14, 4),
                    Radius = Theme.S(10), FillColor = fill, BorderColor = today ? Theme.Accent : fill
                };
                TableLayoutPanel cells = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = fill, Margin = new Padding(0) };
                cells.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(70)));
                cells.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                cells.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(86)));
                cells.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(96)));
                cells.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                cells.Controls.Add(FillLabel(StudyPlanner.DayName(day) + "\n" + date.ToString("M/d"), 9.5f, true, today || weekend ? Theme.AccentStrong : Theme.Text), 0, 0);
                cells.Controls.Add(FillLabel(StudyPlanner.WeeklyLine(day), 9, false, today ? Theme.Text : Theme.Muted), 1, 0);
                int mins = store.GetDaySeconds(date) / 60;
                string right = date > DateTime.Today ? "—" : (today ? "今天 · " : "") + mins + " 分钟";
                Label m = FillLabel(right, 9, today || mins > 0, today ? Theme.AccentStrong : mins > 0 ? Theme.Green : Theme.Muted);
                m.TextAlign = ContentAlignment.MiddleRight;
                int dayGoal = weekend ? 180 : 15;
                MiniBar bar = new MiniBar { Dock = DockStyle.Fill, Margin = Theme.S(8, 0, 0, 0), BarColor = mins >= dayGoal ? Theme.Green : Theme.AccentStrong, TrackColor = today ? Color.White : Theme.Stroke };
                bar.Value = date > DateTime.Today ? 0 : mins / (float)dayGoal;
                cells.Controls.Add(bar, 2, 0);
                cells.Controls.Add(m, 3, 0);
                row.Controls.Add(cells);
                rows.Controls.Add(row, 0, i);
            }
            weekHost.Controls.Add(rows);

            RoundPanel settings = Card(body, 1, 1, new Padding(0));
            TableLayoutPanel form = Rows(settings, 34, 22, 40, 22, 40, 22, 40, 14, 34, 34, 34, 54, -1, 42, 26);
            form.Controls.Add(CardHeader("专注设置", null, Glyph.Settings), 0, 0);
            form.Controls.Add(FillLabel("工作日两题 · 连续轮数（每轮 " + (plan.IsWeekend ? 15 : plan.FocusMinutes) + " 分钟）", 9, false, Theme.Muted), 0, 1);
            NumericUpDown cycles = Theme.Number(1, 12, store.Config.Cycles); cycles.Anchor = AnchorStyles.Left; cycles.Width = Theme.S(150); form.Controls.Add(cycles, 0, 2);
            form.Controls.Add(FillLabel("轮间休息（分钟）", 9, false, Theme.Muted), 0, 3);
            NumericUpDown rest = Theme.Number(0, 60, store.Config.BreakMinutes); rest.Anchor = AnchorStyles.Left; rest.Width = Theme.S(150); form.Controls.Add(rest, 0, 4);
            form.Controls.Add(FillLabel("临时待办专注时长（分钟）", 9, false, Theme.Muted), 0, 5);
            NumericUpDown taskMinutes = Theme.Number(5, 180, store.Config.TaskMinutes); taskMinutes.Anchor = AnchorStyles.Left; taskMinutes.Width = Theme.S(150); form.Controls.Add(taskMinutes, 0, 6);
            CheckDot background = new CheckDot { Checked = store.Config.BackgroundReminder, Caption = "关闭窗口后留在托盘，按时提醒", Dock = DockStyle.Fill, Margin = new Padding(0) };
            CheckDot autostart = new CheckDot { Checked = AutoStartEnabled(), Caption = "登录 Windows 时在后台启动", Dock = DockStyle.Fill, Margin = new Padding(0) };
            tips.SetToolTip(background, "工作日 10:00 / 18:00、周末 12:00 弹出提醒");
            tips.SetToolTip(autostart, "写入当前用户的启动项（HKCU\\…\\Run），可随时关闭");
            form.Controls.Add(background, 0, 8);
            form.Controls.Add(autostart, 0, 9);
            CheckDot effects = new CheckDot { Checked = store.Config.Effects, Caption = "界面动效（光效、粒子、庆祝彩带）", Dock = DockStyle.Fill, Margin = new Padding(0) };
            tips.SetToolTip(effects, "关闭后只保留基础过渡，适合低配置电脑");
            form.Controls.Add(effects, 0, 10);
            Label note = FillLabel("周末固定 3 × 60 分钟：第 1 轮知识学习，第 2、3 轮实操，中间按上面的休息时长休息。锁定期间白名单、时长和轮数固定。", 8.5f, false, Theme.Muted);
            note.TextAlign = ContentAlignment.TopLeft;
            form.Controls.Add(note, 0, 11);
            RoundButton save = Theme.Button("保存设置", true); save.Glyph = Glyph.Check; save.Dock = DockStyle.Fill; form.Controls.Add(save, 0, 13);
            Label feedback = FillLabel("", 8.5f, false, Theme.Green); form.Controls.Add(feedback, 0, 14);
            save.Click += delegate {
                TryAction(delegate {
                    store.Config.Cycles = (int)cycles.Value;
                    store.Config.BreakMinutes = (int)rest.Value;
                    store.Config.TaskMinutes = (int)taskMinutes.Value;
                    store.Config.BackgroundReminder = background.Checked;
                    store.Config.Effects = effects.Checked;
                    Ambient.Enabled = effects.Checked;
                    store.SaveConfig();
                    tray.Visible = !snapshotMode && background.Checked;
                    if (autostart.Checked != AutoStartEnabled()) SetAutoStart(autostart.Checked);
                    feedback.Text = "已保存，下次开始时生效。";
                    UpdateStatus();
                    Toast.Pop(this, "设置已保存");
                });
            };
        }

        private void AddKpis(TableLayoutPanel body, StudyPlan plan)
        {
            TableLayoutPanel kpis = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = Theme.Bg, Margin = Theme.S(0, 0, 0, 12), Padding = new Padding(0) };
            for (int i = 0; i < 4; i++) kpis.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            kpis.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            DateTime todayDate = DateTime.Today;
            DateTime monday = todayDate.AddDays(-(((int)todayDate.DayOfWeek + 6) % 7));
            int week = 0, active = 0;
            for (DateTime d = monday; d <= todayDate; d = d.AddDays(1)) { int m = store.GetDaySeconds(d) / 60; week += m; if (m > 0) active++; }
            int streak = 0;
            DateTime cursor = store.GetDaySeconds(todayDate) > 0 ? todayDate : todayDate.AddDays(-1);
            while (streak < 366 && store.GetDaySeconds(cursor) > 0) { streak++; cursor = cursor.AddDays(-1); }
            int todayMin = store.GetTodaySeconds() / 60;
            kpis.Controls.Add(new KpiTile { Dock = DockStyle.Fill, Margin = Theme.S(0, 0, 12, 0), GlyphText = Glyph.Clock, Caption = "今日专注", Value = todayMin, Unit = "分钟",
                Note = "目标 " + plan.DailyGoalMinutes, Progress = plan.DailyGoalMinutes <= 0 ? 0 : todayMin / (float)plan.DailyGoalMinutes }, 0, 0);
            kpis.Controls.Add(new KpiTile { Dock = DockStyle.Fill, Margin = Theme.S(0, 0, 12, 0), GlyphText = Glyph.Calendar, Caption = "本周专注", Value = week, Unit = "分钟",
                Note = active + " 天有记录", Tint = Theme.Violet, TintSoft = Theme.VioletSoft }, 1, 0);
            kpis.Controls.Add(new KpiTile { Dock = DockStyle.Fill, Margin = Theme.S(0, 0, 12, 0), GlyphText = Glyph.Bolt, Caption = "连续学习", Value = streak, Unit = "天",
                Note = streak > 0 ? "保持节奏" : "今天开始", Tint = Theme.Amber, TintSoft = Theme.AmberSoft }, 2, 0);
            kpis.Controls.Add(new KpiTile { Dock = DockStyle.Fill, Margin = new Padding(0), GlyphText = Glyph.Flag, Caption = "当前进度", Prefix = "Day ", Value = plan.LearningDay,
                Note = CoreDone(plan) ? "今日已验收" : "完成验收后推进", Tint = Theme.Green, TintSoft = Theme.GreenSoft }, 3, 0);
            body.Controls.Add(kpis, 0, 0);
            body.SetColumnSpan(kpis, 2);
        }

        private static bool AutoStartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key != null && key.GetValue(RunValue) != null;
            }
            catch (Exception) { return false; }
        }

        private static void SetAutoStart(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) key.SetValue(RunValue, "\"" + Application.ExecutablePath + "\" --background");
                else if (key.GetValue(RunValue) != null) key.DeleteValue(RunValue, false);
            }
        }

        private void BuildTasks()
        {
            pageSubtitle.Text = "这里只放临时待办；每日训练题由编程计划自动展示在“今天”。";
            TableLayoutPanel outer = Vertical(2);
            outer.BackColor = Theme.Bg;
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(156)));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            SetBody(outer, Theme.S(480));

            RoundPanel add = Card(outer, 0, 0, Theme.S(0, 0, 0, 14));
            TableLayoutPanel addRows = Rows(add, 32, 44, -1);
            addRows.Controls.Add(CardHeader("添加临时待办", null, Glyph.Add), 0, 0);
            TableLayoutPanel row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Surface, Margin = new Padding(0) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(112)));
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            TextBox input = Theme.Input(); input.Font = Theme.Font(11.5f); input.Anchor = AnchorStyles.Left | AnchorStyles.Right; input.Margin = Theme.S(0, 0, 10, 0); input.MaxLength = 200;
            RoundButton button = Theme.Button("添加", true); button.Glyph = Glyph.Add; button.Dock = DockStyle.Fill;
            Action addTask = delegate {
                string value = input.Text.Trim();
                if (value.Length == 0) { input.Focus(); return; }
                store.Tasks.Add(new TaskItem { Id = Guid.NewGuid().ToString("N"), Title = value, Done = false });
                store.SaveTasks(); ShowPage("tasks");
                Toast.Pop(this, "已添加待办");
            };
            button.Click += delegate { TryAction(addTask); };
            input.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; TryAction(addTask); } };
            row.Controls.Add(input, 0, 0); row.Controls.Add(button, 1, 0);
            addRows.Controls.Add(row, 0, 1);
            addRows.Controls.Add(FillLabel("回车即可添加。点“专注”会用今天的允许应用锁定 " + store.Config.TaskMinutes + " 分钟（可在编程计划页修改）。", 8.5f, false, Theme.Muted), 0, 2);

            RoundPanel tasks = Card(outer, 0, 1, new Padding(0));
            Panel scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Surface };
            tasks.Controls.Add(scroller);
            tasks.Controls.Add(CardHeader("待办  ·  " + store.Tasks.Count(t => !t.Done) + " 项未完成", null, Glyph.Tasks));
            if (store.Tasks.Count == 0)
            {
                Label empty = Theme.Label("没有额外待办。今天的两道训练题已经在“今天”页面。", 10, false, Theme.Muted);
                empty.Dock = DockStyle.Fill; empty.TextAlign = ContentAlignment.MiddleCenter; scroller.Controls.Add(empty);
                return;
            }
            int rowH = Theme.S(54);
            TableLayoutPanel entries = Vertical(store.Tasks.Count); entries.Dock = DockStyle.Top; entries.Height = store.Tasks.Count * rowH;
            int index = 0;
            foreach (TaskItem item in store.Tasks.OrderBy(t => t.Done).ToList())
            {
                TaskItem task = item;
                entries.RowStyles.Add(new RowStyle(SizeType.Absolute, rowH));
                TableLayoutPanel entry = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = Theme.Surface, Margin = Theme.S(0, 3, 0, 3) };
                entry.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(38)));
                entry.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                entry.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(112)));
                entry.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(76)));
                entry.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                CheckDot done = new CheckDot { Checked = task.Done, Dock = DockStyle.Fill, Margin = new Padding(0) };
                done.CheckedChanged += delegate {
                    TryAction(delegate {
                        task.Done = done.Checked;
                        store.SaveTasks();
                        if (done.Checked) Confetti.Burst(this, done.PointToScreen(new Point(Theme.S(11), done.Height / 2)), 40);
                        ShowPage("tasks");
                    });
                };
                Label text = FillLabel(task.Title, 10, false, task.Done ? Theme.Muted : Theme.Text);
                tips.SetToolTip(text, task.Title);
                RoundButton focus = Theme.Button(task.Done ? "已完成" : "专注 " + store.Config.TaskMinutes + " 分");
                focus.Glyph = task.Done ? Glyph.Check : Glyph.Play;
                focus.Dock = DockStyle.Fill; focus.Enabled = !task.Done; focus.Margin = Theme.S(6, 6, 0, 6); focus.Font = Theme.F(9, true);
                focus.Click += delegate { StartAdHoc(task); };
                RoundButton remove = Theme.Button("删除");
                remove.Kind = ButtonKind.Danger; remove.Glyph = Glyph.Delete;
                remove.Dock = DockStyle.Fill; remove.Margin = Theme.S(6, 6, 0, 6); remove.Font = Theme.F(9, true);
                remove.Click += delegate {
                    if (!task.Done && MessageBox.Show(this, "删除未完成的待办「" + task.Title + "」？", "待办", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    TryAction(delegate { store.Tasks.Remove(task); store.SaveTasks(); ShowPage("tasks"); Toast.Pop(this, "已删除待办"); });
                };
                entry.Controls.Add(done, 0, 0); entry.Controls.Add(text, 1, 0); entry.Controls.Add(focus, 2, 0); entry.Controls.Add(remove, 3, 0);
                entries.Controls.Add(entry, 0, index++);
            }
            scroller.Controls.Add(entries);
        }

        private void BuildApps()
        {
            StudyPlan plan = currentPlan;
            pageSubtitle.Text = "默认范围每天按计划自动更新；手动添加的应用作为额外允许项保留。正式开始后本轮范围固定。";
            TableLayoutPanel body = Grid(1, 54);
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            SetBody(body, Theme.S(540));

            RoundPanel recommended = Card(body, 0, 0, Theme.S(0, 0, 14, 0));
            Panel recommendedHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, AutoScroll = true };
            recommended.Controls.Add(recommendedHost);
            Label note = Theme.Label("锁定时可用：① 这里列出的 exe（按完整路径，不按文件名）；② 它们安装目录里的组件，如 conda 环境中的 python；③ 由它们启动的 Python / 终端 / 编译器；④ 输入法。其余窗口隐藏，程序在后台继续运行。", 8.5f, false, Theme.Muted);
            note.Dock = DockStyle.Bottom; note.Height = Theme.S(62); note.TextAlign = ContentAlignment.BottomLeft;
            recommended.Controls.Add(note);
            recommended.Controls.Add(CardHeader("今天自动允许", null, Glyph.Shield));
            AddAppRows(recommendedHost, TodayApps(plan));

            RoundPanel manual = Card(body, 1, 0, new Padding(0));
            TableLayoutPanel layout = Rows(manual, 34, 50, -1, 50, 54);
            layout.Controls.Add(CardHeader("额外允许应用", null, Glyph.Add), 0, 0);
            FlowLayoutPanel actions = Flow(); actions.Dock = DockStyle.Fill; actions.Padding = Theme.S(0, 4, 0, 4);
            RoundButton running = Theme.Button("从运行窗口选择", true); running.Glyph = Glyph.Apps; running.Width = Theme.S(164); running.Margin = Theme.S(0, 0, 8, 0);
            RoundButton browse = Theme.Button("浏览 exe"); browse.Glyph = Glyph.Folder; browse.Width = Theme.S(116);
            actions.Controls.Add(running); actions.Controls.Add(browse); layout.Controls.Add(actions, 0, 1);

            ListView list = AppList();
            foreach (AllowedApp app in store.Config.ManualAllowed)
            {
                ListViewItem row = new ListViewItem(String.IsNullOrWhiteSpace(app.Name) ? Path.GetFileNameWithoutExtension(app.Path) : app.Name);
                row.SubItems.Add(Path.GetFileName(app.Path));
                row.SubItems.Add(app.Path);
                row.Tag = app; row.ToolTipText = app.Path; list.Items.Add(row);
            }
            layout.Controls.Add(list, 0, 2);
            TableLayoutPanel lower = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Surface, Margin = Theme.S(0, 8, 0, 0) };
            lower.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            lower.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(108)));
            lower.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Label count = FillLabel(store.Config.ManualAllowed.Count == 0 ? "还没有额外应用" : store.Config.ManualAllowed.Count + " 个额外应用", 9, false, Theme.Muted);
            RoundButton remove = Theme.Button("移除所选"); remove.Kind = ButtonKind.Danger; remove.Glyph = Glyph.Delete; remove.Dock = DockStyle.Fill; remove.Enabled = false;
            list.SelectedIndexChanged += delegate { remove.Enabled = list.SelectedItems.Count > 0; };
            lower.Controls.Add(count, 0, 0); lower.Controls.Add(remove, 1, 0); layout.Controls.Add(lower, 0, 3);
            Label boundary = FillLabel("后台程序不会被结束。未允许的普通窗口会被隐藏或覆盖；系统安全桌面和管理员级入口仍由 Windows 控制。", 8.5f, false, Theme.Muted);
            boundary.TextAlign = ContentAlignment.BottomLeft;
            layout.Controls.Add(boundary, 0, 4);

            running.Click += delegate {
                TryAction(delegate {
                    using (AppPicker picker = new AppPicker(NativeWindows.ListVisibleApplications()))
                    {
                        if (picker.ShowDialog(this) != DialogResult.OK) return;
                        foreach (AllowedApp app in picker.SelectedApps) AddManualApp(app);
                        StudyPlanner.ApplyCoreProfile(store, currentPlan);
                        ShowPage("apps");
                    }
                });
            };
            browse.Click += delegate {
                using (OpenFileDialog dlg = new OpenFileDialog { Filter = "Windows 应用 (*.exe)|*.exe", Multiselect = true, Title = "选择额外允许的 exe", CheckFileExists = true })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    TryAction(delegate {
                        foreach (string path in dlg.FileNames)
                            AddManualApp(new AllowedApp { Name = Path.GetFileNameWithoutExtension(path), Path = path, Arguments = "" });
                        StudyPlanner.ApplyCoreProfile(store, currentPlan);
                        ShowPage("apps");
                    });
                }
            };
            remove.Click += delegate {
                TryAction(delegate {
                    foreach (ListViewItem item in list.SelectedItems) store.Config.ManualAllowed.Remove((AllowedApp)item.Tag);
                    store.SaveConfig();
                    try { StudyPlanner.ApplyCoreProfile(store, currentPlan); } catch (InvalidOperationException) { }
                    ShowPage("apps");
                });
            };
        }

        private ListView AppList()
        {
            ListView list = new ListView {
                Dock = DockStyle.Fill, BackColor = Theme.Surface, ForeColor = Theme.Text,
                BorderStyle = BorderStyle.FixedSingle, View = View.Details, FullRowSelect = true,
                MultiSelect = true, HideSelection = false, Font = Theme.Font(9),
                HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            list.Columns.Add("应用", Theme.S(120));
            list.Columns.Add("进程", Theme.S(110));
            list.Columns.Add("完整路径", Theme.S(260));
            list.Resize += delegate {
                if (list.ClientSize.Width > Theme.S(260))
                    list.Columns[2].Width = Math.Max(Theme.S(140), list.ClientSize.Width - list.Columns[0].Width - list.Columns[1].Width - SystemInformation.VerticalScrollBarWidth - 4);
            };
            list.ShowItemToolTips = true;
            return list;
        }

        private void AddManualApp(AllowedApp app)
        {
            if (!NativeWindows.IsAllowedExecutablePath(app.Path))
                throw new InvalidOperationException("无法添加这个路径，请选择实际存在且可识别的 exe：\n" + app.Path);
            string normalized = NativeWindows.NormalizedPath(app.Path);
            if (store.Config.ManualAllowed.Any(a => String.Equals(NativeWindows.NormalizedPath(a.Path), normalized, StringComparison.OrdinalIgnoreCase))) return;
            store.Config.ManualAllowed.Add(new AllowedApp {
                Name = String.IsNullOrWhiteSpace(app.Name) ? Path.GetFileNameWithoutExtension(normalized) : app.Name,
                Path = normalized,
                Arguments = app.Arguments ?? ""
            });
            store.SaveConfig();
        }

        private void TrySaveConfig() { TryAction(delegate { store.SaveConfig(); }); }

        private void TryAction(Action action)
        {
            try { action(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        // ------------------------------------------------------------ layout helpers

        private static RoundPanel Card(TableLayoutPanel body, int col, int row, Padding margin)
        {
            RoundPanel p = new RoundPanel { Dock = DockStyle.Fill, Margin = margin, Padding = Theme.S(20, 16, 22, 22), Shadow = true, BorderColor = Color.FromArgb(232, 237, 243) };
            body.Controls.Add(p, col, row);
            return p;
        }

        /// <summary>Card title row; optional control (e.g. a small button) on the right.</summary>
        private static Control CardHeader(string text, Control right) { return CardHeader(text, right, null); }

        private static Control CardHeader(string text, Control right, string glyph)
        {
            Panel header = new Panel { Dock = DockStyle.Top, Height = Theme.S(40), BackColor = Theme.Surface, Padding = Theme.S(0, 0, 0, 8) };
            Label title = Theme.Label(text, 12, true);
            title.Dock = DockStyle.Fill;
            header.Controls.Add(title);
            if (!String.IsNullOrEmpty(glyph) && Theme.HasIcons)
            {
                Label icon = new Label { Text = glyph, Font = Theme.Icon(12), ForeColor = Theme.AccentStrong, BackColor = Color.Transparent, Dock = DockStyle.Left, Width = Theme.S(28), TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };
                header.Controls.Add(icon);
                title.BringToFront();
            }
            if (right != null)
            {
                Panel holder = new Panel { Dock = DockStyle.Right, Width = right.Width, BackColor = Theme.Surface };
                right.Location = new Point(0, Math.Max(0, (Theme.S(32) - right.Height) / 2));
                holder.Controls.Add(right);
                header.Controls.Add(holder);
            }
            return header;
        }

        /// <summary>Two-column page grid; leftPercent is the width of the first column.</summary>
        private static TableLayoutPanel Grid(int rows, float leftPercent)
        {
            TableLayoutPanel p = new TableLayoutPanel { ColumnCount = 2, RowCount = rows, BackColor = Theme.Bg, Margin = new Padding(0), Padding = new Padding(0) };
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, leftPercent));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100 - leftPercent));
            return p;
        }

        /// <summary>Fills a card with a single-column table; heights in unscaled px, -1 = take the remaining space.</summary>
        private static TableLayoutPanel Rows(RoundPanel card, params int[] heights)
        {
            TableLayoutPanel p = Vertical(heights.Length);
            p.Dock = DockStyle.Fill;
            p.BackColor = card.FillColor2.IsEmpty ? card.FillColor : Color.Transparent;
            foreach (int h in heights)
                p.RowStyles.Add(h < 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, Theme.S(h)));
            card.Controls.Add(p);
            return p;
        }

        private static TableLayoutPanel Vertical(int rows)
        {
            TableLayoutPanel p = new TableLayoutPanel { ColumnCount = 1, RowCount = rows, Margin = new Padding(0), Padding = new Padding(0), BackColor = Theme.Surface };
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return p;
        }

        private static Label FillLabel(string text, float size, bool bold, Color? color = null)
        {
            Label l = Theme.Label(text, size, bold, color);
            l.Dock = DockStyle.Fill;
            return l;
        }

        private static Pill MakeTag(string text, Color fg, Color fill, string glyph = null)
        {
            Pill p = new Pill(text, fg, fill, glyph);
            p.Margin = Theme.S(0, 0, 8, 0);
            return p;
        }

        private static FlowLayoutPanel Flow()
        {
            return new FlowLayoutPanel { BackColor = Theme.Surface, Margin = new Padding(0), Padding = new Padding(0), WrapContents = false, FlowDirection = FlowDirection.LeftToRight };
        }

        // ------------------------------------------------------------ verification aid

        /// <summary>Renders every page to PNG without starting a session (used by --snapshot).</summary>
        public static int Snapshot(string dataDir, string outDir)
        {
            Directory.CreateDirectory(outDir);
            Anim.Disabled = true;
            AppStore s = new AppStore(dataDir);
            using (MainForm f = new MainForm(s, false, true))
            {
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-30000, -30000);
                f.Show();
                Application.DoEvents();
                foreach (string page in new[] { "today", "plan", "tasks", "apps" })
                {
                    f.ShowPage(page);
                    Application.DoEvents();
                    using (Bitmap bmp = new Bitmap(f.ClientSize.Width, f.ClientSize.Height))
                    {
                        f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.ClientSize));
                        bmp.Save(Path.Combine(outDir, page + ".png"), ImageFormat.Png);
                    }
                }
                f.allowExit = true;
                f.Close();
            }
            SessionForm.RenderPreview(s, Path.Combine(outDir, "focus.png"), false);
            SessionForm.RenderPreview(s, Path.Combine(outDir, "focus-preparing.png"), true);
            return 0;
        }
    }

    internal sealed class SidePanel : Panel
    {
        public SidePanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen pen = new Pen(Theme.Stroke)) e.Graphics.DrawLine(pen, Width - 1, 0, Width - 1, Height);
        }
    }

    internal sealed class BrandMark : Control
    {
        public BrandMark()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int d = Theme.S(40);
            Rectangle logo = new Rectangle(Theme.S(4), (Height - d) / 2, d, d);
            using (GraphicsPath p = Theme.RoundRect(logo, Theme.S(11)))
            using (LinearGradientBrush b = new LinearGradientBrush(logo, Theme.HeroA, Theme.HeroB, 45f)) g.FillPath(b, p);
            float inset = d * 0.22f;
            RectangleF dial = new RectangleF(logo.X + inset, logo.Y + inset, d - inset * 2, d - inset * 2);
            using (Pen pen = new Pen(Color.White, Math.Max(2f, d / 14f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawArc(pen, dial, -90, 300);
                float cx = dial.X + dial.Width / 2, cy = dial.Y + dial.Height / 2;
                g.DrawLine(pen, cx, cy, cx, dial.Y + dial.Height * 0.22f);
                g.DrawLine(pen, cx, cy, cx + dial.Width * 0.24f, cy + dial.Height * 0.12f);
            }
            using (SolidBrush dot = new SolidBrush(Color.FromArgb(120, 214, 160)))
                g.FillEllipse(dot, logo.Right - d * 0.3f, logo.Y + d * 0.12f, d * 0.2f, d * 0.2f);
            int x = logo.Right + Theme.S(12);
            using (Font title = Theme.Font(14, true))
            using (Font sub = Theme.Font(8.5f))
            {
                Size ts = TextRenderer.MeasureText("爱时间", title);
                int top = (Height - ts.Height - Theme.S(18)) / 2;
                TextRenderer.DrawText(g, "爱时间", title, new Point(x, top), Theme.Text, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, "学习专注 · Windows", sub, new Point(x + 1, top + ts.Height), Theme.Muted, TextFormatFlags.NoPadding);
            }
        }
    }

    internal sealed class AppPicker : Form
    {
        public readonly List<AllowedApp> SelectedApps = new List<AllowedApp>();
        private readonly List<AllowedApp> candidates;

        public AppPicker(List<AllowedApp> applications)
        {
            candidates = applications ?? new List<AllowedApp>();
            Text = "选择正在运行的应用";
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Font(10);
            AutoScaleMode = AutoScaleMode.None;
            Size = new Size(Theme.S(820), Theme.S(540));
            MinimumSize = new Size(Theme.S(660), Theme.S(420));
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            Padding = Theme.S4(24);

            Label title = Theme.Label("把额外需要的窗口加入本轮范围", 13, true);
            title.Dock = DockStyle.Top; title.Height = Theme.S(40);
            Label note = Theme.Label("显示实际窗口所属 exe。默认学习应用已经由今天的计划自动加入。", 9, false, Theme.Muted);
            note.Dock = DockStyle.Top; note.Height = Theme.S(34);
            CheckedListBox choices = new CheckedListBox {
                Dock = DockStyle.Fill, BackColor = Theme.Surface, ForeColor = Theme.Text,
                Font = Theme.Font(10), CheckOnClick = true, BorderStyle = BorderStyle.FixedSingle,
                IntegralHeight = false, HorizontalScrollbar = true
            };
            foreach (AllowedApp app in candidates)
                choices.Items.Add((app.Name ?? Path.GetFileNameWithoutExtension(app.Path)) + "    ·    " + Path.GetFileName(app.Path) + "    ·    " + app.Path);
            if (candidates.Count == 0) choices.Items.Add("（没有找到其他可见窗口）");

            Panel footer = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(58), BackColor = Theme.Bg, Padding = Theme.S(0, 14, 0, 0) };
            RoundButton confirm = Theme.Button("加入额外范围", true);
            confirm.Dock = DockStyle.Right; confirm.Width = Theme.S(150);
            RoundButton cancel = Theme.Button("取消");
            cancel.Width = Theme.S(100); cancel.Dock = DockStyle.Left;
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
            confirm.Click += delegate {
                foreach (int index in choices.CheckedIndices) if (index < candidates.Count) SelectedApps.Add(candidates[index]);
                DialogResult = DialogResult.OK;
            };
            footer.Controls.Add(confirm); footer.Controls.Add(cancel);
            Controls.Add(choices); Controls.Add(footer); Controls.Add(note); Controls.Add(title);
            CancelButton = cancel; AcceptButton = confirm;
            Shown += delegate { Theme.LightTitleBar(this); };
        }
    }
}