using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace Headroom
{
    sealed class UsageFetchResult
    {
        public UsageData Data;
        public string Status;
        public DateTime? RateLimitedUntil;
        public string DebugName;
        public string DebugText;
        public bool CredentialRefreshed;

        public static UsageFetchResult LoginRequired(string name, string source)
        {
            return new UsageFetchResult
            {
                Data = new UsageData { Name = name, Source = source, UpdatedAt = DateTime.Now, Status = "login_required" },
                Status = "login_required"
            };
        }

        public static UsageFetchResult FetchError(string debugName, string debugText)
        {
            return new UsageFetchResult
            {
                Status = "fetch_error",
                DebugName = debugName,
                DebugText = debugText
            };
        }
    }

    static class ClaudeUsageClient
    {
        const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
        const string TokenUrl = "https://console.anthropic.com/v1/oauth/token";
        const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
        static readonly string[] Scopes = "user:inference user:profile".Split(' ');

        public static async Task<UsageFetchResult> FetchAsync(HttpClient httpClient, string credentialPath)
        {
            try
            {
                ClaudeCredentials credentials;
                if (!ClaudeCredentialStore.Read(credentialPath, out credentials))
                    return UsageFetchResult.LoginRequired("Claude", "Claude API");

                bool credentialRefreshed = false;
                if (IsTokenExpired(credentials.ExpiresAtMs))
                {
                    if (string.IsNullOrEmpty(credentials.RefreshToken) ||
                        !await TryRefreshTokenAsync(httpClient, credentialPath, credentials.RefreshToken))
                        return UsageFetchResult.LoginRequired("Claude", "Claude API");
                    credentialRefreshed = true;
                    if (!ClaudeCredentialStore.Read(credentialPath, out credentials))
                        return UsageFetchResult.LoginRequired("Claude", "Claude API");
                }

                for (int attempt = 0; attempt < 2; attempt++)
                {
                    using (var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl))
                    {
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
                        req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
                        using (var resp = await httpClient.SendAsync(req))
                        {
                            int code = (int)resp.StatusCode;
                            if ((code == 401 || code == 403) && attempt == 0 && !string.IsNullOrEmpty(credentials.RefreshToken)
                                && await TryRefreshTokenAsync(httpClient, credentialPath, credentials.RefreshToken)
                                && ClaudeCredentialStore.Read(credentialPath, out credentials))
                            {
                                credentialRefreshed = true;
                                continue;
                            }
                            if (code == 401 || code == 403)
                                return UsageFetchResult.LoginRequired("Claude", "Claude API");
                            if (code == 429)
                            {
                                if (attempt == 0 && !string.IsNullOrEmpty(credentials.RefreshToken)
                                    && ShouldRefreshTokenAfter429(resp)
                                    && await TryRefreshTokenAsync(httpClient, credentialPath, credentials.RefreshToken)
                                    && ClaudeCredentialStore.Read(credentialPath, out credentials))
                                {
                                    credentialRefreshed = true;
                                    continue;
                                }
                                DateTime until = RefreshPolicy.RateLimitUntil(resp, DateTime.Now);
                                return new UsageFetchResult
                                {
                                    Status = "rate_limited",
                                    RateLimitedUntil = until,
                                    DebugName = "claude-api-error.txt",
                                    DebugText = "HTTP " + code + "\r\nRetry-After: " + RetryAfterText(resp) + "\r\nBackoff until: " + until.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                                    CredentialRefreshed = credentialRefreshed
                                };
                            }
                            if (!resp.IsSuccessStatusCode)
                                return UsageFetchResult.FetchError("claude-api-error.txt", "HTTP " + code);

                            string json = await resp.Content.ReadAsStringAsync();
                            var data = UsageParsers.ParseClaudeApi(json);
                            return new UsageFetchResult
                            {
                                Data = data,
                                Status = data.Status,
                                DebugName = "claude-api.txt",
                                DebugText = json,
                                CredentialRefreshed = credentialRefreshed
                            };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return UsageFetchResult.FetchError("claude-api-error.txt", ex.ToString());
            }
            return UsageFetchResult.FetchError("claude-api-error.txt", "No response");
        }

        static bool IsTokenExpired(long expiresAtMs)
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= expiresAtMs - 30000;
        }

        static async Task<bool> TryRefreshTokenAsync(HttpClient httpClient, string credentialPath, string refreshToken)
        {
            if (string.IsNullOrEmpty(refreshToken)) return false;
            try
            {
                var formParams = new Dictionary<string, string>
                {
                    { "grant_type", "refresh_token" },
                    { "refresh_token", refreshToken },
                    { "client_id", ClientId },
                };
                using (var content = new FormUrlEncodedContent(formParams))
                using (var resp = await httpClient.PostAsync(TokenUrl, content))
                {
                    string body = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode)
                    {
                        DebugLog.Write("claude-refresh-error.txt", (int)resp.StatusCode + ": " + body);
                        return false;
                    }
                    string accessToken = TokenResponse.String(body, "access_token");
                    string newRefresh = TokenResponse.String(body, "refresh_token");
                    long expiresIn = TokenResponse.Long(body, "expires_in");
                    if (string.IsNullOrEmpty(accessToken)) return false;
                    long expiresAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        + (expiresIn > 0 ? expiresIn * 1000 : 8L * 3600 * 1000);
                    ClaudeCredentialStore.Write(credentialPath, new ClaudeCredentials
                    {
                        AccessToken = accessToken,
                        RefreshToken = !string.IsNullOrEmpty(newRefresh) ? newRefresh : refreshToken,
                        ExpiresAtMs = expiresAtMs
                    }, Scopes);
                    return true;
                }
            }
            catch (Exception ex)
            {
                DebugLog.Write("claude-refresh-error.txt", ex.ToString());
                return false;
            }
        }

        static bool ShouldRefreshTokenAfter429(HttpResponseMessage resp)
        {
            var retryAfter = resp.Headers.RetryAfter;
            if (retryAfter == null) return true;
            if (retryAfter.Delta.HasValue)
                return retryAfter.Delta.Value.TotalSeconds <= 5;
            if (retryAfter.Date.HasValue)
                return retryAfter.Date.Value.LocalDateTime <= DateTime.Now.AddSeconds(5);
            return true;
        }

        static string RetryAfterText(HttpResponseMessage resp)
        {
            return resp.Headers.RetryAfter == null ? "" : resp.Headers.RetryAfter.ToString();
        }
    }

    static class CodexUsageClient
    {
        const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
        const string TokenUrl = "https://auth.openai.com/oauth/token";
        const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";

        public static async Task<UsageFetchResult> FetchAsync(HttpClient httpClient, string credentialPath)
        {
            try
            {
                CodexCredentials credentials;
                if (!CodexCredentialStore.Read(credentialPath, out credentials))
                    return UsageFetchResult.LoginRequired("Codex", "Codex API");

                bool credentialRefreshed = false;
                if (credentials.ExpiresAtMs > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= credentials.ExpiresAtMs - 30000
                    && !string.IsNullOrEmpty(credentials.RefreshToken))
                {
                    if (await TryRefreshTokenAsync(httpClient, credentialPath, credentials.RefreshToken, credentials.AccountId))
                    {
                        credentialRefreshed = true;
                        CodexCredentialStore.Read(credentialPath, out credentials);
                    }
                }

                for (int attempt = 0; attempt < 2; attempt++)
                {
                    using (var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl))
                    {
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
                        req.Headers.UserAgent.ParseAdd("codex-cli");
                        if (!string.IsNullOrEmpty(credentials.AccountId))
                            req.Headers.Add("ChatGPT-Account-Id", credentials.AccountId);
                        using (var resp = await httpClient.SendAsync(req))
                        {
                            int code = (int)resp.StatusCode;
                            if ((code == 401 || code == 403) && attempt == 0 && !string.IsNullOrEmpty(credentials.RefreshToken)
                                && await TryRefreshTokenAsync(httpClient, credentialPath, credentials.RefreshToken, credentials.AccountId)
                                && CodexCredentialStore.Read(credentialPath, out credentials))
                            {
                                credentialRefreshed = true;
                                continue;
                            }
                            if (code == 401 || code == 403)
                                return UsageFetchResult.LoginRequired("Codex", "Codex API");
                            if (code == 429)
                            {
                                DateTime until = RefreshPolicy.RateLimitUntil(resp, DateTime.Now);
                                return new UsageFetchResult
                                {
                                    Status = "rate_limited",
                                    RateLimitedUntil = until,
                                    DebugName = "codex-api-error.txt",
                                    DebugText = "HTTP " + code + "\r\nRetry-After: " + RetryAfterText(resp) + "\r\nBackoff until: " + until.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                                    CredentialRefreshed = credentialRefreshed
                                };
                            }
                            if (!resp.IsSuccessStatusCode)
                                return UsageFetchResult.FetchError("codex-api-error.txt", "HTTP " + code);

                            string json = await resp.Content.ReadAsStringAsync();
                            var data = UsageParsers.ParseCodexApi(json);
                            return new UsageFetchResult
                            {
                                Data = data,
                                Status = data.Status,
                                DebugName = "codex-api.txt",
                                DebugText = json,
                                CredentialRefreshed = credentialRefreshed
                            };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return UsageFetchResult.FetchError("codex-api-error.txt", ex.ToString());
            }
            return UsageFetchResult.FetchError("codex-api-error.txt", "No response");
        }

        static async Task<bool> TryRefreshTokenAsync(HttpClient httpClient, string credentialPath, string refreshToken, string existingAccountId)
        {
            if (string.IsNullOrEmpty(refreshToken)) return false;
            try
            {
                var formParams = new Dictionary<string, string>
                {
                    { "grant_type", "refresh_token" },
                    { "refresh_token", refreshToken },
                    { "client_id", ClientId },
                };
                using (var content = new FormUrlEncodedContent(formParams))
                using (var resp = await httpClient.PostAsync(TokenUrl, content))
                {
                    string body = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode)
                    {
                        DebugLog.Write("codex-refresh-error.txt", (int)resp.StatusCode + ": " + body);
                        return false;
                    }
                    string accessToken = TokenResponse.String(body, "access_token");
                    string newRefresh = TokenResponse.String(body, "refresh_token");
                    string idToken = TokenResponse.String(body, "id_token");
                    long expiresIn = TokenResponse.Long(body, "expires_in");
                    if (string.IsNullOrEmpty(accessToken)) return false;
                    if (string.IsNullOrEmpty(idToken)) idToken = CodexCredentialStore.ReadIdToken(credentialPath);
                    string accountId = TokenResponse.String(body, "account_id")
                        ?? ExtractAccountIdFromIdToken(idToken)
                        ?? existingAccountId;
                    long expiresAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        + (expiresIn > 0 ? expiresIn * 1000 : 8L * 3600 * 1000);
                    CodexCredentialStore.Write(credentialPath, new CodexCredentials
                    {
                        AccessToken = accessToken,
                        RefreshToken = !string.IsNullOrEmpty(newRefresh) ? newRefresh : refreshToken,
                        IdToken = idToken,
                        AccountId = accountId,
                        ExpiresAtMs = expiresAtMs
                    });
                    return true;
                }
            }
            catch (Exception ex)
            {
                DebugLog.Write("codex-refresh-error.txt", ex.ToString());
                return false;
            }
        }

        public static string ExtractAccountIdFromIdToken(string idToken)
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
                var claims = Json.ParseObject(json);
                string id = Json.String(claims, "chatgpt_account_id");
                if (!string.IsNullOrEmpty(id)) return id;
                id = Json.String(Json.Object(claims, "https://api.openai.com/auth"), "chatgpt_account_id");
                if (!string.IsNullOrEmpty(id)) return id;
            }
            catch { }
            return null;
        }

        static string RetryAfterText(HttpResponseMessage resp)
        {
            return resp.Headers.RetryAfter == null ? "" : resp.Headers.RetryAfter.ToString();
        }
    }

    static class TokenResponse
    {
        public static string String(string json, string key)
        {
            return Json.String(Json.ParseObject(json), key);
        }

        public static long Long(string json, string key)
        {
            long? value = Json.Long(Json.ParseObject(json), key);
            return value.HasValue ? value.Value : 0;
        }
    }
}
