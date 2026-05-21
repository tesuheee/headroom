using System;
using System.IO;

namespace Headroom
{
    static class ParserTests
    {
        public static void Run(string root)
        {
            TestOkFixture(root);
            TestExhaustedFixtures(root);
            TestNoDataFixture(root);
            TestNestedObjectsDoNotBreakCodexParser();
            Console.WriteLine("ParserTests: passed");
        }

        static void TestOkFixture(string root)
        {
            var claude = UsageParsers.ParseClaudeApi(ReadFixture(root, "01-ok", "claude.json"));
            Equal("Claude", claude.Name, "Claude name");
            Equal(28.5, claude.FiveHourUsed.Value, "Claude 5h used");
            Equal(42.0, claude.WeeklyUsed.Value, "Claude weekly used");
            Equal(72, claude.FiveHourRemainingPercent().Value, "Claude 5h remaining");

            var codex = UsageParsers.ParseCodexApi(ReadFixture(root, "01-ok", "codex.json"));
            Equal("Codex", codex.Name, "Codex name");
            Equal(31.0, codex.FiveHourUsed.Value, "Codex 5h used");
            Equal(44.5, codex.WeeklyUsed.Value, "Codex weekly used");
            Equal(69, codex.FiveHourRemainingPercent().Value, "Codex 5h remaining");
        }

        static void TestExhaustedFixtures(string root)
        {
            var five = UsageParsers.ParseCodexApi(ReadFixture(root, "02-five-hour-exhausted", "codex.json"));
            Equal(100.0, five.FiveHourUsed.Value, "Codex exhausted 5h used");
            Equal(0, five.FiveHourRemainingPercent().Value, "Codex exhausted 5h remaining");

            var week = UsageParsers.ParseClaudeApi(ReadFixture(root, "03-weekly-exhausted", "claude.json"));
            Equal(100.0, week.WeeklyUsed.Value, "Claude exhausted weekly used");
            Equal(0, week.WeeklyRemainingPercent().Value, "Claude exhausted weekly remaining");
        }

        static void TestNoDataFixture(string root)
        {
            var claude = UsageParsers.ParseClaudeApi(ReadFixture(root, "05-no-data", "claude.json"));
            Equal("no_data", claude.Status, "Claude no-data status");

            var codex = UsageParsers.ParseCodexApi(ReadFixture(root, "05-no-data", "codex.json"));
            Equal("no_data", codex.Status, "Codex no-data status");
        }

        static void TestNestedObjectsDoNotBreakCodexParser()
        {
            string json = "{" +
                "\"primary_window\":{\"metadata\":{\"ignored\":true},\"used_percent\":12.5,\"reset_at\":1779432000}," +
                "\"secondary_window\":{\"limits\":{\"ignored\":true},\"used_percent\":25,\"reset_at\":1779667200}" +
                "}";
            var codex = UsageParsers.ParseCodexApi(json);
            Equal(12.5, codex.FiveHourUsed.Value, "Codex nested 5h used");
            Equal(25.0, codex.WeeklyUsed.Value, "Codex nested weekly used");
        }

        static string ReadFixture(string root, string scenario, string fileName)
        {
            return File.ReadAllText(Path.Combine(root, "docs", "fixtures", scenario, fileName));
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

        static void Equal(double expected, double actual, string label)
        {
            if (Math.Abs(expected - actual) > 0.0001)
                throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual);
        }
    }
}
