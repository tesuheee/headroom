using System;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace Headroom
{
    sealed class RefreshDecision
    {
        public bool ShouldRefresh;
        public bool RateLimited;
        public bool RateLimitExpired;
        public bool BoostExpired;
        public TimeSpan Due;
    }

    static class RefreshPolicy
    {
        public const int DefaultRateLimitBackoffMinutes = 30;

        public static int SchedulerIntervalMs(WidgetSettings settings, ServiceState claude, ServiceState codex, DateTime now)
        {
            return 10000;
        }

        public static RefreshDecision Evaluate(ServiceState service, WidgetSettings settings, DateTime now)
        {
            var decision = new RefreshDecision();
            if (service.ManuallyLoggedOut || service.IsRefreshing) return decision;

            if (service.RateLimitedUntil.HasValue)
            {
                if (service.RateLimitedUntil.Value > now)
                {
                    decision.RateLimited = true;
                    return decision;
                }
                decision.RateLimitExpired = true;
            }

            if (service.BoostUntil.HasValue && service.BoostUntil.Value <= now)
                decision.BoostExpired = true;

            decision.Due = DueInterval(service, settings, now);
            decision.ShouldRefresh = decision.RateLimitExpired ||
                service.LastRefresh == DateTime.MinValue ||
                now - service.LastRefresh >= decision.Due;
            return decision;
        }

        public static TimeSpan DueInterval(ServiceState service, WidgetSettings settings, DateTime now)
        {
            if (IsNearOrRecentReset(service.Data, now))
                return TimeSpan.FromSeconds(10);
            int minutes = service.BoostUntil.HasValue && service.BoostUntil.Value > now
                ? settings.BoostIntervalMinutes
                : settings.NormalIntervalMinutes;
            return TimeSpan.FromMinutes(Math.Max(1, minutes));
        }

        public static bool IsNearOrRecentReset(UsageData data, DateTime now)
        {
            return IsResetDueOrPast(data.FiveHourReset, now) ||
                IsResetDueOrPast(data.WeeklyReset, now);
        }

        public static bool IsResetDueOrPast(string raw, DateTime now)
        {
            TimeSpan rem;
            return !string.IsNullOrWhiteSpace(raw) &&
                TryGetResetRemaining(raw, now, false, out rem) &&
                rem.TotalSeconds <= 0;
        }

        public static DateTime RateLimitUntil(HttpResponseMessage resp, DateTime now)
        {
            var retryAfter = resp.Headers.RetryAfter;
            if (retryAfter != null)
            {
                if (retryAfter.Delta.HasValue)
                {
                    double seconds = Math.Max(60, Math.Min(7200, retryAfter.Delta.Value.TotalSeconds));
                    return now.AddSeconds(seconds);
                }
                if (retryAfter.Date.HasValue)
                {
                    DateTime target = retryAfter.Date.Value.LocalDateTime;
                    if (target > now) return target;
                }
            }
            return now.AddMinutes(DefaultRateLimitBackoffMinutes);
        }

        static bool TryGetResetRemaining(string raw, DateTime now, bool rollTimeOnlyToTomorrow, out TimeSpan remaining)
        {
            string cleaned = Regex.Replace(raw ?? "", @"^\s*リセット\s*[：:]\s*", "").Trim();
            var relative = Regex.Match(cleaned, @"(?:(\d+)\s*時間)?\s*(?:(\d+)\s*分)?\s*後にリセット");
            if (relative.Success)
            {
                int hours = relative.Groups[1].Success ? int.Parse(relative.Groups[1].Value) : 0;
                int minutes = relative.Groups[2].Success ? int.Parse(relative.Groups[2].Value) : 0;
                remaining = new TimeSpan(hours, minutes, 0);
                return true;
            }
            DateTime target;
            if (TryParseResetTarget(raw, now, rollTimeOnlyToTomorrow, out target))
            {
                remaining = target - now;
                return true;
            }
            remaining = TimeSpan.Zero;
            return false;
        }

        static bool TryParseResetTarget(string text, DateTime now, bool rollTimeOnlyToTomorrow, out DateTime target)
        {
            target = DateTime.MinValue;
            var dateTime = Regex.Match(text, @"(?:(\d{4})/)?(\d{1,2})/(\d{1,2})\s+(\d{1,2}):(\d{2})");
            if (dateTime.Success)
            {
                int year = dateTime.Groups[1].Success ? int.Parse(dateTime.Groups[1].Value) : now.Year;
                target = new DateTime(year, int.Parse(dateTime.Groups[2].Value), int.Parse(dateTime.Groups[3].Value), int.Parse(dateTime.Groups[4].Value), int.Parse(dateTime.Groups[5].Value), 0);
                if (!dateTime.Groups[1].Success && target < now.AddMinutes(-1)) target = target.AddYears(1);
                return true;
            }

            var time = Regex.Match(text, @"(?:^|[^\d])(\d{1,2}):(\d{2})(?:$|[^\d])");
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
    }
}
