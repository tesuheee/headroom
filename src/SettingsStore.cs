using System;
using System.Collections.Generic;
using System.IO;

namespace Headroom
{
    static class SettingsStore
    {
        public static WidgetSettings Load(string targetPath, string legacyPath)
        {
            var settings = new WidgetSettings();
            try
            {
                if (!File.Exists(targetPath))
                {
                    if (!string.IsNullOrEmpty(legacyPath) && File.Exists(legacyPath))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                        File.Copy(legacyPath, targetPath, false);
                    }
                    else
                    {
                        Save(targetPath, settings);
                        return settings;
                    }
                }

                string json = File.ReadAllText(targetPath);
                var root = Json.ParseObject(json);
                if (root == null)
                {
                    DebugLog.Write("settings-load-error.txt", "Invalid JSON: " + targetPath);
                    return settings;
                }

                settings.Width = ReadInt(root, "width", settings.Width);
                settings.Height = ReadInt(root, "height", settings.Height);
                settings.Language = ReadString(root, "language", settings.Language);
                settings.NormalIntervalMinutes = ReadInt(root, "normalIntervalMinutes", settings.NormalIntervalMinutes);
                settings.BoostDurationMinutes = ReadInt(root, "boostDurationMinutes", settings.BoostDurationMinutes);
                settings.BoostIntervalMinutes = ReadInt(root, "boostIntervalMinutes", settings.BoostIntervalMinutes);
                settings.NearResetIntervalSeconds = ReadInt(root, "nearResetIntervalSeconds", settings.NearResetIntervalSeconds);
                settings.AlwaysOnTop = ReadBool(root, "alwaysOnTop", settings.AlwaysOnTop);
                settings.ShowCodex = ReadBool(root, "showCodex", settings.ShowCodex);
                settings.ShowClaude = ReadBool(root, "showClaude", settings.ShowClaude);
                settings.LayoutMode = NormalizeLayoutMode(ReadString(root, "layoutMode", settings.LayoutMode));
                settings.CodexShowUsed = ReadBool(root, "codexShowUsed", settings.CodexShowUsed);
                settings.ClaudeShowUsed = ReadBool(root, "claudeShowUsed", settings.ClaudeShowUsed);
                settings.CodexLoggedOut = ReadBool(root, "codexLoggedOut", settings.CodexLoggedOut);
                settings.ClaudeLoggedOut = ReadBool(root, "claudeLoggedOut", settings.ClaudeLoggedOut);
                settings.CodexLoginMethod = NormalizeLoginMethod(ReadString(root, "codexLoginMethod", settings.CodexLoginMethod));
                settings.ClaudeLoginMethod = NormalizeLoginMethod(ReadString(root, "claudeLoginMethod", settings.ClaudeLoginMethod));
                settings.FiveHourResetMode = NormalizeResetMode(ReadString(root, "fiveHourResetMode", settings.FiveHourResetMode));
                settings.WeeklyResetMode = NormalizeResetMode(ReadString(root, "weeklyResetMode", settings.WeeklyResetMode));
                settings.WarningRemainingPercent = ReadInt(root, "warningRemainingPercent", settings.WarningRemainingPercent);
                settings.CriticalRemainingPercent = ReadInt(root, "criticalRemainingPercent", settings.CriticalRemainingPercent);
            }
            catch (Exception ex)
            {
                DebugLog.Write("settings-load-error.txt", ex.ToString());
            }
            return settings;
        }

        public static void Save(string path, WidgetSettings settings)
        {
            try
            {
                var root = new Dictionary<string, object>
                {
                    { "width", settings.Width },
                    { "height", settings.Height },
                    { "language", settings.Language },
                    { "normalIntervalMinutes", settings.NormalIntervalMinutes },
                    { "boostDurationMinutes", settings.BoostDurationMinutes },
                    { "boostIntervalMinutes", settings.BoostIntervalMinutes },
                    { "nearResetIntervalSeconds", settings.NearResetIntervalSeconds },
                    { "alwaysOnTop", settings.AlwaysOnTop },
                    { "showCodex", settings.ShowCodex },
                    { "showClaude", settings.ShowClaude },
                    { "layoutMode", settings.LayoutMode },
                    { "codexShowUsed", settings.CodexShowUsed },
                    { "claudeShowUsed", settings.ClaudeShowUsed },
                    { "codexLoggedOut", settings.CodexLoggedOut },
                    { "claudeLoggedOut", settings.ClaudeLoggedOut },
                    { "codexLoginMethod", settings.CodexLoginMethod },
                    { "claudeLoginMethod", settings.ClaudeLoginMethod },
                    { "fiveHourResetMode", settings.FiveHourResetMode },
                    { "weeklyResetMode", settings.WeeklyResetMode },
                    { "warningRemainingPercent", settings.WarningRemainingPercent },
                    { "criticalRemainingPercent", settings.CriticalRemainingPercent },
                };
                FileWrites.WriteUtf8Atomic(path, Json.Serialize(root) + "\n");
            }
            catch (Exception ex)
            {
                DebugLog.Write("settings-save-error.txt", ex.ToString());
            }
        }

        static int ReadInt(Dictionary<string, object> root, string key, int fallback)
        {
            long? value = Json.Long(root, key);
            return value.HasValue ? (int)value.Value : fallback;
        }

        static bool ReadBool(Dictionary<string, object> root, string key, bool fallback)
        {
            bool? value = Json.Bool(root, key);
            return value.HasValue ? value.Value : fallback;
        }

        static string ReadString(Dictionary<string, object> root, string key, string fallback)
        {
            string value = Json.String(root, key);
            return value ?? fallback;
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
    }
}
