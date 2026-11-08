using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AiTime
{
    public sealed class PlanApp
    {
        public string Name = "";
        public string Path = "";
        public string Role = "";
        public bool Required;
        public bool Exists { get { return !String.IsNullOrWhiteSpace(Path) && File.Exists(Path); } }
    }

    public sealed class StudyPlan
    {
        public DateTime Date;
        public bool IsWeekend;
        public string PlanKey = "";
        public string Title = "";
        public string Subtitle = "";
        public string Track = "";
        public string Reminder = "";
        /// <summary>Hour at which the day's training starts (weekday 18, weekend 12).</summary>
        public int StartHour;
        /// <summary>Length of one focus round in minutes.</summary>
        public int FocusMinutes;
        /// <summary>Rounds defined by the plan itself (weekend 60 + 120 = 3 x 60). 0 = use the user's setting.</summary>
        public int PlanCycles;
        public string[] CycleLabels = new string[0];
        public int DailyGoalMinutes;
        public int LearningDay = 1;
        public string ReviewTitle = "";
        public string ReviewBody = "";
        public string NewTitle = "";
        public string NewBody = "";
        public string Acceptance = "";
        public string ExtensionTitle = "";
        public string ExtensionBody = "";
        public int ExtensionMinutes;
        public string TaskFile = "";
        public string TaskTitle = "";
        public string DayLabel = "";
        public List<PlanApp> Apps = new List<PlanApp>();
        public List<PlanApp> ExtensionApps = new List<PlanApp>();
        public bool HasTaskFile { get { return !String.IsNullOrWhiteSpace(TaskFile) && File.Exists(TaskFile); } }
    }

    public static class StudyPlanner
    {
        private static readonly string PythonPracticeRoot =
            Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", ".."));
        public const int UpdateHour = 10;

        private static DayOfWeek EffectiveDay(DateTime date)
        {
            // A one-day, user-controlled study override. It never changes the real date or weekly schedule.
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AiTimeWindows", "study-day-override.txt");
            try
            {
                if (File.Exists(path))
                {
                    string[] parts = File.ReadAllText(path, Encoding.UTF8).Trim().Split('|');
                    if (parts.Length == 2 && parts[0] == date.ToString("yyyy-MM-dd")
                        && parts[1] == "Monday") return DayOfWeek.Monday;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return date.DayOfWeek;
        }

        public static StudyPlan Today()
        {
            return ForDate(DateTime.Today, 1);
        }

        public static StudyPlan Today(int learningDay) { return ForDate(DateTime.Today, learningDay); }

        public static StudyPlan ForDate(DateTime date) { return ForDate(date, 1); }

        public static StudyPlan ForDate(DateTime date, int learningDay)
        {
            StudyPlan plan = new StudyPlan();
            plan.LearningDay = Math.Max(1, learningDay);
            plan.Date = date.Date;
            DayOfWeek effectiveDay = EffectiveDay(date);
            plan.IsWeekend = effectiveDay == DayOfWeek.Saturday || effectiveDay == DayOfWeek.Sunday;
            plan.PlanKey = date.ToString("yyyyMMdd") + (plan.IsWeekend ? "-weekend" : "-daily");
            CurrentTask task = FindCurrentTask(plan.LearningDay);
            plan.DayLabel = task.DayLabel;
            plan.TaskFile = task.TaskFile;
            plan.TaskTitle = task.Title;
            plan.Acceptance = task.Acceptance;
            plan.ReviewTitle = String.IsNullOrWhiteSpace(task.ReviewTitle) ? "字典、切片与循环统计" : task.ReviewTitle;
            plan.ReviewBody = String.IsNullOrWhiteSpace(task.ReviewBody)
                ? "先独立写一个短题：用 dict.get() / items() 做计数与均值，再补一个 name[:-1] 切片。"
                : task.ReviewBody;
            plan.NewTitle = String.IsNullOrWhiteSpace(task.NewTitle)
                ? (String.IsNullOrWhiteSpace(task.Title) ? "文件读取与数据清洗" : task.Title)
                : task.NewTitle;
            plan.NewBody = String.IsNullOrWhiteSpace(task.NewBody)
                ? "从文本文件读取开始，逐步练到 CSV、坏数据处理和输出文件。只给 TODO 骨架，完成后再验收。"
                : task.NewBody;

            ExtensionFor(effectiveDay, out plan.ExtensionTitle, out plan.ExtensionBody, out plan.ExtensionMinutes);
            string dayName = DayName(effectiveDay);
            if (task.Extensions.ContainsKey(dayName)) plan.ExtensionBody = task.Extensions[dayName];

            if (plan.IsWeekend)
            {
                plan.Title = date.DayOfWeek == DayOfWeek.Saturday ? "周六深度学习" : "周日复盘与补强";
                plan.Subtitle = "12:00 开始 · 60 分钟知识学习 + 120 分钟实操";
                plan.Track = date.DayOfWeek == DayOfWeek.Saturday ? "Python 主线 + 生信 / AIDD" : "Python 主线 + 错题复盘";
                plan.Reminder = "周末 12:00";
                plan.StartHour = 12;
                plan.FocusMinutes = 60;
                plan.PlanCycles = 3;
                plan.CycleLabels = new[] { "知识学习", "实操 1 / 2", "实操 2 / 2" };
                plan.DailyGoalMinutes = 180;
            }
            else
            {
                plan.Title = String.IsNullOrWhiteSpace(plan.DayLabel) ? "今日两题训练" : plan.DayLabel + " · 两题训练";
                plan.Subtitle = "10:00 更新 · 18:00 开始 · 一旧一新 · 约 15 分钟";
                plan.Track = "Python 二级实操主线";
                plan.Reminder = "每天 18:00";
                plan.StartHour = 18;
                plan.FocusMinutes = 15;
                plan.PlanCycles = 0;
                plan.DailyGoalMinutes = 15;
            }

            plan.Apps = CoreApps(plan.IsWeekend, effectiveDay);
            plan.ExtensionApps = ExtensionApps(effectiveDay);
            return plan;
        }

        public static void ApplyCoreProfile(AppStore store, StudyPlan plan)
        {
            ApplyProfile(store, plan, false);
        }

        public static void ApplyExtensionProfile(AppStore store, StudyPlan plan)
        {
            ApplyProfile(store, plan, true);
        }

        private static void ApplyProfile(AppStore store, StudyPlan plan, bool extension)
        {
            List<PlanApp> specs = extension ? plan.ExtensionApps : plan.Apps;
            List<AllowedApp> next = new List<AllowedApp>();
            foreach (PlanApp spec in specs.Where(a => a.Exists))
                AddUnique(next, new AllowedApp { Name = spec.Name, Path = spec.Path, Arguments = "" });
            if (store.Config.ManualAllowed != null)
                foreach (AllowedApp app in store.Config.ManualAllowed)
                    if (NativeWindows.IsAllowedExecutablePath(app.Path)) AddUnique(next, app);
            if (next.Count == 0)
                throw new InvalidOperationException("当前计划没有检测到可用的学习应用。请先到“应用范围”添加至少一个实际 exe。");
            store.Config.Allowed = next;
            store.Config.Subject = extension ? plan.ExtensionTitle : plan.Title;
            store.Config.Minutes = extension ? Math.Max(1, plan.ExtensionMinutes) : plan.FocusMinutes;
            store.Config.DailyGoalMinutes = plan.DailyGoalMinutes;
            store.Config.LastPlanKey = plan.PlanKey + (extension ? "-extension" : "-core");
            store.SaveConfig();
        }

        /// <summary>Rounds the core session will actually run.</summary>
        public static int CoreCycles(AppStore store, StudyPlan plan)
        {
            return plan.PlanCycles > 0 ? plan.PlanCycles : Math.Max(1, store.Config.Cycles);
        }

        private static void AddUnique(List<AllowedApp> list, AllowedApp app)
        {
            string normalized = NativeWindows.NormalizedPath(app.Path);
            if (list.Any(a => String.Equals(NativeWindows.NormalizedPath(a.Path), normalized, StringComparison.OrdinalIgnoreCase))) return;
            list.Add(new AllowedApp {
                Name = String.IsNullOrWhiteSpace(app.Name) ? Path.GetFileNameWithoutExtension(normalized) : app.Name,
                Path = normalized,
                Arguments = app.Arguments ?? ""
            });
        }

        public static string WeeklyLine(DayOfWeek day)
        {
            if (day == DayOfWeek.Monday || day == DayOfWeek.Wednesday || day == DayOfWeek.Friday)
                return "18:00 两题训练  ·  扩展 C 语言，熟练后衔接 C++ 与软考";
            if (day == DayOfWeek.Tuesday || day == DayOfWeek.Thursday)
                return "18:00 两题训练  ·  扩展生信 / AIDD：pandas、Linux、R、Biopython、GEO、RDKit";
            if (day == DayOfWeek.Saturday)
                return "12:00 周末计划  ·  60 分钟知识学习 + 120 分钟实操，侧重生信 / AIDD";
            return "12:00 周末计划  ·  60 分钟知识整理 + 120 分钟错题复盘与补强";
        }

        public static string DayName(DayOfWeek day)
        {
            switch (day)
            {
                case DayOfWeek.Monday: return "周一";
                case DayOfWeek.Tuesday: return "周二";
                case DayOfWeek.Wednesday: return "周三";
                case DayOfWeek.Thursday: return "周四";
                case DayOfWeek.Friday: return "周五";
                case DayOfWeek.Saturday: return "周六";
                default: return "周日";
            }
        }

        private static void ExtensionFor(DayOfWeek day, out string title, out string body, out int minutes)
        {
            minutes = 30;
            if (day == DayOfWeek.Monday || day == DayOfWeek.Wednesday || day == DayOfWeek.Friday)
            {
                title = "C 语言 → C++ / 软件设计师";
                body = "只加一个短扩展：先练 C 的变量、分支、循环、数组与函数；C 熟练后再进 C++。不挤掉 Python 的一旧一新两题。";
                return;
            }
            if (day == DayOfWeek.Tuesday || day == DayOfWeek.Thursday || day == DayOfWeek.Saturday)
            {
                title = "生物信息学 / AIDD";
                body = "从 pandas、Linux、R、Biopython、GEO、RDKit 中取一个小任务，优先与当前 Python 文件和数据处理能力衔接。";
                return;
            }
            title = "本周错题复盘";
            body = "只复盘本周真实出错的题：先口述错误原因，再独立重写；通过验收后才推进下一 Day。";
        }

        private static string VsCodePath()
        {
            return FirstExisting(@"D:\Microsoft VS Code\Code.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Microsoft VS Code\Code.exe"));
        }

        private static List<PlanApp> CoreApps(bool weekend, DayOfWeek day)
        {
            List<PlanApp> apps = new List<PlanApp>();
            AddDetected(apps, "PyCharm", "Python 编写与运行", true,
                @"D:\PyCharm Community Edition 2025.1.2\bin\pycharm64.exe");
            AddDetected(apps, "Python", "运行与测试", true,
                @"D:\conda\python.exe");
            if (weekend)
                AddDetected(apps, "Visual Studio Code", "扩展课程与数据文件", false, VsCodePath());
            return apps;
        }

        private static List<PlanApp> ExtensionApps(DayOfWeek day)
        {
            List<PlanApp> apps = new List<PlanApp>();
            if (day == DayOfWeek.Monday || day == DayOfWeek.Wednesday || day == DayOfWeek.Friday)
            {
                AddDetected(apps, "Visual Studio Code", "C / C++ 编辑器", true, VsCodePath());
                string compiler = FirstExisting(
                    @"C:\msys64\ucrt64\bin\gcc.exe", @"C:\msys64\mingw64\bin\gcc.exe",
                    @"D:\msys64\ucrt64\bin\gcc.exe", @"D:\mingw64\bin\gcc.exe",
                    @"C:\Program Files\LLVM\bin\clang.exe");
                apps.Add(new PlanApp {
                    Name = String.IsNullOrWhiteSpace(compiler) ? "C / C++ 编译器" : Path.GetFileNameWithoutExtension(compiler),
                    Path = compiler ?? "",
                    Role = String.IsNullOrWhiteSpace(compiler) ? "未检测到编译器，暂不加入白名单" : "编译与运行",
                    Required = false
                });
            }
            else
            {
                AddDetected(apps, "Visual Studio Code", "生信 / AIDD 代码与笔记", true, VsCodePath());
                AddDetected(apps, "Python", "pandas / Biopython / RDKit 运行", true,
                    @"D:\conda\python.exe");
            }
            return apps;
        }

        private static void AddDetected(List<PlanApp> list, string name, string role, bool required, params string[] candidates)
        {
            string path = FirstExisting(candidates);
            list.Add(new PlanApp { Name = name, Path = path ?? "", Role = path == null ? "未检测到" : role, Required = required });
        }

        private static string FirstExisting(params string[] paths)
        {
            foreach (string p in paths)
            {
                if (String.IsNullOrWhiteSpace(p)) continue;
                try { if (File.Exists(p)) return Path.GetFullPath(p); }
                catch (Exception) { }
            }
            return null;
        }

        private sealed class CurrentTask
        {
            public string DayLabel = "";
            public string TaskFile = "";
            public string Title = "";
            public string ReviewTitle = "";
            public string ReviewBody = "";
            public string NewTitle = "";
            public string NewBody = "";
            public string Acceptance = "";
            public Dictionary<string, string> Extensions = new Dictionary<string, string>();
        }

        public static bool HasTask(int learningDay) { return !String.IsNullOrWhiteSpace(FindCurrentTask(learningDay).TaskFile); }

        private static CurrentTask FindCurrentTask(int learningDay)
        {
            CurrentTask result = new CurrentTask();
            result.DayLabel = "Day " + Math.Max(1,learningDay).ToString("00");
            if (!Directory.Exists(PythonPracticeRoot)) return result;
            var candidates = new List<Tuple<int, string>>();
            foreach (string dir in Directory.GetDirectories(PythonPracticeRoot, "Day*_*", SearchOption.TopDirectoryOnly))
            {
                Match m = Regex.Match(Path.GetFileName(dir), @"^Day(\d+)_");
                int number;
                if (m.Success && Int32.TryParse(m.Groups[1].Value, out number))
                    candidates.Add(Tuple.Create(number, dir));
            }
            if (candidates.Count == 0) return result;
            Tuple<int, string> current = candidates.Where(x => x.Item1 == learningDay).OrderBy(x => x.Item2).FirstOrDefault();
            if (current == null) return result;
            string taskPath = Path.Combine(current.Item2, "task.txt");
            if (!File.Exists(taskPath)) return result;
            result.TaskFile = taskPath;
            try { ParseTask(File.ReadAllText(taskPath, Encoding.UTF8), result); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return result;
        }

        /// <summary>
        /// Reads task.txt. Understands both the Day01 layout (目标/要求) and the Day02 layout
        /// (旧知识复习 / 新知识进阶 goals, 【旧知识复习】/【新知识进阶】 headings, 验收条件, 周X扩展｜…).
        /// </summary>
        private static void ParseTask(string text, CurrentTask result)
        {
            string[] lines = text.Replace("\r", "").Split('\n').Select(l => l.Trim()).ToArray();
            string first = lines.FirstOrDefault(x => x.Length > 0) ?? "";
            Match head = Regex.Match(first, @"^Day\s*\d+\s*[｜|:：]\s*(.+?)\s*(（[^）]*）|\([^)]*\))?$");
            result.Title = head.Success ? head.Groups[1].Value.Trim() : first;

            var goals = new List<string>();
            var acceptance = new List<string>();
            string section = "";
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0) continue;
                Match heading = Regex.Match(line, @"^【(旧知识复习|新知识进阶)】\s*(.+)$");
                if (heading.Success)
                {
                    if (heading.Groups[1].Value == "旧知识复习") result.ReviewTitle = heading.Groups[2].Value.Trim();
                    else result.NewTitle = heading.Groups[2].Value.Trim();
                    section = "";
                    continue;
                }
                Match ext = Regex.Match(line, @"^(周[一二三四五六日])扩展\s*[｜|]\s*(.+)$");
                if (ext.Success)
                {
                    string body = i + 1 < lines.Length ? lines[i + 1] : "";
                    string label = Regex.Replace(ext.Groups[2].Value, @"（[^）]*）", "").Trim().TrimEnd('：', ':').Trim();
                    if (body.Length > 0) result.Extensions[ext.Groups[1].Value] = body;
                    section = "";
                    continue;
                }
                if (line == "目标：" || line == "要求：") { section = goals.Count == 0 ? "goal" : section; continue; }
                if (line == "验收条件：" || line == "验收：") { section = "accept"; continue; }
                if (line.EndsWith("：") && line.Length <= 8) { section = ""; continue; }
                if (section == "goal")
                {
                    if (line.StartsWith("旧知识复习")) { result.ReviewBody = AfterColon(line); continue; }
                    if (line.StartsWith("新知识进阶")) { result.NewBody = AfterColon(line); continue; }
                    if (goals.Count < 4) goals.Add(line);
                }
                else if (section == "accept" && acceptance.Count < 4)
                    acceptance.Add(line);
            }
            if (String.IsNullOrWhiteSpace(result.NewBody) && goals.Count > 0)
                result.NewBody = String.Join(" ", goals.Where(g => !g.StartsWith("Day ")).ToArray());
            result.Acceptance = String.Join("\n", acceptance.ToArray());
        }

        private static string AfterColon(string line)
        {
            int i = line.IndexOfAny(new[] { '：', ':' });
            return i >= 0 && i < line.Length - 1 ? line.Substring(i + 1).Trim() : line;
        }
    }
}