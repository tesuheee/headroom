using System;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Headroom
{
    sealed class ServiceState
    {
        public readonly string Name;
        public readonly string Url;
        public readonly Color Accent;
        public UsageData Data;
        public string Status;
        public bool IsRefreshing;
        public bool ManuallyLoggedOut;
        public DateTime LastRefresh = DateTime.MinValue;
        public DateTime? RateLimitedUntil;
        public DateTime? BoostUntil;
        public double? DisplayedFivePct;
        public double? DisplayedWeekPct;

        public ServiceState(string name, string url, Color accent)
        {
            Name = name;
            Url = url;
            Accent = accent;
            Data = new UsageData { Name = name, Source = "starting", Status = "starting" };
            Status = "starting";
        }

        public bool BoostActive
        {
            get { return BoostUntil.HasValue && BoostUntil.Value > DateTime.Now; }
        }
    }

    sealed class UsageData
    {
        public string Name;
        public string Source;
        public string Status;
        public DateTime UpdatedAt;
        public double? FiveHourUsed;
        public double? WeeklyUsed;
        public int? FiveHourRemaining = null;
        public int? WeeklyRemaining = null;
        public string FiveHourReset;
        public string WeeklyReset;
        public bool FiveHourNotStarted = false;
        public bool WeeklyNotStarted = false;
        public bool HitLimit = false;

        public bool HasAnyValue()
        {
            return FiveHourUsed.HasValue || WeeklyUsed.HasValue || FiveHourRemaining.HasValue || WeeklyRemaining.HasValue;
        }

        public int? FiveHourRemainingPercent()
        {
            if (FiveHourRemaining.HasValue) return FiveHourRemaining.Value;
            if (FiveHourUsed.HasValue) return Clamp(100 - FiveHourUsed.Value);
            return null;
        }

        public int? FiveHourUsedPercent()
        {
            if (FiveHourUsed.HasValue) return Clamp(FiveHourUsed.Value);
            if (FiveHourRemaining.HasValue) return Clamp(100 - FiveHourRemaining.Value);
            return null;
        }

        public int? FiveHourDisplayPercent(bool showUsed)
        {
            return showUsed ? FiveHourUsedPercent() : FiveHourRemainingPercent();
        }

        public int? WeeklyRemainingPercent()
        {
            if (WeeklyRemaining.HasValue) return WeeklyRemaining.Value;
            if (WeeklyUsed.HasValue) return Clamp(100 - WeeklyUsed.Value);
            return null;
        }

        public int? WeeklyUsedPercent()
        {
            if (WeeklyUsed.HasValue) return Clamp(WeeklyUsed.Value);
            if (WeeklyRemaining.HasValue) return Clamp(100 - WeeklyRemaining.Value);
            return null;
        }

        public int? WeeklyDisplayPercent(bool showUsed)
        {
            return showUsed ? WeeklyUsedPercent() : WeeklyRemainingPercent();
        }

        static int Clamp(double value)
        {
            return Math.Max(0, Math.Min(100, (int)Math.Round(value)));
        }
    }

    sealed class WidgetSettings
    {
        public int Width = 760;
        public int Height = 170;
        public string Language = DefaultLanguage();
        public int NormalIntervalMinutes = 15;
        public int BoostDurationMinutes = 30;
        public int BoostIntervalMinutes = 1;
        public int NearResetIntervalSeconds = 15;
        public bool AlwaysOnTop = false;
        public bool ShowCodex = true;
        public bool ShowClaude = true;
        public string LayoutMode = "horizontal";
        public bool CodexShowUsed = false;
        public bool ClaudeShowUsed = false;
        public bool CodexLoggedOut = false;
        public bool ClaudeLoggedOut = false;
        public string CodexLoginMethod = "browser";
        public string ClaudeLoginMethod = "browser";
        public string FiveHourResetMode = "relative";
        public string WeeklyResetMode = "time";
        public int WarningRemainingPercent = 50;
        public int CriticalRemainingPercent = 30;

        internal static string SettingsPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Headroom",
                    "settings.json");
            }
        }

        internal static string SettingsDirectory
        {
            get { return Path.GetDirectoryName(SettingsPath); }
        }

        static string LegacySettingsPath
        {
            get { return Path.Combine(Application.StartupPath, "settings.json"); }
        }

        static string DefaultLanguage()
        {
            return "en";
        }

        public static WidgetSettings Load()
        {
            var s = new WidgetSettings();
            try
            {
                string target = SettingsPath;
                if (!File.Exists(target))
                {
                    string legacy = LegacySettingsPath;
                    if (File.Exists(legacy))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        File.Copy(legacy, target, false);
                    }
                    else
                    {
                        s.Save();
                        return s;
                    }
                }
                string json = File.ReadAllText(target);
                s.Width = ReadInt(json, "width", s.Width);
                s.Height = ReadInt(json, "height", s.Height);
                s.Language = ReadString(json, "language", s.Language);
                s.NormalIntervalMinutes = ReadInt(json, "normalIntervalMinutes", s.NormalIntervalMinutes);
                s.BoostDurationMinutes = ReadInt(json, "boostDurationMinutes", s.BoostDurationMinutes);
                s.BoostIntervalMinutes = ReadInt(json, "boostIntervalMinutes", s.BoostIntervalMinutes);
                s.NearResetIntervalSeconds = ReadInt(json, "nearResetIntervalSeconds", s.NearResetIntervalSeconds);
                s.AlwaysOnTop = ReadBool(json, "alwaysOnTop", s.AlwaysOnTop);
                s.ShowCodex = ReadBool(json, "showCodex", s.ShowCodex);
                s.ShowClaude = ReadBool(json, "showClaude", s.ShowClaude);
                s.LayoutMode = NormalizeLayoutMode(ReadString(json, "layoutMode", s.LayoutMode));
                s.CodexShowUsed = ReadBool(json, "codexShowUsed", s.CodexShowUsed);
                s.ClaudeShowUsed = ReadBool(json, "claudeShowUsed", s.ClaudeShowUsed);
                s.CodexLoggedOut = ReadBool(json, "codexLoggedOut", s.CodexLoggedOut);
                s.ClaudeLoggedOut = ReadBool(json, "claudeLoggedOut", s.ClaudeLoggedOut);
                s.CodexLoginMethod = NormalizeLoginMethod(ReadString(json, "codexLoginMethod", s.CodexLoginMethod));
                s.ClaudeLoginMethod = NormalizeLoginMethod(ReadString(json, "claudeLoginMethod", s.ClaudeLoginMethod));
                s.FiveHourResetMode = NormalizeResetMode(ReadString(json, "fiveHourResetMode", s.FiveHourResetMode));
                s.WeeklyResetMode = NormalizeResetMode(ReadString(json, "weeklyResetMode", s.WeeklyResetMode));
                s.WarningRemainingPercent = ReadInt(json, "warningRemainingPercent", s.WarningRemainingPercent);
                s.CriticalRemainingPercent = ReadInt(json, "criticalRemainingPercent", s.CriticalRemainingPercent);
            }
            catch (Exception ex)
            {
                DebugLog.Write("settings-load-error.txt", ex.ToString());
            }
            return s;
        }

        public WidgetSettings Clone()
        {
            var copy = new WidgetSettings();
            copy.CopyFrom(this);
            return copy;
        }

        public void CopyFrom(WidgetSettings other)
        {
            Width = other.Width;
            Height = other.Height;
            Language = other.Language;
            NormalIntervalMinutes = other.NormalIntervalMinutes;
            BoostDurationMinutes = other.BoostDurationMinutes;
            BoostIntervalMinutes = other.BoostIntervalMinutes;
            NearResetIntervalSeconds = other.NearResetIntervalSeconds;
            AlwaysOnTop = other.AlwaysOnTop;
            ShowCodex = other.ShowCodex;
            ShowClaude = other.ShowClaude;
            LayoutMode = other.LayoutMode;
            CodexShowUsed = other.CodexShowUsed;
            ClaudeShowUsed = other.ClaudeShowUsed;
            CodexLoggedOut = other.CodexLoggedOut;
            ClaudeLoggedOut = other.ClaudeLoggedOut;
            CodexLoginMethod = other.CodexLoginMethod;
            ClaudeLoginMethod = other.ClaudeLoginMethod;
            FiveHourResetMode = other.FiveHourResetMode;
            WeeklyResetMode = other.WeeklyResetMode;
            WarningRemainingPercent = other.WarningRemainingPercent;
            CriticalRemainingPercent = other.CriticalRemainingPercent;
        }

        public void ResetToDefaults()
        {
            CopyFrom(new WidgetSettings());
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllText(SettingsPath,
                    "{\r\n" +
                    "  \"width\": " + Width + ",\r\n" +
                    "  \"height\": " + Height + ",\r\n" +
                    "  \"language\": \"" + Escape(Language) + "\",\r\n" +
                    "  \"normalIntervalMinutes\": " + NormalIntervalMinutes + ",\r\n" +
                    "  \"boostDurationMinutes\": " + BoostDurationMinutes + ",\r\n" +
                    "  \"boostIntervalMinutes\": " + BoostIntervalMinutes + ",\r\n" +
                    "  \"nearResetIntervalSeconds\": " + NearResetIntervalSeconds + ",\r\n" +
                    "  \"alwaysOnTop\": " + (AlwaysOnTop ? "true" : "false") + ",\r\n" +
                    "  \"showCodex\": " + (ShowCodex ? "true" : "false") + ",\r\n" +
                    "  \"showClaude\": " + (ShowClaude ? "true" : "false") + ",\r\n" +
                    "  \"layoutMode\": \"" + Escape(LayoutMode) + "\",\r\n" +
                    "  \"codexShowUsed\": " + (CodexShowUsed ? "true" : "false") + ",\r\n" +
                    "  \"claudeShowUsed\": " + (ClaudeShowUsed ? "true" : "false") + ",\r\n" +
                    "  \"codexLoggedOut\": " + (CodexLoggedOut ? "true" : "false") + ",\r\n" +
                    "  \"claudeLoggedOut\": " + (ClaudeLoggedOut ? "true" : "false") + ",\r\n" +
                    "  \"codexLoginMethod\": \"" + Escape(CodexLoginMethod) + "\",\r\n" +
                    "  \"claudeLoginMethod\": \"" + Escape(ClaudeLoginMethod) + "\",\r\n" +
                    "  \"fiveHourResetMode\": \"" + Escape(FiveHourResetMode) + "\",\r\n" +
                    "  \"weeklyResetMode\": \"" + Escape(WeeklyResetMode) + "\",\r\n" +
                    "  \"warningRemainingPercent\": " + WarningRemainingPercent + ",\r\n" +
                    "  \"criticalRemainingPercent\": " + CriticalRemainingPercent + "\r\n" +
                    "}\r\n");
            }
            catch (Exception ex)
            {
                DebugLog.Write("settings-save-error.txt", ex.ToString());
            }
        }

        static int ReadInt(string json, string key, int fallback)
        {
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(\\d+)");
            if (!m.Success) return fallback;
            int value;
            return int.TryParse(m.Groups[1].Value, out value) ? value : fallback;
        }

        static bool ReadBool(string json, string key, bool fallback)
        {
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
            if (!m.Success) return fallback;
            return string.Equals(m.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
        }

        static string ReadString(string json, string key, string fallback)
        {
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"");
            if (!m.Success) return fallback;
            return m.Groups[1].Value;
        }

        static string NormalizeResetMode(string value)
        {
            return string.Equals(value, "time", StringComparison.OrdinalIgnoreCase) ? "time" : "relative";
        }

        static string NormalizeLayoutMode(string value)
        {
            return string.Equals(value, "vertical", StringComparison.OrdinalIgnoreCase) ? "vertical" : "horizontal";
        }

        static string NormalizeLoginMethod(string value)
        {
            if (string.Equals(value, "cli", StringComparison.OrdinalIgnoreCase)) return "cli";
            if (string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase)) return "auto";
            return "browser";
        }

        static string Escape(string value)
        {
            return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
