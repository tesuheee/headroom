using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Headroom
{
    sealed class ClaudeCredentials
    {
        public string AccessToken;
        public string RefreshToken;
        public long ExpiresAtMs;
    }

    sealed class CodexCredentials
    {
        public string AccessToken;
        public string RefreshToken;
        public string IdToken;
        public string AccountId;
        public long ExpiresAtMs;
    }

    static class ClaudeCredentialStore
    {
        public static bool Read(string path, out ClaudeCredentials credentials)
        {
            credentials = null;
            string content = CredentialFiles.TryReadFileWithRetry(path);
            if (content == null) return false;

            var root = Json.ParseObject(content);
            var oauth = Json.Object(root, "claudeAiOauth");
            string token = Json.String(oauth, "accessToken");
            long? expires = Json.Long(oauth, "expiresAt");
            if (string.IsNullOrEmpty(token) || !expires.HasValue) return false;

            credentials = new ClaudeCredentials
            {
                AccessToken = token,
                RefreshToken = Json.String(oauth, "refreshToken"),
                ExpiresAtMs = expires.Value
            };
            return true;
        }

        public static void Write(string path, ClaudeCredentials credentials, string[] scopes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var root = Json.ParseObject(CredentialFiles.TryReadFileWithRetry(path)) ?? new Dictionary<string, object>();
            var oauth = Json.ObjectOrNew(root, "claudeAiOauth");
            oauth["accessToken"] = credentials.AccessToken ?? "";
            if (credentials.RefreshToken != null) oauth["refreshToken"] = credentials.RefreshToken;
            oauth["expiresAt"] = credentials.ExpiresAtMs;
            if (scopes != null) oauth["scopes"] = scopes;
            File.WriteAllText(path, Json.Serialize(root) + "\n", new System.Text.UTF8Encoding(false));
        }
    }

    static class CodexCredentialStore
    {
        public static bool Read(string path, out CodexCredentials credentials)
        {
            credentials = null;
            string content = CredentialFiles.TryReadFileWithRetry(path);
            if (content == null) return false;

            var root = Json.ParseObject(content);
            var tokens = Json.Object(root, "tokens");
            string token = Json.String(tokens, "access_token");
            if (string.IsNullOrEmpty(token)) return false;

            long? expires = Json.Long(tokens, "expires_at_ms");
            credentials = new CodexCredentials
            {
                AccessToken = token,
                RefreshToken = Json.String(tokens, "refresh_token"),
                IdToken = Json.String(tokens, "id_token"),
                AccountId = Json.String(tokens, "account_id"),
                ExpiresAtMs = expires.HasValue ? expires.Value : 0
            };
            return true;
        }

        public static string ReadIdToken(string path)
        {
            string content = CredentialFiles.TryReadFileWithRetry(path);
            if (content == null) return null;
            return Json.String(Json.Object(Json.ParseObject(content), "tokens"), "id_token");
        }

        public static void Write(string path, CodexCredentials credentials)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var root = Json.ParseObject(CredentialFiles.TryReadFileWithRetry(path)) ?? new Dictionary<string, object>();
            root["auth_mode"] = "chatgpt";
            var tokens = Json.ObjectOrNew(root, "tokens");
            if (!string.IsNullOrEmpty(credentials.IdToken)) tokens["id_token"] = credentials.IdToken;
            tokens["access_token"] = credentials.AccessToken ?? "";
            if (!string.IsNullOrEmpty(credentials.RefreshToken)) tokens["refresh_token"] = credentials.RefreshToken;
            if (!string.IsNullOrEmpty(credentials.AccountId)) tokens["account_id"] = credentials.AccountId;
            if (credentials.ExpiresAtMs > 0) tokens["expires_at_ms"] = credentials.ExpiresAtMs;
            root["last_refresh"] = System.DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            File.WriteAllText(path, Json.Serialize(root) + "\n", new System.Text.UTF8Encoding(false));
        }
    }

    static class CredentialFiles
    {
        public static string TryReadFileWithRetry(string path)
        {
            for (int i = 0; i < 3; i++)
            {
                try { return File.ReadAllText(path); }
                catch (FileNotFoundException) { return null; }
                catch (DirectoryNotFoundException) { return null; }
                catch (IOException) { System.Threading.Thread.Sleep(50); }
                catch { return null; }
            }
            return null;
        }
    }
}
