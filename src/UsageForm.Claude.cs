using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
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
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
            string content = TryReadFileWithRetry(path);
            if (content == null) return false;

            var t = Regex.Match(content, "\"accessToken\"\\s*:\\s*\"([^\"]+)\"");
            var e = Regex.Match(content, "\"expiresAt\"\\s*:\\s*(\\d+)");
            if (!t.Success || !e.Success) return false;
            token = t.Groups[1].Value;
            if (!long.TryParse(e.Groups[1].Value, out expiresAtMs)) return false;

            var r = Regex.Match(content, "\"refreshToken\"\\s*:\\s*\"([^\"]+)\"");
            if (r.Success) refreshToken = r.Groups[1].Value;
            return true;
        }

        static void WriteClaudeCredentials(string accessToken, string refreshToken, long expiresAtMs, string[] scopes)
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var root = Json.ParseObject(TryReadFileWithRetry(path)) ?? new Dictionary<string, object>();
            var oauth = Json.ObjectOrNew(root, "claudeAiOauth");
            oauth["accessToken"] = accessToken ?? "";
            if (refreshToken != null) oauth["refreshToken"] = refreshToken;
            oauth["expiresAt"] = expiresAtMs;
            if (scopes != null) oauth["scopes"] = scopes;
            File.WriteAllText(path, Json.Serialize(root) + "\n", new System.Text.UTF8Encoding(false));
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

                string token, refreshToken;
                long expiresAtMs;
                if (!ReadClaudeCredentials(out token, out refreshToken, out expiresAtMs))
                {
                    service.Data = new UsageData { Name = "Claude", Source = "Claude API", UpdatedAt = DateTime.Now, Status = "login_required" };
                    service.Status = "login_required";
                    service.LastRefresh = DateTime.Now;
                    return;
                }
                if (IsClaudeTokenExpired(expiresAtMs))
                {
                    bool refreshed = !string.IsNullOrEmpty(refreshToken)
                        && await TryRefreshClaudeTokenAsync(refreshToken)
                        && ReadClaudeCredentials(out token, out refreshToken, out expiresAtMs);
                    if (!refreshed)
                    {
                        service.Data = new UsageData { Name = "Claude", Source = "Claude API", UpdatedAt = DateTime.Now, Status = "login_required" };
                        service.Status = "login_required";
                        service.LastRefresh = DateTime.Now;
                        return;
                    }
                }

                for (int attempt = 0; attempt < 2; attempt++)
                {
                    using (var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage"))
                    {
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
                        using (var resp = await httpClient.SendAsync(req))
                        {
                            int code = (int)resp.StatusCode;
                            if ((code == 401 || code == 403) && attempt == 0 && !string.IsNullOrEmpty(refreshToken)
                                && await TryRefreshClaudeTokenAsync(refreshToken)
                                && ReadClaudeCredentials(out token, out refreshToken, out expiresAtMs))
                            {
                                continue;
                            }
                            if (code == 401 || code == 403)
                            {
                                service.Data = new UsageData { Name = "Claude", Source = "Claude API", UpdatedAt = DateTime.Now, Status = "login_required" };
                                service.Status = "login_required";
                                service.LastRefresh = DateTime.Now;
                                return;
                            }
                            if (code == 429)
                            {
                                if (attempt == 0 && !string.IsNullOrEmpty(refreshToken)
                                    && ShouldRefreshClaudeTokenAfter429(resp)
                                    && await TryRefreshClaudeTokenAsync(refreshToken)
                                    && ReadClaudeCredentials(out token, out refreshToken, out expiresAtMs))
                                {
                                    continue;
                                }
                                ApplyRateLimit(service, resp, "claude-api-error.txt", code);
                                return;
                            }
                            if (!resp.IsSuccessStatusCode)
                            {
                                service.Status = "fetch_error";
                                service.LastRefresh = DateTime.Now;
                                WriteDebug("claude-api-error.txt", "HTTP " + code);
                                return;
                            }
                            string json = await resp.Content.ReadAsStringAsync();
                            WriteDebug("claude-api.txt", json);
                            service.Data = UsageParsers.ParseClaudeApi(json);
                            service.Status = service.Data.Status;
                            service.RateLimitedUntil = null;
                            service.LastRefresh = DateTime.Now;
                            return;
                        }
                    }
                }
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

        static bool ShouldRefreshClaudeTokenAfter429(HttpResponseMessage resp)
        {
            var retryAfter = resp.Headers.RetryAfter;
            if (retryAfter == null) return true;
            if (retryAfter.Delta.HasValue)
                return retryAfter.Delta.Value.TotalSeconds <= 5;
            if (retryAfter.Date.HasValue)
                return retryAfter.Date.Value.LocalDateTime <= DateTime.Now.AddSeconds(5);
            return true;
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

        async Task<bool> TryRefreshClaudeTokenAsync(string refreshToken)
        {
            if (string.IsNullOrEmpty(refreshToken)) return false;
            try
            {
                var formParams = new Dictionary<string, string>
                {
                    { "grant_type",    "refresh_token" },
                    { "refresh_token", refreshToken },
                    { "client_id",     ClaudeOAuthClientId },
                };
                using (var content = new FormUrlEncodedContent(formParams))
                using (var resp = await httpClient.PostAsync(ClaudeOAuthTokenUrl, content))
                {
                    string body = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode)
                    {
                        WriteDebug("claude-refresh-error.txt", (int)resp.StatusCode + ": " + body);
                        return false;
                    }
                    string accessToken = ExtractJsonString(body, "access_token");
                    string newRefresh = ExtractJsonString(body, "refresh_token");
                    long expiresIn = ExtractJsonLong(body, "expires_in");
                    if (string.IsNullOrEmpty(accessToken)) return false;
                    long expiresAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        + (expiresIn > 0 ? expiresIn * 1000 : 8L * 3600 * 1000);
                    lastClaudeCredNotify = DateTime.Now;
                    WriteClaudeCredentials(accessToken,
                        !string.IsNullOrEmpty(newRefresh) ? newRefresh : refreshToken,
                        expiresAtMs, ClaudeOAuthScopes.Split(' '));
                    return true;
                }
            }
            catch (Exception ex)
            {
                WriteDebug("claude-refresh-error.txt", ex.ToString());
                return false;
            }
        }
    }
}
