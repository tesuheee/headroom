using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
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
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
            string content = TryReadFileWithRetry(path);
            if (content == null) return false;

            var t = Regex.Match(content, "\"access_token\"\\s*:\\s*\"([^\"]+)\"");
            if (!t.Success) return false;
            token = t.Groups[1].Value;

            var rt = Regex.Match(content, "\"refresh_token\"\\s*:\\s*\"([^\"]+)\"");
            if (rt.Success) refreshToken = rt.Groups[1].Value;

            var a = Regex.Match(content, "\"account_id\"\\s*:\\s*\"([^\"]+)\"");
            if (a.Success) accountId = a.Groups[1].Value;

            var ex = Regex.Match(content, "\"expires_at_ms\"\\s*:\\s*(\\d+)");
            if (ex.Success) long.TryParse(ex.Groups[1].Value, out expiresAtMs);
            return true;
        }

        static string ReadCodexIdToken()
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
            string content = TryReadFileWithRetry(path);
            if (content == null) return null;
            return ExtractJsonString(content, "id_token");
        }

        static void WriteCodexCredentials(string accessToken, string refreshToken, string idToken, string accountId, long expiresAtMs)
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var root = Json.ParseObject(TryReadFileWithRetry(path)) ?? new Dictionary<string, object>();
            root["auth_mode"] = "chatgpt";
            var tokens = Json.ObjectOrNew(root, "tokens");
            if (!string.IsNullOrEmpty(idToken)) tokens["id_token"] = idToken;
            tokens["access_token"] = accessToken ?? "";
            if (!string.IsNullOrEmpty(refreshToken)) tokens["refresh_token"] = refreshToken;
            if (!string.IsNullOrEmpty(accountId)) tokens["account_id"] = accountId;
            if (expiresAtMs > 0) tokens["expires_at_ms"] = expiresAtMs;
            root["last_refresh"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            File.WriteAllText(path, Json.Serialize(root) + "\n", new System.Text.UTF8Encoding(false));
        }

        static string ExtractAccountIdFromIdToken(string idToken)
        {
            if (string.IsNullOrEmpty(idToken)) return null;
            var parts = idToken.Split('.');
            if (parts.Length < 2) return null;
            try
            {
                string payload = parts[1].Replace('-', '+').Replace('_', '/');
                int mod = payload.Length % 4;
                if (mod > 0) payload += new string('=', 4 - mod);
                byte[] bytes = Convert.FromBase64String(payload);
                string json = System.Text.Encoding.UTF8.GetString(bytes);
                string id = ExtractJsonString(json, "chatgpt_account_id");
                if (!string.IsNullOrEmpty(id)) return id;
                var m = Regex.Match(json, "\"https://api\\.openai\\.com/auth\"\\s*:\\s*\\{([^}]+)\\}");
                if (m.Success)
                {
                    id = ExtractJsonString(m.Groups[1].Value, "chatgpt_account_id");
                    if (!string.IsNullOrEmpty(id)) return id;
                }
            }
            catch { }
            return null;
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

                string token, refreshToken, accountId;
                long expiresAtMs;
                if (!ReadCodexCredentials(out token, out refreshToken, out accountId, out expiresAtMs))
                {
                    service.Data = new UsageData { Name = "Codex", Source = "Codex API", UpdatedAt = DateTime.Now, Status = "login_required" };
                    service.Status = "login_required";
                    service.LastRefresh = DateTime.Now;
                    return;
                }

                if (expiresAtMs > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= expiresAtMs - 30000
                    && !string.IsNullOrEmpty(refreshToken))
                {
                    if (await TryRefreshCodexTokenAsync(refreshToken, accountId))
                        ReadCodexCredentials(out token, out refreshToken, out accountId, out expiresAtMs);
                }

                for (int attempt = 0; attempt < 2; attempt++)
                {
                    using (var req = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage"))
                    {
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        req.Headers.UserAgent.ParseAdd("codex-cli");
                        if (!string.IsNullOrEmpty(accountId))
                            req.Headers.Add("ChatGPT-Account-Id", accountId);
                        using (var resp = await httpClient.SendAsync(req))
                        {
                            int code = (int)resp.StatusCode;
                            if ((code == 401 || code == 403) && attempt == 0 && !string.IsNullOrEmpty(refreshToken)
                                && await TryRefreshCodexTokenAsync(refreshToken, accountId)
                                && ReadCodexCredentials(out token, out refreshToken, out accountId, out expiresAtMs))
                            {
                                continue;
                            }
                            if (code == 401 || code == 403)
                            {
                                service.Data = new UsageData { Name = "Codex", Source = "Codex API", UpdatedAt = DateTime.Now, Status = "login_required" };
                                service.Status = "login_required";
                                service.LastRefresh = DateTime.Now;
                                return;
                            }
                            if (code == 429)
                            {
                                ApplyRateLimit(service, resp, "codex-api-error.txt", code);
                                return;
                            }
                            if (!resp.IsSuccessStatusCode)
                            {
                                service.Status = "fetch_error";
                                service.LastRefresh = DateTime.Now;
                                WriteDebug("codex-api-error.txt", "HTTP " + code);
                                return;
                            }
                            string json = await resp.Content.ReadAsStringAsync();
                            WriteDebug("codex-api.txt", json);
                            service.Data = UsageParsers.ParseCodexApi(json);
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
                                       ?? ExtractAccountIdFromIdToken(idToken);
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

        async Task<bool> TryRefreshCodexTokenAsync(string refreshToken, string existingAccountId)
        {
            if (string.IsNullOrEmpty(refreshToken)) return false;
            try
            {
                var formParams = new Dictionary<string, string>
                {
                    { "grant_type",    "refresh_token" },
                    { "refresh_token", refreshToken },
                    { "client_id",     CodexOAuthClientId },
                };
                using (var content = new FormUrlEncodedContent(formParams))
                using (var resp = await httpClient.PostAsync(CodexOAuthTokenUrl, content))
                {
                    string body = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode)
                    {
                        WriteDebug("codex-refresh-error.txt", (int)resp.StatusCode + ": " + body);
                        return false;
                    }
                    string accessToken = ExtractJsonString(body, "access_token");
                    string newRefresh = ExtractJsonString(body, "refresh_token");
                    string idToken = ExtractJsonString(body, "id_token");
                    long expiresIn = ExtractJsonLong(body, "expires_in");
                    if (string.IsNullOrEmpty(accessToken)) return false;
                    if (string.IsNullOrEmpty(idToken)) idToken = ReadCodexIdToken();
                    string accountId = ExtractJsonString(body, "account_id")
                                       ?? ExtractAccountIdFromIdToken(idToken)
                                       ?? existingAccountId;
                    long expiresAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        + (expiresIn > 0 ? expiresIn * 1000 : 8L * 3600 * 1000);
                    lastCodexCredNotify = DateTime.Now;
                    WriteCodexCredentials(accessToken,
                        !string.IsNullOrEmpty(newRefresh) ? newRefresh : refreshToken,
                        idToken, accountId, expiresAtMs);
                    return true;
                }
            }
            catch (Exception ex)
            {
                WriteDebug("codex-refresh-error.txt", ex.ToString());
                return false;
            }
        }
    }
}
