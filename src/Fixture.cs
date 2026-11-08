using System;
using System.Drawing;
using System.Windows.Forms;

namespace AiTimeFixture
{
    internal static class FixtureProgram
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string marker = args.Length > 0 ? args[0] : "fixture";
            bool allowed = marker.StartsWith("allowed-", StringComparison.Ordinal);
            using (var window = new Form())
            using (var timer = new Timer())
            {
                string title = "AiTime 验证窗口 · " + marker;
                int mouseDownCount = 0;
                window.Text = title + " · mouseDown=0";
                window.Size = new Size(530, 240);
                window.StartPosition = FormStartPosition.Manual;
                Rectangle area = Screen.PrimaryScreen.WorkingArea;
                window.Location = new Point(area.Left + (allowed ? 35 : Math.Max(45, area.Width - 565)), area.Top + 100);
                window.BackColor = Color.FromArgb(24, 29, 39);
                window.ForeColor = Color.White;
                window.Font = new Font("Microsoft YaHei UI", 10);
                window.Controls.Add(new Label { Text = "本轮验证用窗口 · " + (allowed ? "允许路径" : "未允许路径")
                    + "\n保留后台进程；60 秒后自动关闭", Dock = DockStyle.Top, Height = 80,
                    Padding = new Padding(16), AutoSize = false });
                var input = new TextBox { Text = "可编辑的验证文本", Left = 18, Top = 100, Width = 470,
                    BackColor = Color.FromArgb(39, 47, 62), ForeColor = Color.White };
                window.Controls.Add(input);
                input.MouseDown += delegate {
                    mouseDownCount++;
                    window.Text = title + " · mouseDown=" + mouseDownCount;
                };
                window.Activated += delegate {
                    window.ActiveControl = input;
                    input.Focus();
                    input.SelectionStart = input.TextLength;
                    input.SelectionLength = 0;
                };
                window.Shown += delegate {
                    window.ActiveControl = input;
                    input.Focus();
                    input.SelectionStart = input.TextLength;
                    input.SelectionLength = 0;
                };
                timer.Interval = 60000;
                timer.Tick += delegate { timer.Stop(); window.Close(); };
                timer.Start();
                Application.Run(window);
            }
        }
    }
}