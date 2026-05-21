using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;

namespace Headroom
{
    partial class UsageForm
    {
        const string CodexUrl = "https://chatgpt.com/codex/cloud/settings/analytics#usage";

        const string CodexOAuthClientId     = "app_EMoamEEZ73f0CkXaXp7hrann";
        const string CodexOAuthAuthorizeUrl = "https://auth.openai.com/oauth/authorize";
        const string CodexOAuthTokenUrl     = "https://auth.openai.com/oauth/token";
        const string CodexOAuthScopes       = "openid profile email offline_access api.connectors.read api.connectors.invoke";
        const int CodexOAuthDefaultPort     = 1455;
        const int CodexOAuthFallbackPort    = 1457;

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
            string verifier = GeneratePkceVerifier();
            string challenge = GeneratePkceChallenge(verifier);
            string state = GeneratePkceVerifier();
            int port = CodexOAuthDefaultPort;
            System.Net.HttpListener listener;
            Exception firstError;
            if (!TryStartOAuthListener(port, out listener, out firstError))
            {
                port = CodexOAuthFallbackPort;
                Exception fallbackError;
                if (!TryStartOAuthListener(port, out listener, out fallbackError))
                {
                    WriteDebug("codex-pkce-error.txt", "listener " + CodexOAuthDefaultPort + ": " + firstError + "\nlistener " + CodexOAuthFallbackPort + ": " + fallbackError);
                    return false;
                }
            }

            string redirectUri = "http://localhost:" + port + "/auth/callback";

            string authorizeUrl = CodexOAuthAuthorizeUrl
                + "?response_type=code"
                + "&client_id=" + Uri.EscapeDataString(CodexOAuthClientId)
                + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
                + "&scope=" + Uri.EscapeDataString(CodexOAuthScopes)
                + "&code_challenge=" + Uri.EscapeDataString(challenge)
                + "&code_challenge_method=S256"
                + "&id_token_add_organizations=true"
                + "&codex_cli_simplified_flow=true"
                + "&state=" + Uri.EscapeDataString(state)
                + "&originator=headroom";

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(authorizeUrl) { UseShellExecute = true };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                try { listener.Close(); } catch { }
                WriteDebug("codex-pkce-error.txt", "browser: " + ex);
                return false;
            }

            string authCode;
            try
            {
                authCode = await WaitForOAuthCallbackAsync(listener, state, PkceLoginTimeoutMs);
            }
            catch (Exception ex)
            {
                WriteDebug("codex-pkce-error.txt", "callback: " + ex);
                return false;
            }
            finally
            {
                try { listener.Close(); } catch { }
            }

            try
            {
                var formParams = new Dictionary<string, string>
                {
                    { "grant_type",    "authorization_code" },
                    { "code",          authCode },
                    { "redirect_uri",  redirectUri },
                    { "client_id",     CodexOAuthClientId },
                    { "code_verifier", verifier },
                };
                using (var content = new FormUrlEncodedContent(formParams))
                using (var resp = await httpClient.PostAsync(CodexOAuthTokenUrl, content))
                {
                    string body = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode)
                    {
                        WriteDebug("codex-pkce-error.txt", "exchange " + (int)resp.StatusCode + ": " + body);
                        return false;
                    }
                    string accessToken = ExtractJsonString(body, "access_token");
                    string refreshToken = ExtractJsonString(body, "refresh_token");
                    string idToken = ExtractJsonString(body, "id_token");
                    long expiresIn = ExtractJsonLong(body, "expires_in");
                    if (string.IsNullOrEmpty(accessToken))
                    {
                        WriteDebug("codex-pkce-error.txt", "missing access_token: " + body);
                        return false;
                    }
                    string accountId = ExtractJsonString(body, "account_id")
                                       ?? CodexUsageClient.ExtractAccountIdFromIdToken(idToken);
                    long expiresAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        + (expiresIn > 0 ? expiresIn * 1000 : 8L * 3600 * 1000);
                    WriteCodexCredentials(accessToken, refreshToken, idToken, accountId, expiresAtMs);
                    return true;
                }
            }
            catch (Exception ex)
            {
                WriteDebug("codex-pkce-error.txt", "exchange exception: " + ex);
                return false;
            }
        }

    }
}
