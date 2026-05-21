using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Headroom
{
    static class OAuthPkce
    {
        public static string GenerateVerifier()
        {
            var bytes = new byte[32];
            using (var rng = new RNGCryptoServiceProvider())
                rng.GetBytes(bytes);
            return Base64UrlEncode(bytes);
        }

        public static string GenerateChallenge(string verifier)
        {
            using (var sha = new SHA256Managed())
            {
                byte[] hash = sha.ComputeHash(System.Text.Encoding.ASCII.GetBytes(verifier));
                return Base64UrlEncode(hash);
            }
        }

        static string Base64UrlEncode(byte[] data)
        {
            return Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }
    }

    static class LocalOAuthCallbackListener
    {
        public static int GetFreeLocalPort()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public static bool TryStart(int port, out HttpListener listener, out Exception error)
        {
            listener = new HttpListener();
            error = null;
            listener.Prefixes.Add("http://localhost:" + port + "/");
            try
            {
                listener.Start();
                return true;
            }
            catch (Exception ex)
            {
                error = ex;
                try { listener.Close(); } catch { }
                listener = null;
                return false;
            }
        }

        public static async Task<string> WaitForCallbackAsync(HttpListener listener, string expectedState, int timeoutMs)
        {
            Task<HttpListenerContext> getCtx = listener.GetContextAsync();
            Task delay = Task.Delay(timeoutMs);
            var completed = await Task.WhenAny(getCtx, delay);
            if (completed == delay)
            {
                try { listener.Stop(); } catch { }
                throw new TimeoutException("OAuth callback timed out");
            }
            HttpListenerContext ctx = await getCtx;
            string code = null;
            string error = null;
            try
            {
                string query = ctx.Request.Url.Query ?? "";
                var values = ParseQuery(query);
                if (values.ContainsKey("error")) error = values["error"];
                if (!values.ContainsKey("state")) error = error ?? "state_missing";
                else if (values["state"] != expectedState) error = error ?? "state_mismatch";
                if (values.ContainsKey("code") && error == null) code = values["code"];

                string body = error == null
                    ? "<!doctype html><html lang='ja'><head><meta charset='utf-8'><title>Headroom</title></head><body style='font-family:Segoe UI,sans-serif;text-align:center;padding-top:80px;color:#222;'><h2>Headroom ログイン完了</h2><p>このタブを閉じてください。</p></body></html>"
                    : "<!doctype html><html lang='ja'><head><meta charset='utf-8'><title>Headroom</title></head><body style='font-family:Segoe UI,sans-serif;text-align:center;padding-top:80px;color:#b00;'><h2>Headroom ログイン失敗</h2><p>" + WebUtility.HtmlEncode(error) + "</p></body></html>";
                byte[] buf = System.Text.Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.ContentLength64 = buf.Length;
                await ctx.Response.OutputStream.WriteAsync(buf, 0, buf.Length);
            }
            finally
            {
                try { ctx.Response.Close(); } catch { }
            }
            if (code == null) throw new InvalidOperationException("OAuth error: " + (error ?? "no_code"));
            return code;
        }

        static Dictionary<string, string> ParseQuery(string query)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query)) return values;
            string q = query[0] == '?' ? query.Substring(1) : query;
            foreach (string part in q.Split('&'))
            {
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                string key = eq >= 0 ? part.Substring(0, eq) : part;
                string value = eq >= 0 ? part.Substring(eq + 1) : "";
                values[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value);
            }
            return values;
        }
    }

    static class ClaudeBrowserLoginFlow
    {
        const int TimeoutMs = 5 * 60 * 1000;
        const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
        const string AuthorizeUrl = "https://claude.ai/oauth/authorize";
        const string TokenUrl = "https://console.anthropic.com/v1/oauth/token";
        const string Scopes = "user:inference user:profile";

        public static async Task<bool> StartAsync(HttpClient httpClient, string credentialPath)
        {
            string verifier = OAuthPkce.GenerateVerifier();
            string challenge = OAuthPkce.GenerateChallenge(verifier);
            string state = OAuthPkce.GenerateVerifier();
            int port;
            try { port = LocalOAuthCallbackListener.GetFreeLocalPort(); }
            catch (Exception ex) { DebugLog.Write("claude-pkce-error.txt", "port: " + ex); return false; }

            HttpListener listener;
            Exception listenerError;
            if (!LocalOAuthCallbackListener.TryStart(port, out listener, out listenerError))
            {
                DebugLog.Write("claude-pkce-error.txt", "listener: " + listenerError);
                return false;
            }

            string redirectUri = "http://localhost:" + port + "/callback";
            string authorizeUrl = AuthorizeUrl
                + "?response_type=code"
                + "&client_id=" + Uri.EscapeDataString(ClientId)
                + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
                + "&scope=" + Uri.EscapeDataString(Scopes)
                + "&code_challenge=" + Uri.EscapeDataString(challenge)
                + "&code_challenge_method=S256"
                + "&state=" + Uri.EscapeDataString(state)
                + "&code=true";

            if (!OpenBrowser(authorizeUrl, listener, "claude-pkce-error.txt")) return false;
            string authCode;
            try { authCode = await LocalOAuthCallbackListener.WaitForCallbackAsync(listener, state, TimeoutMs); }
            catch (Exception ex) { DebugLog.Write("claude-pkce-error.txt", "callback: " + ex); return false; }
            finally { try { listener.Close(); } catch { } }

            try
            {
                var formParams = new Dictionary<string, string>
                {
                    { "grant_type", "authorization_code" },
                    { "code", authCode },
                    { "redirect_uri", redirectUri },
                    { "client_id", ClientId },
                    { "code_verifier", verifier },
                    { "state", state },
                };
                using (var content = new FormUrlEncodedContent(formParams))
                using (var resp = await httpClient.PostAsync(TokenUrl, content))
                {
                    string body = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode)
                    {
                        DebugLog.Write("claude-pkce-error.txt", "exchange " + (int)resp.StatusCode + ": " + body);
                        return false;
                    }
                    string accessToken = TokenResponse.String(body, "access_token");
                    string refreshToken = TokenResponse.String(body, "refresh_token");
                    long expiresIn = TokenResponse.Long(body, "expires_in");
                    if (string.IsNullOrEmpty(accessToken))
                    {
                        DebugLog.Write("claude-pkce-error.txt", "missing access_token: " + body);
                        return false;
                    }
                    long expiresAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        + (expiresIn > 0 ? expiresIn * 1000 : 8L * 3600 * 1000);
                    ClaudeCredentialStore.Write(credentialPath, new ClaudeCredentials
                    {
                        AccessToken = accessToken,
                        RefreshToken = refreshToken,
                        ExpiresAtMs = expiresAtMs
                    }, Scopes.Split(' '));
                    return true;
                }
            }
            catch (Exception ex)
            {
                DebugLog.Write("claude-pkce-error.txt", "exchange exception: " + ex);
                return false;
            }
        }

        static bool OpenBrowser(string authorizeUrl, HttpListener listener, string debugName)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(authorizeUrl) { UseShellExecute = true };
                System.Diagnostics.Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                try { listener.Close(); } catch { }
                DebugLog.Write(debugName, "browser: " + ex);
                return false;
            }
        }
    }

    static class CodexBrowserLoginFlow
    {
        const int TimeoutMs = 5 * 60 * 1000;
        const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
        const string AuthorizeUrl = "https://auth.openai.com/oauth/authorize";
        const string TokenUrl = "https://auth.openai.com/oauth/token";
        const string Scopes = "openid profile email offline_access api.connectors.read api.connectors.invoke";
        const int DefaultPort = 1455;
        const int FallbackPort = 1457;

        public static async Task<bool> StartAsync(HttpClient httpClient, string credentialPath)
        {
            string verifier = OAuthPkce.GenerateVerifier();
            string challenge = OAuthPkce.GenerateChallenge(verifier);
            string state = OAuthPkce.GenerateVerifier();
            int port = DefaultPort;
            HttpListener listener;
            Exception firstError;
            if (!LocalOAuthCallbackListener.TryStart(port, out listener, out firstError))
            {
                port = FallbackPort;
                Exception fallbackError;
                if (!LocalOAuthCallbackListener.TryStart(port, out listener, out fallbackError))
                {
                    DebugLog.Write("codex-pkce-error.txt", "listener " + DefaultPort + ": " + firstError + "\nlistener " + FallbackPort + ": " + fallbackError);
                    return false;
                }
            }

            string redirectUri = "http://localhost:" + port + "/auth/callback";
            string authorizeUrl = AuthorizeUrl
                + "?response_type=code"
                + "&client_id=" + Uri.EscapeDataString(ClientId)
                + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
                + "&scope=" + Uri.EscapeDataString(Scopes)
                + "&code_challenge=" + Uri.EscapeDataString(challenge)
                + "&code_challenge_method=S256"
                + "&id_token_add_organizations=true"
                + "&codex_cli_simplified_flow=true"
                + "&state=" + Uri.EscapeDataString(state)
                + "&originator=headroom";

            if (!OpenBrowser(authorizeUrl, listener, "codex-pkce-error.txt")) return false;
            string authCode;
            try { authCode = await LocalOAuthCallbackListener.WaitForCallbackAsync(listener, state, TimeoutMs); }
            catch (Exception ex) { DebugLog.Write("codex-pkce-error.txt", "callback: " + ex); return false; }
            finally { try { listener.Close(); } catch { } }

            try
            {
                var formParams = new Dictionary<string, string>
                {
                    { "grant_type", "authorization_code" },
                    { "code", authCode },
                    { "redirect_uri", redirectUri },
                    { "client_id", ClientId },
                    { "code_verifier", verifier },
                };
                using (var content = new FormUrlEncodedContent(formParams))
                using (var resp = await httpClient.PostAsync(TokenUrl, content))
                {
                    string body = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode)
                    {
                        DebugLog.Write("codex-pkce-error.txt", "exchange " + (int)resp.StatusCode + ": " + body);
                        return false;
                    }
                    string accessToken = TokenResponse.String(body, "access_token");
                    string refreshToken = TokenResponse.String(body, "refresh_token");
                    string idToken = TokenResponse.String(body, "id_token");
                    long expiresIn = TokenResponse.Long(body, "expires_in");
                    if (string.IsNullOrEmpty(accessToken))
                    {
                        DebugLog.Write("codex-pkce-error.txt", "missing access_token: " + body);
                        return false;
                    }
                    string accountId = TokenResponse.String(body, "account_id")
                        ?? CodexUsageClient.ExtractAccountIdFromIdToken(idToken);
                    long expiresAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        + (expiresIn > 0 ? expiresIn * 1000 : 8L * 3600 * 1000);
                    CodexCredentialStore.Write(credentialPath, new CodexCredentials
                    {
                        AccessToken = accessToken,
                        RefreshToken = refreshToken,
                        IdToken = idToken,
                        AccountId = accountId,
                        ExpiresAtMs = expiresAtMs
                    });
                    return true;
                }
            }
            catch (Exception ex)
            {
                DebugLog.Write("codex-pkce-error.txt", "exchange exception: " + ex);
                return false;
            }
        }

        static bool OpenBrowser(string authorizeUrl, HttpListener listener, string debugName)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(authorizeUrl) { UseShellExecute = true };
                System.Diagnostics.Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                try { listener.Close(); } catch { }
                DebugLog.Write(debugName, "browser: " + ex);
                return false;
            }
        }
    }
}
