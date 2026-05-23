using System;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Headroom
{
    partial class UsageForm
    {
        const string ClaudeUrl = "https://claude.ai/settings/usage";

        internal static string ClaudeCredentialPath
        {
            get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json"); }
        }

        static bool IsClaudeTokenExpired(long expiresAtMs)
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= expiresAtMs - 30000;
        }

        static bool ReadClaudeCredentials(out string token, out long expiresAtMs)
        {
            string refreshTokenIgnored;
            return ReadClaudeCredentials(out token, out refreshTokenIgnored, out expiresAtMs);
        }

        static bool ReadClaudeCredentials(out string token, out string refreshToken, out long expiresAtMs)
        {
            token = null;
            refreshToken = null;
            expiresAtMs = 0;
            ClaudeCredentials credentials;
            if (!ClaudeCredentialStore.Read(ClaudeCredentialPath, out credentials)) return false;
            token = credentials.AccessToken;
            refreshToken = credentials.RefreshToken;
            expiresAtMs = credentials.ExpiresAtMs;
            return true;
        }

        static void WriteClaudeCredentials(string accessToken, string refreshToken, long expiresAtMs, string[] scopes)
        {
            ClaudeCredentialStore.Write(ClaudeCredentialPath, new ClaudeCredentials
            {
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                ExpiresAtMs = expiresAtMs
            }, scopes);
        }

        async Task RefreshClaudeViaApiAsync(ServiceState service, bool manual)
        {
            if (service.ManuallyLoggedOut)
            {
                MarkLoggedOut(service);
                Invalidate();
                return;
            }
            if (service.IsRefreshing) return;
            service.IsRefreshing = true;
            if (manual) service.Status = "updating";
            Invalidate();
            try
            {
                if (HeadroomOptions.FixtureMode)
                {
                    LoadFixture(service, "Claude", "claude.json", UsageParsers.ParseClaudeApi);
                    return;
                }

                UsageFetchResult result = await ClaudeUsageClient.FetchAsync(httpClient, ClaudeCredentialPath);
                if (result.CredentialRefreshed) lastClaudeCredNotify = DateTime.Now;
                ApplyFetchResult(service, result);
            }
            catch (Exception ex)
            {
                service.Status = "fetch_error";
                WriteDebug("claude-api-error.txt", ex.ToString());
            }
            finally
            {
                service.IsRefreshing = false;
                Invalidate();
            }
        }

        async Task<bool> StartClaudePkceLoginAsync(ServiceState service)
        {
            return await ClaudeBrowserLoginFlow.StartAsync(httpClient, ClaudeCredentialPath);
        }

    }
}
