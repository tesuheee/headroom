using System;
using System.Globalization;

namespace Headroom
{
    static class UsageParsers
    {
        public static UsageData ParseClaudeApi(string json)
        {
            var data = new UsageData { Name = "Claude", Source = "Claude API", UpdatedAt = DateTime.Now };
            if (string.IsNullOrWhiteSpace(json))
            {
                data.Status = "no_data";
                return data;
            }

            var root = Json.ParseObject(json);
            if (root == null)
            {
                data.Status = "no_data";
                return data;
            }

            var five = Json.Object(root, "five_hour");
            if (five != null)
            {
                data.FiveHourUsed = Json.Double(five, "utilization");
                string reset = Json.String(five, "resets_at");
                if (!string.IsNullOrEmpty(reset)) data.FiveHourReset = ConvertIsoToLegacyFormat(reset);
            }

            var week = Json.Object(root, "seven_day");
            if (week != null)
            {
                data.WeeklyUsed = Json.Double(week, "utilization");
                string reset = Json.String(week, "resets_at");
                if (!string.IsNullOrEmpty(reset)) data.WeeklyReset = ConvertIsoToLegacyFormat(reset);
            }

            if (!data.HasAnyValue()) data.Status = "no_data";
            return data;
        }

        public static UsageData ParseCodexApi(string json)
        {
            var data = new UsageData { Name = "Codex", Source = "Codex API", UpdatedAt = DateTime.Now };
            if (string.IsNullOrWhiteSpace(json))
            {
                data.Status = "no_data";
                return data;
            }

            var root = Json.ParseObject(json);
            if (root == null)
            {
                data.Status = "no_data";
                return data;
            }

            var primary = Json.Object(root, "primary_window");
            if (primary != null)
            {
                data.FiveHourUsed = Json.Double(primary, "used_percent");
                long? reset = Json.Long(primary, "reset_at");
                if (reset.HasValue && reset.Value > 0)
                    data.FiveHourReset = ConvertUnixSecondsToLegacyFormat(reset.Value);
            }

            var secondary = Json.Object(root, "secondary_window");
            if (secondary != null)
            {
                data.WeeklyUsed = Json.Double(secondary, "used_percent");
                long? reset = Json.Long(secondary, "reset_at");
                if (reset.HasValue && reset.Value > 0)
                    data.WeeklyReset = ConvertUnixSecondsToLegacyFormat(reset.Value);
            }

            if (!data.HasAnyValue()) data.Status = "no_data";
            return data;
        }

        static string ConvertIsoToLegacyFormat(string iso)
        {
            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dto))
                return dto.ToLocalTime().ToString("yyyy/M/d H:mm", CultureInfo.InvariantCulture);
            return iso;
        }

        static string ConvertUnixSecondsToLegacyFormat(long unixSec)
        {
            try
            {
                var dto = DateTimeOffset.FromUnixTimeSeconds(unixSec);
                return dto.ToLocalTime().ToString("yyyy/M/d H:mm", CultureInfo.InvariantCulture);
            }
            catch { return null; }
        }
    }
}
