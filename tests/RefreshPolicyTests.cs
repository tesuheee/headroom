using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Headroom
{
    static class RefreshPolicyTests
    {
        public static void Run(string root)
        {
            TestNormalAndBoostIntervals();
            TestNearResetIntervals();
            TestRateLimitDecisions();
            TestRateLimitHeaderParsing();
            Console.WriteLine("RefreshPolicyTests: passed");
        }

        static void TestNormalAndBoostIntervals()
        {
            var now = new DateTime(2026, 5, 22, 10, 0, 0);
            var settings = new WidgetSettings();
            var svc = Service("Codex", 25, 40);

            svc.LastRefresh = now.AddMinutes(-14);
            True(!RefreshPolicy.Evaluate(svc, settings, now).ShouldRefresh, "normal not due");

            svc.LastRefresh = now.AddMinutes(-15);
            True(RefreshPolicy.Evaluate(svc, settings, now).ShouldRefresh, "normal due");

            svc.BoostUntil = now.AddMinutes(10);
            svc.LastRefresh = now.AddSeconds(-59);
            True(!RefreshPolicy.Evaluate(svc, settings, now).ShouldRefresh, "boost not due");

            svc.LastRefresh = now.AddMinutes(-1);
            True(RefreshPolicy.Evaluate(svc, settings, now).ShouldRefresh, "boost due");

            svc.BoostUntil = now.AddSeconds(-1);
            var expired = RefreshPolicy.Evaluate(svc, settings, now);
            True(expired.BoostExpired, "boost expired");
        }

        static void TestNearResetIntervals()
        {
            var now = new DateTime(2026, 5, 22, 10, 0, 0);
            var settings = new WidgetSettings();
            var svc = Service("Claude", 100, 40);

            svc.Data.FiveHourReset = "2026/5/22 10:08";
            Equal(TimeSpan.FromMinutes(15), RefreshPolicy.DueInterval(svc, settings, now), "pre reset normal due");
            True(!RefreshPolicy.IsNearOrRecentReset(svc.Data, now), "pre reset does not poll");

            svc.Data.FiveHourReset = "2026/5/22 10:00";
            Equal(TimeSpan.FromSeconds(10), RefreshPolicy.DueInterval(svc, settings, now), "due reset poll");
            True(RefreshPolicy.IsNearOrRecentReset(svc.Data, now), "due reset polls");

            svc = Service("Claude", 25, 40);
            svc.Data.WeeklyReset = "2026/5/22 10:08";
            Equal(TimeSpan.FromMinutes(15), RefreshPolicy.DueInterval(svc, settings, now), "weekly pre reset normal due without exhaustion");
            True(!RefreshPolicy.IsNearOrRecentReset(svc.Data, now), "weekly pre reset does not poll without exhaustion");

            svc.Data.WeeklyReset = "2026/5/22 10:00";
            Equal(TimeSpan.FromSeconds(10), RefreshPolicy.DueInterval(svc, settings, now), "weekly due reset poll without exhaustion");
            True(RefreshPolicy.IsNearOrRecentReset(svc.Data, now), "weekly due reset polls without exhaustion");

            svc.Data.WeeklyReset = "10:00（金）";
            Equal(TimeSpan.FromSeconds(10), RefreshPolicy.DueInterval(svc, settings, now), "weekday reset poll without exhaustion");
            True(RefreshPolicy.IsNearOrRecentReset(svc.Data, now), "weekday reset parsed without exhaustion");
        }

        static void TestRateLimitDecisions()
        {
            var now = new DateTime(2026, 5, 22, 10, 0, 0);
            var settings = new WidgetSettings();
            var svc = Service("Codex", 25, 40);

            svc.RateLimitedUntil = now.AddMinutes(5);
            var limited = RefreshPolicy.Evaluate(svc, settings, now);
            True(limited.RateLimited, "rate limited");
            True(!limited.ShouldRefresh, "rate limited no refresh");

            svc.RateLimitedUntil = now.AddSeconds(-1);
            svc.LastRefresh = now.AddMinutes(-1);
            var expired = RefreshPolicy.Evaluate(svc, settings, now);
            True(expired.RateLimitExpired, "rate limit expired");
            True(expired.ShouldRefresh, "rate limit expired refresh");
        }

        static void TestRateLimitHeaderParsing()
        {
            var now = new DateTime(2026, 5, 22, 10, 0, 0);

            using (var resp = new HttpResponseMessage((HttpStatusCode)429))
            {
                resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
                Equal(now.AddSeconds(60), RefreshPolicy.RateLimitUntil(resp, now), "retry-after min clamp");
            }

            using (var resp = new HttpResponseMessage((HttpStatusCode)429))
            {
                resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(3));
                Equal(now.AddHours(2), RefreshPolicy.RateLimitUntil(resp, now), "retry-after max clamp");
            }

            using (var resp = new HttpResponseMessage((HttpStatusCode)429))
            {
                Equal(now.AddMinutes(30), RefreshPolicy.RateLimitUntil(resp, now), "retry-after default");
            }
        }

        static ServiceState Service(string name, double fiveUsed, double weekUsed)
        {
            var svc = new ServiceState(name, "", System.Drawing.Color.Black);
            svc.Data = new UsageData
            {
                Name = name,
                FiveHourUsed = fiveUsed,
                WeeklyUsed = weekUsed
            };
            return svc;
        }

        static void True(bool value, string label)
        {
            if (!value) throw new InvalidOperationException(label + ": expected true");
        }

        static void Equal(TimeSpan expected, TimeSpan actual, string label)
        {
            if (expected != actual)
                throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual);
        }

        static void Equal(DateTime expected, DateTime actual, string label)
        {
            if (expected != actual)
                throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual);
        }
    }
}
