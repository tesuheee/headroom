using System;
using System.Threading.Tasks;

namespace Headroom
{
    partial class UsageForm
    {
        const string CodexUrl = "https://chatgpt.com/codex/cloud/settings/analytics#usage";

        internal static string CodexCredentialPath
        {
            get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json"); }
        }

        static bool ReadCodexCredentials(out string token, out string accountId)
        {
            string refreshTokenIgnored;
            long expiresAtIgnored;
            return ReadCodexCredentials(out token, out refreshTokenIgnored, out accountId, out expiresAtIgnored);
        }

        static bool ReadCodexCredentials(out string token, out string refreshToken, out string accountId, out long expiresAtMs)
        {
            token = null;
            refreshToken = null;
            accountId = null;
            expiresAtMs = 0;
            CodexCredentials credentials;
            if (!CodexCredentialStore.Read(CodexCredentialPath, out credentials)) return false;
            token = credentials.AccessToken;
            refreshToken = credentials.RefreshToken;
            accountId = credentials.AccountId;
            expiresAtMs = credentials.ExpiresAtMs;
            return true;
        }

        static void WriteCodexCredentials(string accessToken, string refreshToken, string idToken, string accountId, long expiresAtMs)
        {
            CodexCredentialStore.Write(CodexCredentialPath, new CodexCredentials
            {
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                IdToken = idToken,
                AccountId = accountId,
                ExpiresAtMs = expiresAtMs
            });
        }

        async Task RefreshCodexViaApiAsync(ServiceState service, bool manual)
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
                    LoadFixture(service, "Codex", "codex.json", UsageParsers.ParseCodexApi);
                    return;
                }

                UsageFetchResult result = await CodexUsageClient.FetchAsync(httpClient, CodexCredentialPath);
                if (result.CredentialRefreshed) lastCodexCredNotify = DateTime.Now;
                ApplyFetchResult(service, result);
            }
            catch (Exception ex)
            {
                service.Status = "fetch_error";
                WriteDebug("codex-api-error.txt", ex.ToString());
            }
            finally
            {
                service.IsRefreshing = false;
                Invalidate();
            }
        }

        async Task<bool> StartCodexPkceLoginAsync(ServiceState service)
        {
            return await CodexBrowserLoginFlow.StartAsync(httpClient, CodexCredentialPath);
        }

    }
}
