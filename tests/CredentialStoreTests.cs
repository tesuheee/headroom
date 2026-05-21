using System;
using System.IO;

namespace Headroom
{
    static class CredentialStoreTests
    {
        public static void Run(string root)
        {
            string dir = Path.Combine(Path.GetTempPath(), "HeadroomCredentialTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                TestClaudePreservesUnknownFields(dir);
                TestCodexPreservesUnknownFields(dir);
                TestMissingAndInvalidFiles(dir);
                Console.WriteLine("CredentialStoreTests: passed");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        static void TestClaudePreservesUnknownFields(string dir)
        {
            string path = Path.Combine(dir, "claude.json");
            File.WriteAllText(path,
                "{\"top\":\"keep\",\"claudeAiOauth\":{\"accessToken\":\"old\",\"refreshToken\":\"old-refresh\",\"expiresAt\":1,\"custom\":\"keep\"}}");

            ClaudeCredentialStore.Write(path, new ClaudeCredentials
            {
                AccessToken = "new",
                RefreshToken = null,
                ExpiresAtMs = 99
            }, new[] { "scope-a" });

            string json = File.ReadAllText(path);
            Contains(json, "\"top\":\"keep\"", "Claude top-level unknown field");
            Contains(json, "\"custom\":\"keep\"", "Claude nested unknown field");
            Contains(json, "\"accessToken\":\"new\"", "Claude access token update");
            Contains(json, "\"refreshToken\":\"old-refresh\"", "Claude refresh token preservation");
            Contains(json, "\"expiresAt\":99", "Claude expiry update");

            ClaudeCredentials credentials;
            True(ClaudeCredentialStore.Read(path, out credentials), "Claude read");
            Equal("new", credentials.AccessToken, "Claude read access token");
            Equal("old-refresh", credentials.RefreshToken, "Claude read refresh token");
            Equal(99L, credentials.ExpiresAtMs, "Claude read expiry");
        }

        static void TestCodexPreservesUnknownFields(string dir)
        {
            string path = Path.Combine(dir, "codex.json");
            File.WriteAllText(path,
                "{\"auth_mode\":\"chatgpt\",\"top\":\"keep\",\"tokens\":{\"access_token\":\"old\",\"refresh_token\":\"old-refresh\",\"id_token\":\"old-id\",\"account_id\":\"old-account\",\"expires_at_ms\":1,\"custom\":\"keep\"}}");

            CodexCredentialStore.Write(path, new CodexCredentials
            {
                AccessToken = "new",
                RefreshToken = null,
                IdToken = null,
                AccountId = "new-account",
                ExpiresAtMs = 99
            });

            string json = File.ReadAllText(path);
            Contains(json, "\"top\":\"keep\"", "Codex top-level unknown field");
            Contains(json, "\"custom\":\"keep\"", "Codex token unknown field");
            Contains(json, "\"access_token\":\"new\"", "Codex access token update");
            Contains(json, "\"refresh_token\":\"old-refresh\"", "Codex refresh token preservation");
            Contains(json, "\"id_token\":\"old-id\"", "Codex id token preservation");
            Contains(json, "\"account_id\":\"new-account\"", "Codex account update");
            Contains(json, "\"expires_at_ms\":99", "Codex expiry update");

            CodexCredentials credentials;
            True(CodexCredentialStore.Read(path, out credentials), "Codex read");
            Equal("new", credentials.AccessToken, "Codex read access token");
            Equal("old-refresh", credentials.RefreshToken, "Codex read refresh token");
            Equal("old-id", credentials.IdToken, "Codex read id token");
            Equal("new-account", credentials.AccountId, "Codex read account");
            Equal(99L, credentials.ExpiresAtMs, "Codex read expiry");
            Equal("old-id", CodexCredentialStore.ReadIdToken(path), "Codex read id token helper");
        }

        static void TestMissingAndInvalidFiles(string dir)
        {
            ClaudeCredentials claude;
            True(!ClaudeCredentialStore.Read(Path.Combine(dir, "missing-claude.json"), out claude), "Claude missing file");

            string invalidClaude = Path.Combine(dir, "invalid-claude.json");
            File.WriteAllText(invalidClaude, "{invalid");
            True(!ClaudeCredentialStore.Read(invalidClaude, out claude), "Claude invalid JSON");

            CodexCredentials codex;
            True(!CodexCredentialStore.Read(Path.Combine(dir, "missing-codex.json"), out codex), "Codex missing file");

            string invalidCodex = Path.Combine(dir, "invalid-codex.json");
            File.WriteAllText(invalidCodex, "{invalid");
            True(!CodexCredentialStore.Read(invalidCodex, out codex), "Codex invalid JSON");
        }

        static void Contains(string text, string expected, string label)
        {
            if (text.IndexOf(expected, StringComparison.Ordinal) < 0)
                throw new InvalidOperationException(label + ": missing " + expected + " in " + text);
        }

        static void True(bool value, string label)
        {
            if (!value) throw new InvalidOperationException(label + ": expected true");
        }

        static void Equal(string expected, string actual, string label)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual);
        }

        static void Equal(long expected, long actual, string label)
        {
            if (expected != actual)
                throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual);
        }
    }
}
