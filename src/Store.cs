using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace AiTime
{
    public sealed class AllowedApp
    {
        public string Name = "";
        public string Path = "";
        public string Arguments = "";
        public override string ToString() { return Name + "  ·  " + Path; }
    }
    public sealed class AppConfig
    {
        public int Minutes = 45;
        public int DailyGoalMinutes = 180;
        public int Cycles = 1;
        public int BreakMinutes = 5;
        public string Subject = "Python 实操";
        public string LastPlanKey = "";
        public int LearningDay = 1;
        public List<AllowedApp> Allowed = new List<AllowedApp>();
        public List<AllowedApp> ManualAllowed = new List<AllowedApp>();
        /// <summary>Focus length for an ad-hoc to-do item.</summary>
        public int TaskMinutes = 25;
        /// <summary>Closing the window keeps AiTime in the tray so the 10:00 / 18:00 / 12:00 reminders can fire.</summary>
        public bool BackgroundReminder = true;
        public bool TrayHintShown;
        /// <summary>Looping UI effects (glows, particles, confetti). Basic transitions stay on.</summary>
        public bool Effects = true;
        /// <summary>"yyyyMMdd|review" style marks for today's checklist.</summary>
        public List<string> DoneMarks = new List<string>();
        /// <summary>"yyyyMMdd-HHmm" reminders that were already shown.</summary>
        public List<string> ReminderMarks = new List<string>();
    }
    public sealed class TaskItem
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string Title = "";
        public bool Done;
    }
    public sealed class FocusRecord
    {
        public string Id = "";
        public string Subject = "";
        public string Status = "";
        public DateTime StartedAt;
        public DateTime EndedAt;
        public int PlannedSeconds;
        public int ActualSeconds;
        public bool IsTest;
    }
    public static class JsonFile
    {
        public static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 16777216, RecursionLimit = 64 }; }
        public static T Read<T>(string path, T fallback)
        {
            if (!File.Exists(path)) return fallback;
            return Serializer().Deserialize<T>(File.ReadAllText(path, Encoding.UTF8));
        }
        public static void Write(string path, object value)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)));
            string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, Serializer().Serialize(value), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", true);
            else File.Move(tmp, path);
        }
    }
    public sealed class ActiveSession
    {
        public string Id;
        public int ParentPid;
        public long ParentStartTicks;
        public string Subject;
        public string JournalPath;
        public string StopPath;
        public bool IsTest;
        public bool IsBreak;
        public int Cycle;
        public int Cycles;
        public int PlannedSeconds;
        public int ActualSeconds;
        public DateTime StartedAt;
        public DateTime LastHeartbeat;
        public ulong HeartbeatTick;
    }
    public sealed class AppStore
    {
        public readonly string Root;
        public AppConfig Config;
        public List<FocusRecord> Records;
        public List<TaskItem> Tasks;
        public string ActivePath { get { return Path.Combine(Root, "active-session.json"); } }
        public AppStore(string root)
        {
            Root = Path.GetFullPath(root);
            Directory.CreateDirectory(Root);
            Config = LoadPreserving("settings.json", new AppConfig());
            Records = LoadPreserving("records.json", new List<FocusRecord>());
            Tasks = LoadPreserving("tasks.json", new List<TaskItem>());
            if (Config == null) Config = new AppConfig();
            if (Config.Allowed == null) Config.Allowed = new List<AllowedApp>();
            if (Config.ManualAllowed == null) Config.ManualAllowed = new List<AllowedApp>();
            if (Records == null) Records = new List<FocusRecord>();
            if (Tasks == null) Tasks = new List<TaskItem>();
            Config.Minutes = Math.Max(1, Math.Min(720, Config.Minutes));
            Config.DailyGoalMinutes = Math.Max(1, Math.Min(1440, Config.DailyGoalMinutes));
            Config.Cycles = Math.Max(1, Math.Min(12, Config.Cycles));
            Config.BreakMinutes = Math.Max(0, Math.Min(120, Config.BreakMinutes));
            Config.TaskMinutes = Math.Max(5, Math.Min(180, Config.TaskMinutes));
            Config.LearningDay = Math.Max(1, Math.Min(999, Config.LearningDay));
            if (Config.DoneMarks == null) Config.DoneMarks = new List<string>();
            if (Config.ReminderMarks == null) Config.ReminderMarks = new List<string>();
            string cutoff = DateTime.Today.AddDays(-14).ToString("yyyyMMdd");
            Config.DoneMarks = Config.DoneMarks.Where(m => m != null && m.Length >= 8 && String.CompareOrdinal(m.Substring(0, 8), cutoff) >= 0).ToList();
            Config.ReminderMarks = Config.ReminderMarks.Where(m => m != null && m.Length >= 8 && String.CompareOrdinal(m.Substring(0, 8), cutoff) >= 0).ToList();
        }
        private T LoadPreserving<T>(string name, T fallback)
        {
            string path = Path.Combine(Root, name);
            try { return JsonFile.Read<T>(path, fallback); }
            catch (Exception ex)
            {
                // Keep both the original bytes and an explicit diagnostic; never silently overwrite broken data.
                string preserved = path + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
                File.Copy(path, preserved, false);
                File.AppendAllText(Path.Combine(Root, "diagnostics.log"), DateTime.Now.ToString("s") + " " + name + " " + ex.Message + Environment.NewLine);
                try { return JsonFile.Read<T>(path + ".bak", fallback); } catch { return fallback; }
            }
        }
        public void SaveConfig() { JsonFile.Write(Path.Combine(Root, "settings.json"), Config); }
        public void SaveTasks() { JsonFile.Write(Path.Combine(Root, "tasks.json"), Tasks); }
        public bool IsDone(DateTime day, string item) { return Config.DoneMarks.Contains(day.ToString("yyyyMMdd") + "|" + item); }
        public void SetDone(DateTime day, string item, bool done)
        {
            string key = day.ToString("yyyyMMdd") + "|" + item;
            Config.DoneMarks.Remove(key);
            if (done) Config.DoneMarks.Add(key);
            SaveConfig();
        }
        public List<FocusRecord> RecordsOn(DateTime day)
        {
            return Records.Where(r => !r.IsTest && r.StartedAt.ToLocalTime().Date == day.Date).OrderBy(r => r.StartedAt).ToList();
        }
        public void AddRecord(FocusRecord record)
        {
            if (Records.Any(r => r.Id == record.Id)) return;
            var updated = new List<FocusRecord>(Records); updated.Add(record);
            JsonFile.Write(Path.Combine(Root, "records.json"), updated);
            Records = updated;
        }
        public int GetTodaySeconds()
        {
            return GetDaySeconds(DateTime.Today);
        }
        public int GetDaySeconds(DateTime day)
        {
            DateTime start = day.Date, end = start.AddDays(1);
            double total = 0;
            foreach (FocusRecord r in Records.Where(r => !r.IsTest && r.ActualSeconds > 0))
            {
                DateTime a = r.StartedAt.ToLocalTime(), b = r.EndedAt.ToLocalTime();
                if (b <= a) { if (a.Date == start) total += r.ActualSeconds; continue; }
                DateTime left = a > start ? a : start, right = b < end ? b : end;
                if (right > left) total += r.ActualSeconds * (right - left).TotalSeconds / (b - a).TotalSeconds;
            }
            return (int)Math.Min(int.MaxValue, Math.Round(total));
        }
        public void ExportRecords(string path)
        {
            JsonFile.Write(path, new { App = "AiTime Windows", Version = "1.1.0", ExportedAt = DateTime.Now, Settings = Config, Records = Records, Tasks = Tasks });
        }
        public void OpenStudyDeck()
        {
            string p = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "index.html"));
            if (!File.Exists(p)) throw new FileNotFoundException("未找到周末学习舱，请从完整安装目录启动。", p);
            Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
        }
        public void RecoverAbandoned()
        {
            ActiveSession s = JsonFile.Read<ActiveSession>(ActivePath, null);
            if (s == null || NativeWindows.IsSameProcess(s.ParentPid, s.ParentStartTicks)) return;
            GuardEngine.RestoreJournal(s.JournalPath);
            if (!s.IsBreak)
                AddRecord(new FocusRecord { Id = s.Id + "-" + s.Cycle, Subject = s.Subject, Status = "异常中断", StartedAt = s.StartedAt,
                    EndedAt = s.LastHeartbeat, PlannedSeconds = s.PlannedSeconds, ActualSeconds = Math.Min(s.PlannedSeconds, Math.Max(0, s.ActualSeconds)), IsTest = s.IsTest });
            JsonFile.Write(ActivePath, null);
        }
    }
}
