using System;
using System.Text.RegularExpressions;

namespace Headroom
{
    static class ResetTimes
    {
        public static bool TryGetRemaining(string raw, DateTime now, bool rollTimeOnlyToTomorrow, out TimeSpan remaining)
        {
            remaining = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            string cleaned = Clean(raw);

            var relative = Regex.Match(cleaned, @"(?:(\d+)\s*時間)?\s*(?:(\d+)\s*分)?\s*後にリセット");
            if (relative.Success)
            {
                int hours = relative.Groups[1].Success ? int.Parse(relative.Groups[1].Value) : 0;
                int minutes = relative.Groups[2].Success ? int.Parse(relative.Groups[2].Value) : 0;
                remaining = new TimeSpan(hours, minutes, 0);
                return true;
            }

            DateTime target;
            if (TryParseTarget(cleaned, now, rollTimeOnlyToTomorrow, out target))
            {
                remaining = target - now;
                return true;
            }
            return false;
        }

        public static bool TryParseTarget(string text, DateTime now, bool rollTimeOnlyToTomorrow, out DateTime target)
        {
            target = DateTime.MinValue;
            string cleaned = Clean(text);
            var dateTime = Regex.Match(cleaned, @"(?:(\d{4})/)?(\d{1,2})/(\d{1,2})\s+(\d{1,2}):(\d{2})");
            if (dateTime.Success)
            {
                int year = dateTime.Groups[1].Success ? int.Parse(dateTime.Groups[1].Value) : now.Year;
                target = new DateTime(year, int.Parse(dateTime.Groups[2].Value), int.Parse(dateTime.Groups[3].Value), int.Parse(dateTime.Groups[4].Value), int.Parse(dateTime.Groups[5].Value), 0);
                if (!dateTime.Groups[1].Success && target < now.AddMinutes(-1)) target = target.AddYears(1);
                return true;
            }

            var weekdayTime = Regex.Match(cleaned, @"(\d{1,2}):(\d{2})\s*(?:[（(]\s*([月火水木金土日])(?:曜(?:日)?)?\s*[）)]|([月火水木金土日])曜(?:日)?)");
            if (weekdayTime.Success)
            {
                int hour = int.Parse(weekdayTime.Groups[1].Value);
                int minute = int.Parse(weekdayTime.Groups[2].Value);
                string dayText = weekdayTime.Groups[3].Success ? weekdayTime.Groups[3].Value : weekdayTime.Groups[4].Value;
                DayOfWeek day;
                if (TryParseJapaneseWeekday(dayText, out day))
                {
                    int days = ((int)day - (int)now.DayOfWeek + 7) % 7;
                    target = now.Date.AddDays(days).AddHours(hour).AddMinutes(minute);
                    if (target < now.AddMinutes(-1)) target = target.AddDays(7);
                    return true;
                }
            }

            var time = Regex.Match(cleaned, @"(?:^|[^\d])(\d{1,2}):(\d{2})(?:$|[^\d])");
            if (time.Success)
            {
                target = new DateTime(now.Year, now.Month, now.Day, int.Parse(time.Groups[1].Value), int.Parse(time.Groups[2].Value), 0);
                if (target < now.AddMinutes(-1))
                {
                    if (!rollTimeOnlyToTomorrow) return false;
                    target = target.AddDays(1);
                }
                return true;
            }
            return false;
        }

        public static string Clean(string raw)
        {
            return Regex.Replace(raw ?? "", @"^\s*リセット\s*[：:]\s*", "").Trim();
        }

        static bool TryParseJapaneseWeekday(string text, out DayOfWeek day)
        {
            switch (text)
            {
                case "日": day = DayOfWeek.Sunday; return true;
                case "月": day = DayOfWeek.Monday; return true;
                case "火": day = DayOfWeek.Tuesday; return true;
                case "水": day = DayOfWeek.Wednesday; return true;
                case "木": day = DayOfWeek.Thursday; return true;
                case "金": day = DayOfWeek.Friday; return true;
                case "土": day = DayOfWeek.Saturday; return true;
                default: day = DayOfWeek.Sunday; return false;
            }
        }
    }
}
