using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AiTime
{
    /// <summary>Segoe Fluent Icons / MDL2 code points.</summary>
    public static class Glyph
    {
        public const string Home = "\uE80F", Calendar = "\uE787", Tasks = "\uE9D5", Apps = "\uE71D", Play = "\uE768", Pause = "\uE769",
            Clock = "\uE823", Check = "\uE73E", Folder = "\uE8B7", Lock = "\uE72E", Shield = "\uEA18", Settings = "\uE713", Add = "\uE710",
            Delete = "\uE74D", Bell = "\uEA8F", Flag = "\uE7C1", Code = "\uE943", Book = "\uE8F1", Switch = "\uE8AB", Close = "\uE711",
            Stopwatch = "\uE916", Bolt = "\uE945", Star = "\uE734", Open = "\uE8A7", Hide = "\uED1A", Info = "\uE946", Down = "\uE70D", Up = "\uE70E";
    }

    public static class Theme
    {
        // Light workspace (slate / blue)
        public static readonly Color Bg = Color.FromArgb(243, 245, 249);
        public static readonly Color Surface = Color.White;
        public static readonly Color Surface2 = Color.FromArgb(241, 245, 249);
        public static readonly Color Stroke = Color.FromArgb(226, 232, 240);
        public static readonly Color Text = Color.FromArgb(15, 23, 42);
        public static readonly Color Text2 = Color.FromArgb(51, 65, 85);
        public static readonly Color Muted = Color.FromArgb(100, 116, 139);
        public static readonly Color Accent = Color.FromArgb(59, 130, 246);
        public static readonly Color AccentStrong = Color.FromArgb(37, 99, 235);
        public static readonly Color AccentHover = Color.FromArgb(29, 78, 216);
        public static readonly Color AccentDeep = Color.FromArgb(30, 58, 138);
        public static readonly Color AccentSoft = Color.FromArgb(235, 242, 254);
        public static readonly Color Violet = Color.FromArgb(124, 58, 237);
        public static readonly Color VioletSoft = Color.FromArgb(243, 232, 255);
        public static readonly Color Green = Color.FromArgb(22, 163, 74);
        public static readonly Color GreenSoft = Color.FromArgb(220, 252, 231);
        public static readonly Color Amber = Color.FromArgb(217, 119, 6);
        public static readonly Color AmberSoft = Color.FromArgb(254, 243, 199);
        public static readonly Color Danger = Color.FromArgb(220, 38, 38);
        public static readonly Color DangerSoft = Color.FromArgb(254, 226, 226);
        public static readonly Color HeroA = Color.FromArgb(29, 78, 216);
        public static readonly Color HeroB = Color.FromArgb(109, 40, 217);
        public static readonly Color OnHero = Color.FromArgb(219, 234, 254);
        // Focus mode (dark)
        public static readonly Color FocusBg = Color.FromArgb(10, 15, 28);
        public static readonly Color FocusCard = Color.FromArgb(22, 30, 48);
        public static readonly Color FocusCardHover = Color.FromArgb(33, 45, 70);
        public static readonly Color FocusStroke = Color.FromArgb(42, 56, 82);
        public static readonly Color FocusText = Color.FromArgb(241, 245, 249);
        public static readonly Color FocusMuted = Color.FromArgb(148, 163, 184);
        public static readonly Color FocusAccent = Color.FromArgb(96, 165, 250);
        public static readonly Color FocusViolet = Color.FromArgb(167, 139, 250);
        public static readonly Color FocusGreen = Color.FromArgb(74, 222, 128);
        public static readonly Color FocusTeal = Color.FromArgb(45, 212, 191);
        public static readonly Color FocusDanger = Color.FromArgb(248, 113, 113);

        private static readonly string FontName = Resolve(true, "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI");
        private static readonly string NumberFontName = Resolve(true, "Segoe UI Variable Display Semib", "Segoe UI Semibold", "Segoe UI");
        private static readonly string IconFontName = Resolve(false, "Segoe Fluent Icons", "Segoe MDL2 Assets");
        private static readonly Dictionary<string, Font> fontCache = new Dictionary<string, Font>();
        private static float scale = 0;

        private static string Resolve(bool fallback, params string[] names)
        {
            string[] installed = FontFamily.Families.Select(f => f.Name).ToArray();
            foreach (string name in names)
                if (installed.Any(x => String.Equals(x, name, StringComparison.OrdinalIgnoreCase))) return name;
            return fallback ? SystemFonts.MessageBoxFont.FontFamily.Name : null;
        }

        public static bool HasIcons { get { return IconFontName != null; } }

        /// <summary>Screen DPI / 96. Fonts are in points and scale on their own; pixel sizes go through S().</summary>
        public static float Scale
        {
            get
            {
                if (scale <= 0)
                {
                    try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = Math.Max(1f, g.DpiX / 96f); }
                    catch (Exception) { scale = 1f; }
                }
                return scale;
            }
        }

        public static int S(int pixels) { return (int)Math.Round(pixels * Scale); }
        public static Padding S(int left, int top, int right, int bottom) { return new Padding(S(left), S(top), S(right), S(bottom)); }
        public static Padding S4(int all) { return new Padding(S(all)); }

        /// <summary>New font owned by the caller.</summary>
        public static Font Font(float size, bool bold = false)
        {
            return new Font(FontName, size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point);
        }

        private static Font Cached(string family, float size, FontStyle style)
        {
            string key = family + "|" + size + "|" + (int)style;
            Font f;
            if (!fontCache.TryGetValue(key, out f)) { f = new Font(family, size, style, GraphicsUnit.Point); fontCache[key] = f; }
            return f;
        }
        /// <summary>Shared fonts for painting and labels. Never dispose these.</summary>
        public static Font F(float size, bool bold = false) { return Cached(FontName, size, bold ? FontStyle.Bold : FontStyle.Regular); }
        public static Font Num(float size) { return Cached(NumberFontName, size, FontStyle.Regular); }
        public static Font Icon(float size) { return IconFontName == null ? F(size) : Cached(IconFontName, size, FontStyle.Regular); }

        public static Color Mix(Color a, Color b, float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }
        public static Color Alpha(Color c, int alpha) { return Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), c.R, c.G, c.B); }

        public static Label Label(string text, float size = 10, bool bold = false, Color? color = null)
        {
            return new Label {
                Text = text, Font = F(size, bold), ForeColor = color ?? Text, BackColor = Color.Transparent,
                AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
                UseCompatibleTextRendering = false, Padding = new Padding(0), Margin = new Padding(0)
            };
        }

        public static RoundButton Button(string text, bool primary = false)
        {
            return new RoundButton { Text = text, Kind = primary ? ButtonKind.Primary : ButtonKind.Secondary, Height = S(40), Width = S(128), Margin = new Padding(0), TabStop = true };
        }

        public static TextBox Input()
        {
            return new TextBox { BackColor = Surface, ForeColor = Text, BorderStyle = BorderStyle.FixedSingle, Font = Font(10.5f), Margin = new Padding(0) };
        }

        public static NumericUpDown Number(int min, int max, int value)
        {
            return new NumericUpDown {
                Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, value)),
                BackColor = Surface, ForeColor = Text, BorderStyle = BorderStyle.FixedSingle,
                Font = Font(10.5f, true), Width = S(110), Margin = new Padding(0), TextAlign = HorizontalAlignment.Center
            };
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>Solid colour behind a control (approximation for gradients).</summary>
        public static Color PaintColorBehind(Control c)
        {
            for (Control p = c.Parent; p != null; p = p.Parent)
            {
                RoundPanel card = p as RoundPanel;
                if (card != null) return card.FillColor2.IsEmpty ? card.FillColor : Mix(card.FillColor, card.FillColor2, .5f);
                if (p.BackColor.A == 255) return p.BackColor;
            }
            return Bg;
        }

        /// <summary>Paints exactly what is behind a control, including a gradient card, so rounded corners blend.</summary>
        public static void PaintBehind(Control c, Graphics g)
        {
            for (Control p = c.Parent; p != null; p = p.Parent)
            {
                RoundPanel card = p as RoundPanel;
                if (card != null)
                {
                    if (card.FillColor2.IsEmpty || !c.IsHandleCreated || !card.IsHandleCreated) { g.Clear(PaintColorBehind(c)); return; }
                    Point offset = card.PointToClient(c.PointToScreen(Point.Empty));
                    card.PaintFillAt(g, offset, c.Size);
                    return;
                }
                if (p.BackColor.A == 255) { g.Clear(p.BackColor); return; }
            }
            g.Clear(Bg);
        }

        public static void DrawShadow(Graphics g, RectangleF r, float radius, float spread, int alpha)
        {
            for (int i = 4; i >= 1; i--)
            {
                float grow = spread * i / 4f;
                RectangleF s = new RectangleF(r.X - grow * .5f, r.Y + spread * .45f - grow * .2f, r.Width + grow, r.Height + grow * .6f);
                using (GraphicsPath p = RoundRect(s, radius + grow * .5f))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(Math.Max(1, alpha / (i + 1)), 15, 23, 42)))
                    g.FillPath(b, p);
            }
        }

        public static void DrawGlyph(Graphics g, string glyph, float size, Color color, Rectangle rect)
        {
            if (String.IsNullOrEmpty(glyph) || !HasIcons) return;
            TextRenderer.DrawText(g, glyph, Icon(size), rect, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        public static void DrawLabel(Graphics g, string text, Font font, Rectangle rect, Color color, TextFormatFlags align)
        {
            TextRenderer.DrawText(g, text ?? "", font, rect, color, align | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private static void TitleBar(Form f, int dark)
        {
            try
            {
                DwmSetWindowAttribute(f.Handle, 20, ref dark, 4);
                int backdrop = 2; // Mica on Windows 11; ignored elsewhere
                DwmSetWindowAttribute(f.Handle, 38, ref backdrop, 4);
                int corner = 2;
                DwmSetWindowAttribute(f.Handle, 33, ref corner, 4);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }
        public static void LightTitleBar(Form f) { TitleBar(f, 0); }
        public static void DarkTitleBar(Form f) { TitleBar(f, 1); }
    }

    /// <summary>Tiny UI-thread animation engine (60 fps, ease-out). Disabled for screenshots.</summary>
    public static class Anim
    {
        public static bool Disabled;
        private sealed class Item { public Control Owner; public int Start; public int Duration; public Action<float> Step; public Action Done; public bool Linear; }
        private static readonly List<Item> items = new List<Item>();
        private static System.Windows.Forms.Timer timer;

        public static float EaseOut(float t) { t = 1 - t; return 1 - t * t * t; }

        public static void Run(Control owner, int ms, Action<float> step, Action done = null) { Run(owner, ms, step, done, false); }

        public static void Run(Control owner, int ms, Action<float> step, Action done, bool linear)
        {
            if (Disabled || ms <= 0 || owner == null || owner.IsDisposed)
            {
                if (step != null) step(1f);
                if (done != null) done();
                return;
            }
            items.Add(new Item { Owner = owner, Start = Environment.TickCount, Duration = ms, Step = step, Done = done, Linear = linear });
            if (timer == null) { timer = new System.Windows.Forms.Timer { Interval = 15 }; timer.Tick += Tick; }
            if (!timer.Enabled) timer.Start();
            if (step != null) step(0f);
        }

        private static void Tick(object sender, EventArgs e)
        {
            int now = Environment.TickCount;
            foreach (Item it in items.ToArray())
            {
                if (it.Owner.IsDisposed) { items.Remove(it); continue; }
                float t = Math.Min(1f, (now - it.Start) / (float)it.Duration);
                try { if (it.Step != null) it.Step(it.Linear ? t : EaseOut(t)); }
                catch (Exception) { items.Remove(it); continue; }
                if (t < 1f) continue;
                items.Remove(it);
                if (it.Done != null) { try { it.Done(); } catch (Exception) { } }
            }
            if (items.Count == 0) timer.Stop();
        }
    }

    /// <summary>Shared 30 fps heartbeat for looping effects (glows, shimmer, particles). Off in screenshots or when disabled.</summary>
    public static class Ambient
    {
        public static bool Enabled = true;
        private static readonly List<Control> controls = new List<Control>();
        private static System.Windows.Forms.Timer timer;
        private static readonly int origin = Environment.TickCount;
        public static float Time { get { return Anim.Disabled || !Enabled ? 1.3f : (Environment.TickCount - origin) / 1000f; } }
        public static bool Active { get { return Enabled && !Anim.Disabled; } }
        public static void Attach(Control c)
        {
            c.HandleCreated += delegate { if (!controls.Contains(c)) controls.Add(c); Ensure(); };
            c.Disposed += delegate { controls.Remove(c); };
            if (c.IsHandleCreated && !controls.Contains(c)) { controls.Add(c); Ensure(); }
        }
        private static void Ensure()
        {
            if (Anim.Disabled) return;
            if (timer == null) { timer = new System.Windows.Forms.Timer { Interval = 33 }; timer.Tick += Tick; }
            if (!timer.Enabled) timer.Start();
        }
        private static void Tick(object sender, EventArgs e)
        {
            if (!Enabled) return;
            foreach (Control c in controls.ToArray())
            {
                if (c.IsDisposed) { controls.Remove(c); continue; }
                if (!c.Visible) continue;
                Form f = c.FindForm();
                if (f == null || !f.Visible || f.WindowState == FormWindowState.Minimized) continue;
                c.Invalidate();
            }
            if (controls.Count == 0) timer.Stop();
        }
    }

    /// <summary>Click-through confetti burst drawn in a short-lived transparent window.</summary>
    public sealed class Confetti : Form
    {
        private sealed class Bit { public float X, Y, VX, VY, Angle, Spin, W, H; public Color Color; }
        private static readonly Color Key = Color.FromArgb(255, 0, 254);
        private static readonly Random random = new Random();
        private readonly List<Bit> bits = new List<Bit>();
        private readonly System.Windows.Forms.Timer frame = new System.Windows.Forms.Timer { Interval = 16 };
        private int age;
        private Confetti(int count)
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            BackColor = Key; TransparencyKey = Key; DoubleBuffered = true; TopMost = true;
            Size = new Size(Theme.S(520), Theme.S(520));
            Color[] palette = { Theme.Accent, Theme.Violet, Theme.Green, Theme.Amber, Color.FromArgb(236, 72, 153), Color.FromArgb(6, 182, 212) };
            for (int i = 0; i < count; i++)
            {
                double angle = -Math.PI / 2 + (random.NextDouble() - .5) * Math.PI * 1.3;
                float speed = Theme.S(5) + (float)random.NextDouble() * Theme.S(9);
                bits.Add(new Bit {
                    X = Width / 2f, Y = Height / 2f, VX = (float)Math.Cos(angle) * speed, VY = (float)Math.Sin(angle) * speed,
                    Angle = (float)(random.NextDouble() * 360), Spin = (float)(random.NextDouble() * 24 - 12),
                    W = Theme.S(4) + (float)random.NextDouble() * Theme.S(5), H = Theme.S(7) + (float)random.NextDouble() * Theme.S(7),
                    Color = palette[random.Next(palette.Length)] });
            }
            frame.Tick += delegate {
                age += 16;
                foreach (Bit b in bits) { b.X += b.VX; b.Y += b.VY; b.VY += Theme.S(1) * .42f; b.VX *= .985f; b.Angle += b.Spin; }
                Invalidate();
                if (age > 1300) { frame.Stop(); Close(); }
            };
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams { get { CreateParams p = base.CreateParams; p.ExStyle |= 0x80 | 0x08000000 | 0x20; return p; } }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None; // no anti-aliasing: edges must not blend with the transparency key
            foreach (Bit b in bits)
            {
                GraphicsState state = g.Save();
                g.TranslateTransform(b.X, b.Y);
                g.RotateTransform(b.Angle);
                float squash = Math.Abs((float)Math.Cos(b.Angle * Math.PI / 90));
                using (SolidBrush brush = new SolidBrush(b.Color)) g.FillRectangle(brush, -b.W / 2, -b.H * squash / 2, b.W, Math.Max(1, b.H * squash));
                g.Restore(state);
            }
        }
        protected override void Dispose(bool disposing) { if (disposing) frame.Dispose(); base.Dispose(disposing); }
        public static void Burst(Form owner, Point screen, int count = 54)
        {
            if (owner == null || owner.IsDisposed || !owner.Visible || !Ambient.Active) return;
            Confetti c = new Confetti(count);
            c.Location = new Point(screen.X - c.Width / 2, screen.Y - c.Height / 2);
            c.FormClosed += delegate { c.Dispose(); };
            c.Show(owner);
            c.frame.Start();
        }
    }

    /// <summary>Animated decoration for the gradient hero: drifting glow, rotating rings, orbiting dots, rising sparks.</summary>
    public sealed class HeroArt : Control
    {
        public float Progress;
        public string CenterGlyph = Glyph.Play;
        public HeroArt()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Ambient.Attach(this);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.PaintBehind(this, g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float t = Ambient.Time;
            float cx = Width * .52f + (float)Math.Sin(t * .6) * Theme.S(4), cy = Height * .48f + (float)Math.Cos(t * .5) * Theme.S(4);
            float R = Math.Min(Width, Height) * .36f;
            if (R < Theme.S(20)) return;
            using (GraphicsPath glowPath = new GraphicsPath())
            {
                float gr = R * 1.7f;
                glowPath.AddEllipse(cx - gr, cy - gr, gr * 2, gr * 2);
                using (PathGradientBrush glow = new PathGradientBrush(glowPath))
                {
                    glow.CenterColor = Color.FromArgb((int)(52 + 22 * Math.Sin(t * 1.4)), 255, 255, 255);
                    glow.SurroundColors = new[] { Color.FromArgb(0, 255, 255, 255) };
                    g.FillPath(glow, glowPath);
                }
            }
            GraphicsState state = g.Save();
            g.TranslateTransform(cx, cy);
            g.RotateTransform(t * 22);
            using (Pen dash = new Pen(Color.FromArgb(90, 255, 255, 255), 1.6f) { DashPattern = new float[] { 2, 5 } }) g.DrawEllipse(dash, -R, -R, R * 2, R * 2);
            g.RotateTransform(-t * 50);
            float r2 = R * 1.22f;
            using (Pen thin = new Pen(Color.FromArgb(50, 255, 255, 255), 1f)) g.DrawArc(thin, -r2, -r2, r2 * 2, r2 * 2, 0, 250);
            g.Restore(state);
            float r1 = R * .68f;
            using (Pen track = new Pen(Color.FromArgb(60, 255, 255, 255), Theme.S(5))) g.DrawEllipse(track, cx - r1, cy - r1, r1 * 2, r1 * 2);
            float sweep = Math.Max(1, 360 * Math.Min(1, Progress));
            using (Pen arc = new Pen(Color.White, Theme.S(5)) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(arc, cx - r1, cy - r1, r1 * 2, r1 * 2, -90, sweep);
            Theme.DrawGlyph(g, CenterGlyph, 17, Color.White, new Rectangle((int)(cx - r1), (int)(cy - r1), (int)(r1 * 2), (int)(r1 * 2)));
            for (int i = 0; i < 3; i++)
            {
                double a = t * (0.9 + i * .45) * (i % 2 == 0 ? 1 : -1) + i * 2.1;
                float rr = R * (1 + .22f * i);
                float x = cx + (float)Math.Cos(a) * rr, y = cy + (float)Math.Sin(a) * rr;
                float d = Theme.S(3) + i;
                using (SolidBrush halo = new SolidBrush(Color.FromArgb(70, 255, 255, 255))) g.FillEllipse(halo, x - d * 1.8f, y - d * 1.8f, d * 3.6f, d * 3.6f);
                using (SolidBrush dot = new SolidBrush(Color.White)) g.FillEllipse(dot, x - d / 2, y - d / 2, d, d);
            }
            for (int i = 0; i < 12; i++)
            {
                float seed = (i * 37 % 100) / 100f;
                float speed = .25f + seed * .35f;
                float y = Height - ((t * speed * Height * .35f + seed * Height * 1.3f) % (Height + Theme.S(20)));
                float x = Width * ((i * 53 % 100) / 100f) + (float)Math.Sin(t * 1.3 + i) * Theme.S(6);
                int alpha = (int)(110 + 90 * Math.Sin(t * 3 + i * 1.7));
                float d = Theme.S(2) + (i % 3);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), 255, 255, 255))) g.FillEllipse(b, x, y, d, d);
            }
        }
    }

    public static class AppIcons
    {
        private static readonly Dictionary<string, Bitmap> cache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        public static Bitmap Get(string path, int size)
        {
            if (String.IsNullOrWhiteSpace(path)) return null;
            string key = path + "|" + size;
            Bitmap bmp;
            if (cache.TryGetValue(key, out bmp)) return bmp;
            try
            {
                if (File.Exists(path))
                    using (Icon icon = System.Drawing.Icon.ExtractAssociatedIcon(path))
                    using (Bitmap source = icon.ToBitmap())
                    {
                        bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
                        using (Graphics g = Graphics.FromImage(bmp))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.DrawImage(source, new Rectangle(0, 0, size, size));
                        }
                    }
            }
            catch (Exception) { bmp = null; }
            cache[key] = bmp;
            return bmp;
        }
    }

    public class RoundPanel : Panel
    {
        public int Radius = Theme.S(14);
        public Color FillColor = Theme.Surface;
        public Color FillColor2 { get; set; } // Color.Empty = solid fill
        public float GradientAngle = 20f;
        public Color BorderColor = Theme.Stroke;
        public Color AccentBar = Color.Empty;
        public bool Shadow;
        public RoundPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Theme.Bg;
            Padding = Theme.S4(22);
        }
        public RectangleF CardBounds
        {
            get
            {
                if (!Shadow) return new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
                float m = Theme.S(5);
                return new RectangleF(m * .6f, 0.5f, Width - m * 1.2f - 1, Height - m * 1.4f - 1);
            }
        }
        private Brush FillBrush()
        {
            if (FillColor2.IsEmpty || Width < 2 || Height < 2) return new SolidBrush(FillColor);
            return new LinearGradientBrush(new RectangleF(-1, -1, Width + 2, Height + 2), FillColor, FillColor2, GradientAngle);
        }
        /// <summary>Paints this card's fill for a child area (offset in this panel's coordinates).</summary>
        public void PaintFillAt(Graphics g, Point offset, Size size)
        {
            GraphicsState state = g.Save();
            g.TranslateTransform(-offset.X, -offset.Y);
            using (Brush b = FillBrush()) g.FillRectangle(b, offset.X, offset.Y, size.Width, size.Height);
            g.Restore(state);
        }
        protected override void OnPaintBackground(PaintEventArgs e) { Theme.PaintBehind(this, e.Graphics); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Width < 4 || Height < 4) return;
            RectangleF card = CardBounds;
            if (Shadow) Theme.DrawShadow(g, card, Radius, Theme.S(5), 46);
            using (GraphicsPath p = Theme.RoundRect(card, Radius))
            {
                using (Brush b = FillBrush()) g.FillPath(b, p);
                if (AccentBar != Color.Empty)
                {
                    Region old = g.Clip;
                    g.SetClip(p, CombineMode.Intersect);
                    using (SolidBrush bar = new SolidBrush(AccentBar)) g.FillRectangle(bar, card.X, card.Y, Theme.S(4), card.Height);
                    g.Clip = old;
                }
                if (BorderColor.A > 0 && FillColor2.IsEmpty)
                    using (Pen pen = new Pen(BorderColor)) g.DrawPath(pen, p);
            }
        }
    }

    public enum ButtonKind { Secondary, Primary, Ghost, OnHero, Inverse, Dark, DarkPrimary, Danger }

    public class RoundButton : Button
    {
        private ButtonKind kind = ButtonKind.Secondary;
        public ButtonKind Kind { get { return kind; } set { kind = value; Invalidate(); } }
        public bool Primary { get { return kind == ButtonKind.Primary; } set { Kind = value ? ButtonKind.Primary : (kind == ButtonKind.Primary ? ButtonKind.Secondary : kind); } }
        public bool Selected { get; set; }
        public bool NavStyle { get; set; }
        public string Glyph = "";
        public int Corner = -1;
        private bool shimmer;
        /// <summary>A soft light band sweeps across the button every few seconds (call-to-action).</summary>
        public bool Shimmer { get { return shimmer; } set { if (value && !shimmer) Ambient.Attach(this); shimmer = value; } }
        private float hover;
        private bool down;
        private int token;

        public RoundButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Opaque, true);
            Font = Theme.F(10, true);
            Cursor = Cursors.Hand;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
        }

        private void AnimateHover(float to)
        {
            float from = hover;
            int my = ++token;
            Anim.Run(this, 150, delegate(float t) { if (my != token) return; hover = from + (to - from) * t; Invalidate(); });
        }
        protected override void OnMouseEnter(EventArgs e) { AnimateHover(1); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { down = false; AnimateHover(0); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnPaintBackground(PaintEventArgs e) { Theme.PaintBehind(this, e.Graphics); }

        private void Colors(out Color fill, out Color border, out Color fg)
        {
            float h = hover;
            switch (NavStyle ? ButtonKind.Ghost : kind)
            {
                case ButtonKind.Primary:
                    fill = Theme.Mix(Theme.AccentStrong, Theme.AccentHover, h); border = fill; fg = Color.White; break;
                case ButtonKind.Ghost:
                    fill = Theme.Alpha(Theme.Surface2, (int)(255 * Math.Max(h, Selected ? 1 : 0))); border = Color.Transparent;
                    fg = Selected ? Theme.AccentStrong : Theme.Mix(Theme.Text2, Theme.Text, h); break;
                case ButtonKind.OnHero:
                    fill = Color.FromArgb((int)(34 + 34 * h), 255, 255, 255); border = Color.FromArgb(90, 255, 255, 255); fg = Color.White; break;
                case ButtonKind.Inverse:
                    fill = Theme.Mix(Color.White, Color.FromArgb(239, 246, 255), h); border = fill; fg = Theme.AccentStrong; break;
                case ButtonKind.Dark:
                    fill = Theme.Mix(Theme.FocusCard, Theme.FocusCardHover, h); border = Theme.Mix(Theme.FocusStroke, Color.FromArgb(71, 85, 105), h); fg = Theme.FocusText; break;
                case ButtonKind.DarkPrimary:
                    fill = Theme.Mix(Theme.AccentStrong, Theme.Accent, h); border = fill; fg = Color.White; break;
                case ButtonKind.Danger:
                    fill = Theme.Mix(Color.White, Theme.DangerSoft, h); border = Theme.Mix(Theme.Stroke, Color.FromArgb(252, 165, 165), h); fg = Theme.Danger; break;
                default:
                    if (Selected) { fill = Theme.AccentSoft; border = Theme.Accent; fg = Theme.AccentStrong; }
                    else { fill = Theme.Mix(Color.White, Color.FromArgb(248, 250, 252), h); border = Theme.Mix(Theme.Stroke, Color.FromArgb(147, 197, 253), h); fg = Theme.Text; }
                    break;
            }
            if (down && Enabled) fill = Theme.Mix(fill, kind == ButtonKind.Dark || kind == ButtonKind.OnHero ? Color.White : Theme.AccentDeep, .12f);
            if (!Enabled)
            {
                bool dark = kind == ButtonKind.Dark || kind == ButtonKind.DarkPrimary;
                fill = dark ? Theme.FocusCard : kind == ButtonKind.OnHero ? Color.FromArgb(24, 255, 255, 255) : Theme.Surface2;
                border = fill; fg = dark ? Theme.FocusMuted : kind == ButtonKind.OnHero ? Color.FromArgb(180, 200, 230) : Color.FromArgb(148, 163, 184);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.PaintBehind(this, g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill, border, fg;
            Colors(out fill, out border, out fg);
            bool focusRing = Focused && Enabled && ShowFocusCues;
            float radius = Corner >= 0 ? Corner : Theme.S(9);
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (GraphicsPath p = Theme.RoundRect(r, radius))
            {
                using (SolidBrush brush = new SolidBrush(fill)) g.FillPath(brush, p);
                if ((kind == ButtonKind.Primary || kind == ButtonKind.DarkPrimary) && Enabled && Height > 4)
                    using (LinearGradientBrush shine = new LinearGradientBrush(new RectangleF(0, 0, Width, Height / 2f + 1), Color.FromArgb(34, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                        g.FillRectangle(shine, 1, 1, Width - 2, Height / 2f);
                if (shimmer && Enabled && Ambient.Active)
                {
                    float cycle = Ambient.Time % 3.4f;
                    if (cycle < 1.1f)
                    {
                        float band = Height * 1.6f, k = cycle / 1.1f;
                        float x0 = -band + k * (Width + band * 2);
                        Color c = kind == ButtonKind.Inverse || kind == ButtonKind.Secondary ? Theme.Alpha(Theme.Accent, 70) : Color.FromArgb(90, 255, 255, 255);
                        Region old = g.Clip;
                        g.SetClip(p, CombineMode.Intersect);
                        using (LinearGradientBrush light = new LinearGradientBrush(new RectangleF(x0, 0, band, Height), Theme.Alpha(c, 0), Theme.Alpha(c, 0), 20f))
                        {
                            ColorBlend blend = new ColorBlend { Colors = new[] { Theme.Alpha(c, 0), c, Theme.Alpha(c, 0) }, Positions = new[] { 0f, .5f, 1f } };
                            light.InterpolationColors = blend;
                            g.FillRectangle(light, x0, 0, band, Height);
                        }
                        g.Clip = old;
                    }
                }
                if (focusRing) border = kind == ButtonKind.Dark ? Theme.FocusAccent : Theme.AccentStrong;
                if (border.A > 0) using (Pen pen = new Pen(border, focusRing ? 2f : 1f)) g.DrawPath(pen, p);
            }
            if (NavStyle && Selected)
                using (SolidBrush bar = new SolidBrush(Theme.AccentStrong)) g.FillRectangle(bar, 0, Theme.S(10), Theme.S(3), Math.Max(0, Height - Theme.S(20)));
            string text = Text ?? "";
            bool hasGlyph = !String.IsNullOrEmpty(Glyph) && Theme.HasIcons;
            Font iconFont = Theme.Icon(Font.SizeInPoints * .95f);
            Size ts = TextRenderer.MeasureText(g, text, Font, new Size(int.MaxValue, Height), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            int glyphW = hasGlyph ? TextRenderer.MeasureText(g, Glyph, iconFont, Size.Empty, TextFormatFlags.NoPadding).Width : 0;
            int gap = hasGlyph && text.Length > 0 ? Theme.S(7) : 0;
            int inset = NavStyle ? Theme.S(16) : Theme.S(10);
            int total = Math.Min(Width - inset * 2, glyphW + gap + ts.Width);
            int x = NavStyle ? inset : Math.Max(inset, (Width - total) / 2);
            if (hasGlyph)
            {
                TextRenderer.DrawText(g, Glyph, iconFont, new Rectangle(x, 0, glyphW, Height), fg,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                x += glyphW + gap;
            }
            TextRenderer.DrawText(g, text, Font, new Rectangle(x, 0, Math.Max(0, Width - x - inset + 2), Height), fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>Sidebar navigation with a sliding selection highlight.</summary>
    public sealed class NavList : Control
    {
        private sealed class Item { public string Key, Text, Glyph; }
        private readonly List<Item> items = new List<Item>();
        private int selected = -1, hover = -1;
        private float indicator = 0, hoverAlpha, pop = 1;
        private int token;
        public event Action<string> SelectedChanged;
        public int ItemHeight = Theme.S(44);

        public NavList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
            Cursor = Cursors.Hand;
        }
        public void Add(string key, string text, string glyph) { items.Add(new Item { Key = key, Text = text, Glyph = glyph }); Invalidate(); }

        public void Select(string key)
        {
            int index = items.FindIndex(i => i.Key == key);
            if (index < 0 || index == selected) return;
            float from = selected < 0 ? index : indicator;
            selected = index;
            int my = ++token;
            Anim.Run(this, 260, delegate(float t) { if (my != token) return; indicator = from + (index - from) * t; pop = t; Invalidate(); });
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = e.Y / ItemHeight;
            if (h >= items.Count) h = -1;
            if (h != hover)
            {
                hover = h;
                Anim.Run(this, 120, delegate(float t) { hoverAlpha = t; Invalidate(); });
            }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            int index = e.Y / ItemHeight;
            if (index >= 0 && index < items.Count && index != selected)
            {
                Select(items[index].Key);
                if (SelectedChanged != null) SelectedChanged(items[index].Key);
            }
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int pad = Theme.S(3);
            if (hover >= 0 && hover != selected)
                using (GraphicsPath p = Theme.RoundRect(new RectangleF(0, hover * ItemHeight + pad, Width - 1, ItemHeight - pad * 2), Theme.S(9)))
                using (SolidBrush b = new SolidBrush(Theme.Alpha(Theme.Surface2, (int)(255 * hoverAlpha)))) g.FillPath(b, p);
            if (selected >= 0)
            {
                float y = indicator * ItemHeight;
                using (GraphicsPath p = Theme.RoundRect(new RectangleF(0, y + pad, Width - 1, ItemHeight - pad * 2), Theme.S(9)))
                using (SolidBrush b = new SolidBrush(Theme.AccentSoft)) g.FillPath(b, p);
                using (GraphicsPath bar = Theme.RoundRect(new RectangleF(0, y + Theme.S(12), Theme.S(3), ItemHeight - Theme.S(24)), Theme.S(2)))
                using (SolidBrush b = new SolidBrush(Theme.AccentStrong)) g.FillPath(b, bar);
            }
            for (int i = 0; i < items.Count; i++)
            {
                bool on = i == selected;
                Color fg = on ? Theme.AccentStrong : i == hover ? Theme.Text : Theme.Text2;
                Rectangle row = new Rectangle(0, i * ItemHeight, Width, ItemHeight);
                float iconSize = on ? 12f * (1f + .35f * (float)Math.Sin(pop * Math.PI)) : 12f;
                Theme.DrawGlyph(g, items[i].Glyph, iconSize, on ? Theme.AccentStrong : Theme.Muted, new Rectangle(Theme.S(10), row.Y, Theme.S(30), row.Height));
                Theme.DrawLabel(g, items[i].Text, Theme.F(10.5f, on), new Rectangle(Theme.S(Theme.HasIcons ? 46 : 16), row.Y, Width - Theme.S(50), row.Height), fg,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine);
            }
        }
    }

    /// <summary>Round check toggle used for "done" marks (animated).</summary>
    public sealed class CheckDot : Control
    {
        private bool isChecked;
        private bool hover;
        private float amount;
        private int token;
        public event EventHandler CheckedChanged;
        public string Caption = "";
        public Color CaptionColor = Theme.Text2;
        public bool Checked
        {
            get { return isChecked; }
            set
            {
                if (isChecked == value) return;
                isChecked = value;
                if (!IsHandleCreated) amount = value ? 1 : 0;
                else
                {
                    float from = amount, to = value ? 1 : 0;
                    int my = ++token;
                    Anim.Run(this, 220, delegate(float t) { if (my != token) return; amount = from + (to - from) * t; Invalidate(); });
                }
                Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }
        public CheckDot()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
            Font = Theme.F(9.5f, true);
            TabStop = true;
        }
        protected override void OnClick(EventArgs e) { Focus(); Checked = !Checked; base.OnClick(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { Checked = !Checked; e.Handled = true; } base.OnKeyDown(e); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.PaintBehind(this, g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int d = Math.Min(Height - 2, Theme.S(22));
            RectangleF circle = new RectangleF(1, (Height - d) / 2f, d, d);
            Color ring = Theme.Mix(hover || Focused ? Theme.Accent : Color.FromArgb(203, 213, 225), Theme.Green, amount);
            using (SolidBrush fill = new SolidBrush(Color.White)) g.FillEllipse(fill, circle);
            if (amount > 0.01f)
            {
                float k = .35f + .65f * amount;
                RectangleF inner = new RectangleF(circle.X + d * (1 - k) / 2, circle.Y + d * (1 - k) / 2, d * k, d * k);
                using (SolidBrush fill = new SolidBrush(Theme.Alpha(Theme.Green, (int)(255 * Math.Min(1, amount * 1.4f))))) g.FillEllipse(fill, inner);
            }
            using (Pen pen = new Pen(ring, Focused ? 2f : 1.5f)) g.DrawEllipse(pen, circle);
            if (amount > 0.35f)
                using (Pen tick = new Pen(Theme.Alpha(Color.White, (int)(255 * Math.Min(1, (amount - .35f) / .5f))), Math.Max(2f, d / 9f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(tick, new[] {
                        new PointF(circle.X + d * .28f, circle.Y + d * .52f),
                        new PointF(circle.X + d * .44f, circle.Y + d * .68f),
                        new PointF(circle.X + d * .73f, circle.Y + d * .36f) });
            if (!String.IsNullOrEmpty(Caption))
                TextRenderer.DrawText(g, Caption, Font, new Rectangle(d + Theme.S(8), 0, Math.Max(0, Width - d - Theme.S(8)), Height),
                    isChecked ? Theme.Green : CaptionColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>Small rounded status label.</summary>
    public sealed class Pill : Control
    {
        public Color FillColor = Theme.AccentSoft;
        public string Glyph = "";
        public Pill(string text, Color fg, Color fill) : this(text, fg, fill, null) { }
        public Pill(string text, Color fg, Color fill, string glyph)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Text = text; ForeColor = fg; FillColor = fill; Font = Theme.F(8.5f, true); Glyph = glyph ?? "";
            Size sz = TextRenderer.MeasureText(text, Font);
            int extra = !String.IsNullOrEmpty(Glyph) && Theme.HasIcons ? Theme.S(18) : 0;
            Size = new Size(sz.Width + Theme.S(18) + extra, Theme.S(26));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.PaintBehind(this, g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Theme.RoundRect(new RectangleF(0, 0, Width - 1, Height - 1), Height / 2f))
            using (SolidBrush b = new SolidBrush(FillColor)) g.FillPath(b, p);
            int x = Theme.S(9);
            if (!String.IsNullOrEmpty(Glyph) && Theme.HasIcons)
            {
                Theme.DrawGlyph(g, Glyph, 8, ForeColor, new Rectangle(x, 0, Theme.S(14), Height));
                x += Theme.S(18);
            }
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Width - x - Theme.S(6), Height), ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>Thin rounded progress bar that fills in when first shown.</summary>
    public sealed class MiniBar : Control
    {
        private float value, shown;
        public Color BarColor = Theme.AccentStrong;
        public Color TrackColor = Theme.Surface2;
        private bool shimmer;
        public bool Shimmer { get { return shimmer; } set { if (value && !shimmer) Ambient.Attach(this); shimmer = value; } }
        public MiniBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        public float Value
        {
            get { return value; }
            set
            {
                float target = Math.Max(0, Math.Min(1, value));
                this.value = target;
                if (!IsHandleCreated) { shown = target; Invalidate(); return; }
                float from = shown;
                Anim.Run(this, 600, delegate(float t) { shown = from + (target - from) * t; Invalidate(); });
            }
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            float target = value;
            Anim.Run(this, 700, delegate(float t) { shown = target * t; Invalidate(); });
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.PaintBehind(this, g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float h = Math.Min(Height, Theme.S(6));
            RectangleF track = new RectangleF(0, (Height - h) / 2, Width - 1, h);
            using (GraphicsPath p = Theme.RoundRect(track, h / 2)) using (SolidBrush b = new SolidBrush(TrackColor)) g.FillPath(b, p);
            if (shown <= 0.001f) return;
            RectangleF fill = new RectangleF(track.X, track.Y, Math.Max(h, track.Width * shown), h);
            using (GraphicsPath p = Theme.RoundRect(fill, h / 2))
            {
                using (SolidBrush b = new SolidBrush(BarColor)) g.FillPath(b, p);
                if (shimmer && Ambient.Active && fill.Width > h * 2)
                {
                    float k = (Ambient.Time % 2.2f) / 2.2f, band = Math.Max(Theme.S(24), fill.Width * .35f);
                    float x0 = fill.X - band + k * (fill.Width + band * 2);
                    Region old = g.Clip;
                    g.SetClip(p, CombineMode.Intersect);
                    using (LinearGradientBrush light = new LinearGradientBrush(new RectangleF(x0, 0, band, Height), Color.Transparent, Color.Transparent, 0f))
                    {
                        light.InterpolationColors = new ColorBlend { Colors = new[] { Color.FromArgb(0, 255, 255, 255), Color.FromArgb(150, 255, 255, 255), Color.FromArgb(0, 255, 255, 255) }, Positions = new[] { 0f, .5f, 1f } };
                        g.FillRectangle(light, x0, 0, band, Height);
                    }
                    g.Clip = old;
                }
            }
        }
    }

    /// <summary>Application icon in a rounded tile (falls back to a generic glyph).</summary>
    public sealed class AppIcon : Control
    {
        public string ExePath = "";
        public bool Dark;
        public AppIcon()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.PaintBehind(this, g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int d = Math.Min(Width, Height) - 2;
            Rectangle tile = new Rectangle((Width - d) / 2, (Height - d) / 2, d, d);
            using (GraphicsPath p = Theme.RoundRect(tile, Theme.S(9)))
            using (SolidBrush b = new SolidBrush(Dark ? Color.FromArgb(30, 41, 64) : Theme.Surface2)) g.FillPath(b, p);
            int size = (int)(d * .62f);
            Bitmap icon = AppIcons.Get(ExePath, size);
            if (icon != null) g.DrawImage(icon, tile.X + (d - size) / 2, tile.Y + (d - size) / 2, size, size);
            else Theme.DrawGlyph(g, Glyph.Apps, 12, Dark ? Theme.FocusMuted : Theme.Muted, tile);
        }
    }

    /// <summary>KPI card: icon, caption, animated number, note, optional progress.</summary>
    public sealed class KpiTile : RoundPanel
    {
        public string GlyphText = Glyph.Clock, Caption = "", Prefix = "", Unit = "", Note = "";
        public int Value;
        public float Progress = -1;
        public Color Tint = Theme.AccentStrong;
        public Color TintSoft = Theme.AccentSoft;
        private float reveal = 1;
        public KpiTile() { Shadow = true; Padding = new Padding(0); }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Anim.Run(this, 800, delegate(float t) { reveal = t; Invalidate(); });
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            RectangleF card = CardBounds;
            int x = (int)card.X + Theme.S(18), y = (int)card.Y + Theme.S(16);
            int icon = Theme.S(34);
            using (GraphicsPath p = Theme.RoundRect(new RectangleF(x, y, icon, icon), Theme.S(9)))
            using (SolidBrush b = new SolidBrush(TintSoft)) g.FillPath(b, p);
            Theme.DrawGlyph(g, GlyphText, 12, Tint, new Rectangle(x, y, icon, icon));
            Theme.DrawLabel(g, Caption, Theme.F(9), new Rectangle(x + icon + Theme.S(10), y, (int)card.Width - icon - Theme.S(46), icon), Theme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine);
            int vy = y + icon + Theme.S(6);
            string number = Prefix + ((int)Math.Round(Value * reveal)).ToString();
            Font numberFont = Theme.Num(20);
            Size ns = TextRenderer.MeasureText(g, number, numberFont, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, number, numberFont, new Point(x - Theme.S(1), vy), Theme.Text, TextFormatFlags.NoPadding);
            if (!String.IsNullOrEmpty(Unit))
                TextRenderer.DrawText(g, Unit, Theme.F(9), new Rectangle(x + ns.Width + Theme.S(4), vy, Theme.S(80), ns.Height - Theme.S(4)), Theme.Muted,
                    TextFormatFlags.Bottom | TextFormatFlags.Left | TextFormatFlags.NoPadding);
            Theme.DrawLabel(g, Note, Theme.F(8.5f), new Rectangle(x + ns.Width + Theme.S(40), vy, (int)card.Right - x - ns.Width - Theme.S(58), ns.Height - Theme.S(4)), Theme.Muted,
                TextFormatFlags.Bottom | TextFormatFlags.Right | TextFormatFlags.SingleLine);
            if (Progress >= 0)
            {
                float h = Theme.S(5), w = card.Width - Theme.S(36);
                RectangleF track = new RectangleF(x, card.Bottom - Theme.S(16), w, h);
                using (GraphicsPath p = Theme.RoundRect(track, h / 2)) using (SolidBrush b = new SolidBrush(Theme.Surface2)) g.FillPath(b, p);
                float v = Math.Min(1, Progress) * reveal;
                if (v > 0.005f)
                    using (GraphicsPath p = Theme.RoundRect(new RectangleF(track.X, track.Y, Math.Max(h, w * v), h), h / 2)) using (SolidBrush b = new SolidBrush(Tint)) g.FillPath(b, p);
            }
        }
    }

    /// <summary>Owned, fading notification ("已保存").</summary>
    public sealed class Toast : Form
    {
        private readonly string message, glyph;
        private readonly Color accent;
        private Toast(string message, Color accent, string glyph)
        {
            this.message = message; this.accent = accent; this.glyph = glyph;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(15, 23, 42); DoubleBuffered = true;
            Size ts = TextRenderer.MeasureText(message, Theme.F(10, true));
            Size = new Size(ts.Width + Theme.S(66), Theme.S(46));
            using (GraphicsPath p = Theme.RoundRect(new RectangleF(0, 0, Width, Height), Theme.S(12))) Region = new Region(p);
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams p = base.CreateParams; p.ExStyle |= 0x80 | 0x08000000; return p; }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int d = Theme.S(24);
            Rectangle dot = new Rectangle(Theme.S(14), (Height - d) / 2, d, d);
            using (SolidBrush b = new SolidBrush(accent)) g.FillEllipse(b, dot);
            Theme.DrawGlyph(g, glyph, 8.5f, Color.White, dot);
            Theme.DrawLabel(g, message, Theme.F(10, true), new Rectangle(Theme.S(48), 0, Width - Theme.S(56), Height), Color.White,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine);
        }
        public static void Pop(Form owner, string message, bool success = true)
        {
            if (owner == null || owner.IsDisposed || !owner.Visible || Anim.Disabled) return;
            Toast t = new Toast(message, success ? Theme.Green : Theme.Amber, success ? Glyph.Check : Glyph.Info);
            Point target = owner.PointToScreen(new Point(owner.ClientSize.Width - t.Width - Theme.S(28), Theme.S(22)));
            t.Location = new Point(target.X, target.Y - Theme.S(10));
            t.Opacity = 0;
            t.Show(owner);
            Anim.Run(t, 220, delegate(float k) { t.Opacity = k; t.Top = target.Y - (int)(Theme.S(10) * (1 - k)); }, delegate {
                Anim.Run(t, 1700, null, delegate {
                    Anim.Run(t, 260, delegate(float k) { t.Opacity = 1 - k; }, delegate { t.Close(); t.Dispose(); });
                });
            });
        }
    }

    /// <summary>Page transition: the old page fades out, then each card of the new page rises in, one after another.</summary>
    public sealed class PageFade : Control
    {
        private readonly Bitmap before;
        private Bitmap after;
        private List<Rectangle> cards = new List<Rectangle>();
        private float ms, total = 1;
        private const int Stagger = 55, CardTime = 320, OutTime = 150;
        public PageFade(Bitmap before)
        {
            this.before = before;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
        }
        public void Play(Bitmap next, Action done) { Play(next, null, done); }
        public void Play(Bitmap next, List<Rectangle> rects, Action done)
        {
            after = next;
            cards = rects ?? new List<Rectangle>();
            total = cards.Count == 0 ? 240 : OutTime / 2 + Math.Min(9, cards.Count - 1) * Stagger + CardTime;
            Anim.Run(this, (int)total, delegate(float k) { ms = k * total; Invalidate(); Update(); }, done, true);
        }
        private static void Blit(Graphics g, Bitmap src, Rectangle from, Point to, float alpha)
        {
            if (alpha >= .999f) { g.DrawImage(src, new Rectangle(to, from.Size), from, GraphicsUnit.Pixel); return; }
            using (ImageAttributes attributes = new ImageAttributes())
            {
                ColorMatrix m = new ColorMatrix { Matrix33 = alpha };
                attributes.SetColorMatrix(m);
                g.DrawImage(src, new Rectangle(to, from.Size), from.X, from.Y, from.Width, from.Height, GraphicsUnit.Pixel, attributes);
            }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Bg);
            float fadeOut = 1 - Math.Min(1, ms / OutTime);
            if (fadeOut > 0) Blit(g, before, new Rectangle(Point.Empty, before.Size), Point.Empty, fadeOut);
            if (after == null) return;
            if (cards.Count == 0)
            {
                float k = Anim.EaseOut(Math.Min(1, ms / 240f));
                Blit(g, after, new Rectangle(Point.Empty, after.Size), new Point(0, (int)(Theme.S(14) * (1 - k))), k);
                return;
            }
            for (int i = 0; i < cards.Count; i++)
            {
                float local = (ms - OutTime / 2 - Math.Min(9, i) * Stagger) / CardTime;
                if (local <= 0) continue;
                float k = Anim.EaseOut(Math.Min(1, local));
                Rectangle r = Rectangle.Intersect(cards[i], new Rectangle(Point.Empty, after.Size));
                if (r.Width <= 0 || r.Height <= 0) continue;
                Blit(g, after, r, new Point(r.X, r.Y + (int)(Theme.S(22) * (1 - k))), k);
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { before.Dispose(); if (after != null) after.Dispose(); }
            base.Dispose(disposing);
        }
    }
    /// <summary>24-hour clock ring: inner ring = 00:00-12:00, outer ring = 12:00-24:00. Sweeps in when shown.</summary>
    public sealed class TimeRing : Control
    {
        public List<FocusRecord> Records = new List<FocusRecord>();
        public int TodaySeconds;
        public int GoalMinutes = 180;
        private float reveal = 1;

        public TimeRing()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
            Ambient.Attach(this);
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Anim.Run(this, 900, delegate(float t) { reveal = t; Invalidate(); });
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.PaintBehind(this, g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int legendH = Theme.S(22);
            float labelGap = Theme.S(16);
            float size = Math.Min(Width, Height - legendH) - labelGap * 2 - Theme.S(6);
            if (size < Theme.S(90)) return;
            float stroke = Math.Max(6f, size / 16f);
            float cx = Width / 2f, cy = (Height - legendH) / 2f;
            RectangleF outer = new RectangleF(cx - size / 2, cy - size / 2, size, size);
            float gap = stroke * 1.85f;
            RectangleF inner = new RectangleF(outer.X + gap, outer.Y + gap, size - gap * 2, size - gap * 2);
            using (Pen basePen = new Pen(Color.FromArgb(237, 241, 246), stroke))
            {
                g.DrawEllipse(basePen, outer);
                g.DrawEllipse(basePen, inner);
            }
            DateTime day = DateTime.Today;
            foreach (FocusRecord r in Records)
            {
                if (r.IsTest || r.ActualSeconds <= 0) continue;
                DateTime start = r.StartedAt.ToLocalTime();
                DateTime end = r.EndedAt.ToLocalTime();
                if (end <= day || start >= day.AddDays(1)) continue;
                if (start < day) start = day;
                if (end > day.AddDays(1)) end = day.AddDays(1);
                double a = (start - day).TotalMinutes;
                double b = a + ((end - day).TotalMinutes - a) * reveal;
                DrawSlice(g, inner, a, Math.Min(720, b), 0, Theme.Accent, stroke);
                DrawSlice(g, outer, Math.Max(720, a), b, 720, Theme.Violet, stroke);
            }
            double nowMin = (DateTime.Now - day).TotalMinutes;
            RectangleF ringNow = nowMin < 720 ? inner : outer;
            double theta = ((nowMin % 720) / 2 - 90) * Math.PI / 180;
            float rx = ringNow.Width / 2;
            PointF dot = new PointF(cx + (float)Math.Cos(theta) * rx, cy + (float)Math.Sin(theta) * rx);
            float dr = stroke * .62f;
            float beat = (Ambient.Time % 1.8f) / 1.8f;
            float hr = dr * (1.4f + 1.6f * beat);
            using (SolidBrush halo = new SolidBrush(Theme.Alpha(Theme.AccentStrong, (int)(90 * (1 - beat))))) g.FillEllipse(halo, dot.X - hr, dot.Y - hr, hr * 2, hr * 2);
            using (SolidBrush white = new SolidBrush(Color.White)) g.FillEllipse(white, dot.X - dr - 2, dot.Y - dr - 2, (dr + 2) * 2, (dr + 2) * 2);
            using (SolidBrush mark = new SolidBrush(Theme.AccentStrong)) g.FillEllipse(mark, dot.X - dr, dot.Y - dr, dr * 2, dr * 2);
            string[] labels = { "12", "3", "6", "9" };
            for (int i = 0; i < 4; i++)
            {
                double t = (-90 + i * 90) * Math.PI / 180;
                float radius = size / 2 + labelGap * .55f + stroke / 2;
                PointF c = new PointF(cx + (float)Math.Cos(t) * radius, cy + (float)Math.Sin(t) * radius);
                Theme.DrawLabel(g, labels[i], Theme.F(8), new Rectangle((int)c.X - Theme.S(14), (int)c.Y - Theme.S(9), Theme.S(28), Theme.S(18)), Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            int minutes = (int)Math.Round(TodaySeconds / 60 * reveal);
            int pct = GoalMinutes <= 0 ? 0 : (int)Math.Min(999, Math.Round(TodaySeconds / 60 * 100.0 / GoalMinutes));
            Font number = Theme.Num(Math.Max(16f, size / Theme.Scale / 6.4f));
            int nh = TextRenderer.MeasureText("0", number).Height;
            Theme.DrawLabel(g, minutes.ToString(), number, new Rectangle(0, (int)(cy - nh * .78f), Width, nh), Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            Theme.DrawLabel(g, "分钟 / 目标 " + GoalMinutes, Theme.F(8.5f), new Rectangle(0, (int)(cy + nh * .2f), Width, Theme.S(18)), Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            Theme.DrawLabel(g, pct >= 100 ? "今日目标已达成" : "完成 " + pct + "%", Theme.F(8.5f, true), new Rectangle(0, (int)(cy + nh * .2f) + Theme.S(18), Width, Theme.S(18)),
                pct >= 100 ? Theme.Green : Theme.AccentStrong, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            string la = "上午", lb = "下午";
            Font lf = Theme.F(8);
            int sw = Theme.S(8), sp = Theme.S(5), between = Theme.S(16);
            int wa = TextRenderer.MeasureText(la, lf).Width, wb = TextRenderer.MeasureText(lb, lf).Width;
            int x = (Width - (sw + sp + wa + between + sw + sp + wb)) / 2, y = Height - legendH / 2;
            using (SolidBrush am = new SolidBrush(Theme.Accent)) g.FillEllipse(am, x, y - sw / 2, sw, sw);
            x += sw + sp; Theme.DrawLabel(g, la, lf, new Rectangle(x, y - Theme.S(9), wa + 2, Theme.S(18)), Theme.Muted, TextFormatFlags.VerticalCenter); x += wa + between;
            using (SolidBrush pm = new SolidBrush(Theme.Violet)) g.FillEllipse(pm, x, y - sw / 2, sw, sw);
            x += sw + sp; Theme.DrawLabel(g, lb, lf, new Rectangle(x, y - Theme.S(9), wb + 2, Theme.S(18)), Theme.Muted, TextFormatFlags.VerticalCenter);
        }

        private static void DrawSlice(Graphics g, RectangleF rect, double start, double end, double offset, Color color, float stroke)
        {
            if (end <= start) return;
            using (Pen pen = new Pen(color, stroke))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawArc(pen, rect, (float)((start - offset) / 2 - 90), (float)Math.Min(359.9, Math.Max(.5, (end - start) / 2)));
            }
        }
    }

    /// <summary>Focus-mode progress ring: smooth sweep, gradient stroke, soft glow, spinner while preparing.</summary>
    public sealed class FocusRing : Control
    {
        public Func<float> Progress;
        public string TimeText = "", Caption = "", SubCaption = "";
        public bool IsBreak;
        private readonly System.Windows.Forms.Timer frame = new System.Windows.Forms.Timer { Interval = 33 };
        private float phase;
        private float rippleAt = -10, celebrateAt = -10;
        private string lastTime = "";
        public void Celebrate() { celebrateAt = phase; }
        public FocusRing()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.FocusBg;
            frame.Tick += delegate { phase += .033f; Invalidate(); };
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); if (!Anim.Disabled) frame.Start(); }
        protected override void Dispose(bool disposing) { if (disposing) { frame.Stop(); frame.Dispose(); } base.Dispose(disposing); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.FocusBg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float size = Math.Min(Width, Height) - Theme.S(40);
            if (size < Theme.S(120)) return;
            float stroke = Math.Max(8f, size / 24f);
            float cx = Width / 2f, cy = Height / 2f;
            RectangleF ring = new RectangleF(cx - size / 2, cy - size / 2, size, size);
            Color a = IsBreak ? Theme.FocusGreen : Theme.FocusAccent;
            Color b = IsBreak ? Theme.FocusTeal : Theme.FocusViolet;
            // soft glow (breathes during breaks)
            float breathe = IsBreak ? .5f + .5f * (float)Math.Sin(phase * 2.2) : .45f + .2f * (float)Math.Sin(phase * 1.1);
            using (GraphicsPath glowPath = new GraphicsPath())
            {
                float gr = size * .72f;
                glowPath.AddEllipse(cx - gr, cy - gr, gr * 2, gr * 2);
                using (PathGradientBrush glow = new PathGradientBrush(glowPath))
                {
                    glow.CenterColor = Theme.Alpha(a, (int)(26 + 22 * breathe));
                    glow.SurroundColors = new[] { Theme.Alpha(Theme.FocusBg, 0) };
                    g.FillPath(glow, glowPath);
                }
            }
            using (Pen track = new Pen(Color.FromArgb(28, 38, 60), stroke)) g.DrawEllipse(track, ring);
            bool lively = Ambient.Active;
            if (lively)
            {
                // orbiting particles
                for (int i = 0; i < 34; i++)
                {
                    float seed = ((i * 73) % 100) / 100f, seed2 = ((i * 41) % 100) / 100f;
                    float orbit = size / 2 + Theme.S(16) + seed * Theme.S(70);
                    double angle = seed2 * Math.PI * 2 + phase * (.05 + seed * .22) * (i % 2 == 0 ? 1 : -1);
                    float x = cx + (float)Math.Cos(angle) * orbit, y = cy + (float)Math.Sin(angle) * orbit;
                    int alpha = (int)(70 + 70 * Math.Sin(phase * (1.5 + seed2 * 2) + i));
                    float d = Theme.S(1) + seed2 * Theme.S(3);
                    using (SolidBrush dot = new SolidBrush(Theme.Alpha(Theme.Mix(a, b, seed), Math.Max(10, alpha)))) g.FillEllipse(dot, x - d / 2, y - d / 2, d, d);
                }
                // a soft ripple every time the clock ticks
                if (TimeText != lastTime) { lastTime = TimeText; rippleAt = phase; }
                float rk = (phase - rippleAt) / .9f;
                if (rk >= 0 && rk < 1)
                {
                    float rr = size / 2 + stroke / 2 + rk * Theme.S(46);
                    using (Pen ripple = new Pen(Theme.Alpha(a, (int)(70 * (1 - rk))), 2f)) g.DrawEllipse(ripple, cx - rr, cy - rr, rr * 2, rr * 2);
                }
            }
            if (Progress == null)
            {
                float start = (phase * 280) % 360;
                using (LinearGradientBrush brush = new LinearGradientBrush(ring, a, b, 45f))
                using (Pen pen = new Pen(brush, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(pen, ring, start - 90, 70 + 40 * (float)Math.Sin(phase * 3));
            }
            else
            {
                float p = Math.Max(0, Math.Min(1, Progress()));
                float sweep = Math.Max(.6f, 360 * p);
                using (LinearGradientBrush brush = new LinearGradientBrush(ring, a, b, 45f))
                using (Pen pen = new Pen(brush, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(pen, ring, -90, sweep);
                double th = (sweep - 90) * Math.PI / 180;
                PointF tip = new PointF(cx + (float)Math.Cos(th) * size / 2, cy + (float)Math.Sin(th) * size / 2);
                float r = stroke * .95f;
                if (lively)
                    for (int s = 1; s <= 7; s++)
                    {
                        double back = (sweep - 90 - s * 3.2) * Math.PI / 180;
                        float jitter = (float)Math.Sin(phase * 9 + s * 2.3) * stroke * .35f;
                        float sr = size / 2 + jitter;
                        float sx = cx + (float)Math.Cos(back) * sr, sy = cy + (float)Math.Sin(back) * sr;
                        float sd = r * (1.1f - s * .12f);
                        using (SolidBrush spark = new SolidBrush(Theme.Alpha(Color.White, 150 - s * 18))) g.FillEllipse(spark, sx - sd / 2, sy - sd / 2, sd, sd);
                    }
                float pulse = lively ? 1.6f + .35f * (float)Math.Sin(phase * 4) : 1.6f;
                using (SolidBrush halo = new SolidBrush(Theme.Alpha(b, 70))) g.FillEllipse(halo, tip.X - r * pulse, tip.Y - r * pulse, r * pulse * 2, r * pulse * 2);
                using (SolidBrush knob = new SolidBrush(Color.White)) g.FillEllipse(knob, tip.X - r * .62f, tip.Y - r * .62f, r * 1.24f, r * 1.24f);
            }
            float ck = (phase - celebrateAt) / 1.6f;
            if (ck >= 0 && ck < 1)
            {
                // round finished: the ring flashes and a ring of sparks bursts outwards
                using (Pen flash = new Pen(Theme.Alpha(Color.White, (int)(160 * (1 - ck))), stroke * (1 + ck))) g.DrawEllipse(flash, ring);
                for (int i = 0; i < 40; i++)
                {
                    double angle = i * Math.PI * 2 / 40 + i * .37;
                    float dist = size / 2 + Anim.EaseOut(ck) * Theme.S(150) * (.6f + ((i * 29) % 10) / 25f);
                    float x = cx + (float)Math.Cos(angle) * dist, y = cy + (float)Math.Sin(angle) * dist;
                    float d = Theme.S(3) + (i % 3) * Theme.S(1);
                    Color c = i % 3 == 0 ? Theme.FocusGreen : i % 3 == 1 ? a : b;
                    using (SolidBrush spark = new SolidBrush(Theme.Alpha(c, (int)(255 * (1 - ck))))) g.FillEllipse(spark, x - d / 2, y - d / 2, d, d);
                }
            }
            Font timeFont = Theme.Num(Math.Max(28f, size / Theme.Scale / 5.6f));
            int th2 = TextRenderer.MeasureText("0", timeFont).Height;
            Theme.DrawLabel(g, Caption, Theme.F(11, true), new Rectangle(0, (int)(cy - th2 * .5f - Theme.S(34)), Width, Theme.S(26)), IsBreak ? Theme.FocusGreen : Theme.FocusAccent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            Theme.DrawLabel(g, TimeText, timeFont, new Rectangle(0, (int)(cy - th2 * .5f), Width, th2), Theme.FocusText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            Theme.DrawLabel(g, SubCaption, Theme.F(9.5f), new Rectangle(Theme.S(30), (int)(cy + th2 * .5f + Theme.S(4)), Width - Theme.S(60), Theme.S(24)), Theme.FocusMuted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>Focus-mode application tile: icon, name, state; hover lifts the tile.</summary>
    public sealed class AppTile : Control
    {
        public string ExePath = "", Title = "", Detail = "";
        public bool Running;
        private float hover;
        private int token;
        public AppTile()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            BackColor = Theme.FocusBg;
            Ambient.Attach(this);
        }
        private void To(float target) { float from = hover; int my = ++token; Anim.Run(this, 150, delegate(float t) { if (my != token) return; hover = from + (target - from) * t; Invalidate(); }); }
        protected override void OnMouseEnter(EventArgs e) { To(1); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { To(0); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.FocusBg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float lift = Theme.S(3) * hover;
            RectangleF r = new RectangleF(1, Theme.S(4) - lift, Width - 3, Height - Theme.S(6));
            if (hover > .01f)
                using (GraphicsPath glow = Theme.RoundRect(new RectangleF(r.X, r.Y + Theme.S(4), r.Width, r.Height), Theme.S(14)))
                using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(60 * hover), 0, 0, 0))) g.FillPath(b, glow);
            using (GraphicsPath p = Theme.RoundRect(r, Theme.S(14)))
            {
                using (SolidBrush b = new SolidBrush(Theme.Mix(Theme.FocusCard, Theme.FocusCardHover, hover))) g.FillPath(b, p);
                using (Pen pen = new Pen(Theme.Mix(Theme.FocusStroke, Theme.Alpha(Theme.FocusAccent, 160), hover))) g.DrawPath(pen, p);
            }
            int icon = Theme.S(38);
            int ix = (int)r.X + Theme.S(14), iy = (int)(r.Y + (r.Height - icon) / 2);
            Bitmap bmp = AppIcons.Get(ExePath, icon);
            if (bmp != null) g.DrawImage(bmp, ix, iy, icon, icon);
            else Theme.DrawGlyph(g, Glyph.Apps, 16, Theme.FocusMuted, new Rectangle(ix, iy, icon, icon));
            int tx = ix + icon + Theme.S(12);
            Theme.DrawLabel(g, Title, Theme.F(10.5f, true), new Rectangle(tx, iy - Theme.S(2), Width - tx - Theme.S(12), Theme.S(22)), Theme.FocusText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            int dot = Theme.S(7);
            int dy = iy + Theme.S(28);
            if (Running && Ambient.Active)
            {
                float beat = (Ambient.Time % 1.6f) / 1.6f, hr = dot * (.6f + 1.2f * beat);
                using (SolidBrush halo = new SolidBrush(Theme.Alpha(Theme.FocusGreen, (int)(120 * (1 - beat))))) g.FillEllipse(halo, tx + dot / 2f - hr, dy + Theme.S(1) - hr, hr * 2, hr * 2);
            }
            using (SolidBrush b = new SolidBrush(Running ? Theme.FocusGreen : Theme.FocusMuted)) g.FillEllipse(b, tx, dy - dot / 2 + Theme.S(1), dot, dot);
            Theme.DrawLabel(g, Running ? "运行中 · 点击切换" : "点击打开", Theme.F(8.5f), new Rectangle(tx + dot + Theme.S(6), dy - Theme.S(9), Width - tx - Theme.S(30), Theme.S(20)),
                Running ? Theme.FocusGreen : Theme.FocusMuted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }
}