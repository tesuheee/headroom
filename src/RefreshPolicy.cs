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
            bool anyVeryNear =
                (settings.ShowClaude && IsVeryNearReset(claude.Data, now)) ||
                (settings.ShowCodex && IsVeryNearReset(codex.Data, now));
            bool anyNear =
                (settings.ShowClaude && IsNearOrRecentReset(claude.Data, now)) ||
                (settings.ShowCodex && IsNearOrRecentReset(codex.Data, now));
            return anyVeryNear ? 5000 : (anyNear ? 5000 : 10000);
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
            decision.ShouldRefresh = service.LastRefresh == DateTime.MinValue || now - service.LastRefresh >= decision.Due;
            return decision;
        }

        public static TimeSpan DueInterval(ServiceState service, WidgetSettings settings, DateTime now)
        {
            if (IsVeryNearReset(service.Data, now))
                return TimeSpan.FromSeconds(5);
            if (IsNearOrRecentReset(service.Data, now))
                return TimeSpan.FromSeconds(Math.Max(15, settings.NearResetIntervalSeconds));
            int minutes = service.BoostUntil.HasValue && service.BoostUntil.Value > now
                ? settings.BoostIntervalMinutes
                : settings.NormalIntervalMinutes;
            return TimeSpan.FromMinutes(Math.Max(1, minutes));
        }

        public static bool IsNearOrRecentReset(UsageData data, DateTime now)
        {
            bool fiveEmpty = data.FiveHourRemainingPercent().HasValue && data.FiveHourRemainingPercent().Value <= 0;
            bool weekEmpty = data.WeeklyRemainingPercent().HasValue && data.WeeklyRemainingPercent().Value <= 0;
            if (!fiveEmpty && !weekEmpty) return false;

            TimeSpan rem;
            if (fiveEmpty)
            {
                if (string.IsNullOrEmpty(data.FiveHourReset)) return true;
                if (TryGetResetRemaining(data.FiveHourReset, now, false, out rem) && Math.Abs(rem.TotalMinutes) < 10)
                    return true;
            }
            if (weekEmpty)
            {
                if (string.IsNullOrEmpty(data.WeeklyReset)) return true;
                if (TryGetResetRemaining(data.WeeklyReset, now, false, out rem) && Math.Abs(rem.TotalMinutes) < 10)
                    return true;
            }
            return false;
        }

        public static bool IsVeryNearReset(UsageData data, DateTime now)
        {
            bool fiveEmpty = data.FiveHourRemainingPercent().HasValue && data.FiveHourRemainingPercent().Value <= 0;
            bool weekEmpty = data.WeeklyRemainingPercent().HasValue && data.WeeklyRemainingPercent().Value <= 0;
            if (!fiveEmpty && !weekEmpty) return false;
            TimeSpan rem;
            if (fiveEmpty && !string.IsNullOrEmpty(data.FiveHourReset)
                && TryGetResetRemaining(data.FiveHourReset, now, false, out rem) && Math.Abs(rem.TotalMinutes) < 1)
                return true;
            if (weekEmpty && !string.IsNullOrEmpty(data.WeeklyReset)
                && TryGetResetRemaining(data.WeeklyReset, now, false, out rem) && Math.Abs(rem.TotalMinutes) < 1)
                return true;
            return false;
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
