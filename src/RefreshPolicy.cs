using System;
using System.Net.Http;

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
            return ResetTimes.TryGetRemaining(raw, now, rollTimeOnlyToTomorrow, out remaining);
        }
    }
}
