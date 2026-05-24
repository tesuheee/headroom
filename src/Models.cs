using System;
using System.Drawing;
using System.IO;
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
        public bool AlwaysOnTop = false;
        public bool ShowCodex = true;
        public bool ShowClaude = true;
        public string LayoutMode = "horizontal";
        public string ServiceOrder = "claude-codex";
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

        internal static string LegacySettingsPath
        {
            get { return Path.Combine(Application.StartupPath, "settings.json"); }
        }

        static string DefaultLanguage()
        {
            return "en";
        }

        public static WidgetSettings Load()
        {
            return SettingsStore.Load(SettingsPath, LegacySettingsPath);
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
            AlwaysOnTop = other.AlwaysOnTop;
            ShowCodex = other.ShowCodex;
            ShowClaude = other.ShowClaude;
            LayoutMode = other.LayoutMode;
            ServiceOrder = other.ServiceOrder;
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
            SettingsStore.Save(SettingsPath, this);
        }
    }
}
