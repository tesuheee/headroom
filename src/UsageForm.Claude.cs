using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Headroom
{
    partial class UsageForm
    {
        const string ClaudeUrl = "https://claude.ai/settings/usage";

        const string ClaudeOAuthClientId    = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
        const string ClaudeOAuthAuthorizeUrl = "https://claude.ai/oauth/authorize";
        const string ClaudeOAuthTokenUrl     = "https://console.anthropic.com/v1/oauth/token";
        const string ClaudeOAuthScopes       = "user:inference user:profile";

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
            string verifier = GeneratePkceVerifier();
            string challenge = GeneratePkceChallenge(verifier);
            string state = GeneratePkceVerifier();
            int port;
            try { port = GetFreeLocalPort(); }
            catch (Exception ex) { WriteDebug("claude-pkce-error.txt", "port: " + ex); return false; }

            string redirectUri = "http://localhost:" + port + "/callback";
            var listener = new System.Net.HttpListener();
            listener.Prefixes.Add("http://localhost:" + port + "/");
            try { listener.Start(); }
            catch (Exception ex) { WriteDebug("claude-pkce-error.txt", "listener: " + ex); return false; }

            string authorizeUrl = ClaudeOAuthAuthorizeUrl
                + "?response_type=code"
                + "&client_id=" + Uri.EscapeDataString(ClaudeOAuthClientId)
                + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
                + "&scope=" + Uri.EscapeDataString(ClaudeOAuthScopes)
                + "&code_challenge=" + Uri.EscapeDataString(challenge)
                + "&code_challenge_method=S256"
                + "&state=" + Uri.EscapeDataString(state)
                + "&code=true";

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(authorizeUrl) { UseShellExecute = true };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                try { listener.Close(); } catch { }
                WriteDebug("claude-pkce-error.txt", "browser: " + ex);
                return false;
            }

            string authCode;
            try
            {
                authCode = await WaitForOAuthCallbackAsync(listener, state, PkceLoginTimeoutMs);
            }
            catch (Exception ex)
            {
                WriteDebug("claude-pkce-error.txt", "callback: " + ex);
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
                    { "client_id",     ClaudeOAuthClientId },
                    { "code_verifier", verifier },
                    { "state",         state },
                };
                using (var content = new FormUrlEncodedContent(formParams))
                using (var resp = await httpClient.PostAsync(ClaudeOAuthTokenUrl, content))
                {
                    string body = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode)
                    {
                        WriteDebug("claude-pkce-error.txt", "exchange " + (int)resp.StatusCode + ": " + body);
                        return false;
                    }
                    string accessToken = ExtractJsonString(body, "access_token");
                    string refreshToken = ExtractJsonString(body, "refresh_token");
                    long expiresIn = ExtractJsonLong(body, "expires_in");
                    if (string.IsNullOrEmpty(accessToken))
                    {
                        WriteDebug("claude-pkce-error.txt", "missing access_token: " + body);
                        return false;
                    }
                    long expiresAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        + (expiresIn > 0 ? expiresIn * 1000 : 8L * 3600 * 1000);
                    WriteClaudeCredentials(accessToken, refreshToken, expiresAtMs, ClaudeOAuthScopes.Split(' '));
                    return true;
                }
            }
            catch (Exception ex)
            {
                WriteDebug("claude-pkce-error.txt", "exchange exception: " + ex);
                return false;
            }
        }

    }
}
