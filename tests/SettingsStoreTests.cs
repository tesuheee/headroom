using System;
using System.IO;

namespace Headroom
{
    static class SettingsStoreTests
    {
        public static void Run(string root)
        {
            string dir = Path.Combine(Path.GetTempPath(), "HeadroomSettingsTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                TestMissingFileCreatesDefault(dir);
                TestLoadsJsonValues(dir);
                TestLegacyMigration(dir);
                TestInvalidJsonFallsBack(dir);
                Console.WriteLine("SettingsStoreTests: passed");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        static void TestMissingFileCreatesDefault(string dir)
        {
            string path = Path.Combine(dir, "missing", "settings.json");
            var settings = SettingsStore.Load(path, null);
            Equal("en", settings.Language, "default language");
            True(File.Exists(path), "default settings file created");
        }

        static void TestLoadsJsonValues(string dir)
        {
            string path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{" +
                "\"width\":900," +
                "\"height\":200," +
                "\"language\":\"ja\"," +
                "\"normalIntervalMinutes\":20," +
                "\"boostDurationMinutes\":40," +
                "\"boostIntervalMinutes\":2," +
                "\"nearResetIntervalSeconds\":30," +
                "\"alwaysOnTop\":true," +
                "\"showCodex\":false," +
                "\"showClaude\":true," +
                "\"layoutMode\":\"vertical\"," +
                "\"codexLoginMethod\":\"auto\"," +
                "\"claudeLoginMethod\":\"cli\"," +
                "\"fiveHourResetMode\":\"time\"," +
                "\"weeklyResetMode\":\"relative\"," +
                "\"warningRemainingPercent\":60," +
                "\"criticalRemainingPercent\":25" +
                "}");

            var settings = SettingsStore.Load(path, null);
            Equal(900, settings.Width, "width");
            Equal(200, settings.Height, "height");
            Equal("ja", settings.Language, "language");
            Equal(20, settings.NormalIntervalMinutes, "normal interval");
            Equal(40, settings.BoostDurationMinutes, "boost duration");
            Equal(2, settings.BoostIntervalMinutes, "boost interval");
            Equal(30, settings.NearResetIntervalSeconds, "near reset interval");
            True(settings.AlwaysOnTop, "always on top");
            True(!settings.ShowCodex, "show codex");
            True(settings.ShowClaude, "show claude");
            Equal("vertical", settings.LayoutMode, "layout");
            Equal("auto", settings.CodexLoginMethod, "codex login");
            Equal("cli", settings.ClaudeLoginMethod, "claude login");
            Equal("time", settings.FiveHourResetMode, "5h reset mode");
            Equal("relative", settings.WeeklyResetMode, "weekly reset mode");
            Equal(60, settings.WarningRemainingPercent, "warning threshold");
            Equal(25, settings.CriticalRemainingPercent, "critical threshold");

            settings.Width = 777;
            SettingsStore.Save(path, settings);
            var saved = SettingsStore.Load(path, null);
            Equal(777, saved.Width, "saved width");
        }

        static void TestLegacyMigration(string dir)
        {
            string target = Path.Combine(dir, "target", "settings.json");
            string legacy = Path.Combine(dir, "legacy.json");
            File.WriteAllText(legacy, "{\"language\":\"ja\",\"width\":888}");
            var settings = SettingsStore.Load(target, legacy);
            Equal("ja", settings.Language, "legacy language");
            Equal(888, settings.Width, "legacy width");
            True(File.Exists(target), "legacy copied");
        }

        static void TestInvalidJsonFallsBack(string dir)
        {
            string path = Path.Combine(dir, "invalid.json");
            File.WriteAllText(path, "{invalid");
            var settings = SettingsStore.Load(path, null);
            Equal("en", settings.Language, "invalid default language");
            Equal(760, settings.Width, "invalid default width");
        }

        static void True(bool value, string label)
        {
            if (!value) throw new InvalidOperationException(label + ": expected true");
        }

        static void Equal(string expected, string actual, string label)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual);
        }

        static void Equal(int expected, int actual, string label)
        {
            if (expected != actual)
                throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual);
        }
    }
}
