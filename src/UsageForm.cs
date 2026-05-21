using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Headroom
{
    sealed partial class UsageForm : Form
    {
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
        [DllImport("user32.dll", EntryPoint = "SendMessage")]
        static extern IntPtr SendMessageIcon(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        const int WmNcLButtonDown = 0xA1;
        const int HtCaption = 0x2;
        const int PkceLoginTimeoutMs = 5 * 60 * 1000;
        const int DefaultRateLimitBackoffMinutes = 30;
        const float LabelFontSize   = 10.8f;
        const float PercentFontSize = 16.5f;
        const float ResetFontSize   =  9.9f;

        readonly Timer paintTimer = new Timer();
        readonly Timer schedulerTimer = new Timer();
        readonly Dictionary<string, Rectangle> hits = new Dictionary<string, Rectangle>();
        readonly Dictionary<string, Rectangle> silentHits = new Dictionary<string, Rectangle>();
        string pendingSilentKey = "";
        Point pendingSilentScreenStart;
        Point pendingSilentWindowStart;
        bool silentDragging;
        readonly WidgetSettings settings = WidgetSettings.Load();
        readonly ToolTip toolTip = new ToolTip { InitialDelay = 2000, ReshowDelay = 100, ShowAlways = true };
        readonly Timer tooltipTimer = new Timer();
        string pendingTooltipText = "";
        Point pendingTooltipLocation;

        static readonly HttpClient httpClient;
        FileSystemWatcher claudeCredWatcher;
        FileSystemWatcher codexCredWatcher;
        FileSystemWatcher fixtureWatcher;
        DateTime lastClaudeCredNotify = DateTime.MinValue;
        DateTime lastCodexCredNotify = DateTime.MinValue;
        DateTime lastFixtureNotify = DateTime.MinValue;
        ServiceState claude = new ServiceState("Claude", ClaudeUrl, Color.FromArgb(45, 132, 235));
        ServiceState codex = new ServiceState("Codex", CodexUrl, Color.FromArgb(26, 177, 92));

        static UsageForm()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        string hoverKey = "";
        bool sideRailVisible;
        double sideRailOpacity;
        int spinnerFrame;
        int paintSubtick;

        bool English
        {
            get { return string.Equals(settings.Language, "en", StringComparison.OrdinalIgnoreCase); }
        }

        static readonly Dictionary<string, bool> cliAvailabilityCache = new Dictionary<string, bool>();
        static readonly object cliCacheLock = new object();

        public UsageForm()
        {
            Text = "Headroom";
            Width = settings.Width;
            Height = settings.Height;
            ApplyLayoutMinimumSize();
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Color.Black;
            TransparencyKey = Color.Black;
            TopMost = settings.AlwaysOnTop;
            DoubleBuffered = true;
            KeyPreview = true;
            ShowInTaskbar = true;

            claude.ManuallyLoggedOut = settings.ClaudeLoggedOut;
            codex.ManuallyLoggedOut = settings.CodexLoggedOut;
            if (claude.ManuallyLoggedOut) MarkLoggedOut(claude);
            if (codex.ManuallyLoggedOut) MarkLoggedOut(codex);

            if (HeadroomOptions.FixtureMode) SetupFixtureWatcher();
            else SetupCredentialWatchers();

            MouseDown += OnMouseDown;
            MouseMove += OnMouseMove;
            MouseUp += (s, e) =>
            {
                if (pendingSilentKey.Length > 0)
                {
                    if (!silentDragging)
                    {
                        string capturedKey = pendingSilentKey;
                        BeginInvoke(new Action(async () => await HandleClickAsync(capturedKey)));
                    }
                    pendingSilentKey = "";
                    silentDragging = false;
                }
            };
            MouseLeave += (s, e) => { hoverKey = ""; sideRailVisible = false; Invalidate(); };
            Resize += (s, e) =>
            {
                settings.Width = Width;
                settings.Height = Height;
                settings.Save();
                Invalidate();
            };
            KeyDown += async (s, e) =>
            {
                if (e.KeyCode == Keys.F5) await RefreshAllAsync(true);
                if (e.KeyCode == Keys.S || e.KeyCode == Keys.F2) await ShowSettingsDialog();
            };

            paintTimer.Interval = 40;
            paintTimer.Tick += (s, e) =>
            {
                UpdateSideRailVisibilityFromCursor();
                UpdateSideRailOpacity();
                paintSubtick = (paintSubtick + 1) % 6;
                if (paintSubtick == 0) spinnerFrame = (spinnerFrame + 1) % 4;
                UpdateBarAnimation(codex,  settings.CodexShowUsed);
                UpdateBarAnimation(claude, settings.ClaudeShowUsed);
                Invalidate();
            };
            paintTimer.Start();

            tooltipTimer.Interval = 2000;
            tooltipTimer.Tick += (s, e) =>
            {
                tooltipTimer.Stop();
                if (!string.IsNullOrEmpty(pendingTooltipText))
                    toolTip.Show(pendingTooltipText, this, pendingTooltipLocation.X, pendingTooltipLocation.Y, 3000);
            };

            schedulerTimer.Interval = 10000;
            schedulerTimer.Tick += async (s, e) => await RunScheduledRefreshAsync();
            schedulerTimer.Start();

            Shown += async (s, e) =>
            {
                await RefreshAllAsync(true);
            };

            FormClosed += (s, e) =>
            {
                try { if (claudeCredWatcher != null) claudeCredWatcher.Dispose(); } catch { }
                try { if (codexCredWatcher  != null) codexCredWatcher.Dispose();  } catch { }
            };
        }

        async Task RunScheduledRefreshAsync()
        {
            bool anyVeryNear  = (settings.ShowClaude && IsVeryNearReset(claude.Data))    || (settings.ShowCodex && IsVeryNearReset(codex.Data));
            bool anyNearReset = (settings.ShowClaude && IsNearOrRecentReset(claude.Data)) || (settings.ShowCodex && IsNearOrRecentReset(codex.Data));
            schedulerTimer.Interval = anyVeryNear ? 5000 : (anyNearReset ? 5000 : 10000);

            if (settings.ShowCodex)  await MaybeRefreshAsync(codex);
            if (settings.ShowClaude) await MaybeRefreshAsync(claude);
        }

        async Task MaybeRefreshAsync(ServiceState service)
        {
            if (service.ManuallyLoggedOut) return;
            if (service.IsRefreshing) return;
            if (service.RateLimitedUntil.HasValue)
            {
                if (service.RateLimitedUntil.Value > DateTime.Now)
                {
                    service.Status = "rate_limited";
                    return;
                }
                service.RateLimitedUntil = null;
            }
            if (service.BoostUntil.HasValue && service.BoostUntil.Value <= DateTime.Now)
                service.BoostUntil = null;

            TimeSpan due;
            if (IsVeryNearReset(service.Data))
                due = TimeSpan.FromSeconds(5);
            else if (IsNearOrRecentReset(service.Data))
                due = TimeSpan.FromSeconds(Math.Max(15, settings.NearResetIntervalSeconds));
            else
                due = TimeSpan.FromMinutes(Math.Max(1, RefreshIntervalMinutes(service)));

            if (service.LastRefresh == DateTime.MinValue || DateTime.Now - service.LastRefresh >= due)
                await RefreshServiceAsync(service, false);
        }

        static bool IsNearOrRecentReset(UsageData data)
        {
            bool fiveEmpty = data.FiveHourRemainingPercent().HasValue && data.FiveHourRemainingPercent().Value <= 0;
            bool weekEmpty = data.WeeklyRemainingPercent().HasValue && data.WeeklyRemainingPercent().Value <= 0;
            if (!fiveEmpty && !weekEmpty) return false;

            TimeSpan rem;
            if (fiveEmpty)
            {
                if (string.IsNullOrEmpty(data.FiveHourReset)) return true;
                if (TryGetResetRemaining(data.FiveHourReset, false, out rem) && Math.Abs(rem.TotalMinutes) < 10)
                    return true;
            }
            if (weekEmpty)
            {
                if (string.IsNullOrEmpty(data.WeeklyReset)) return true;
                if (TryGetResetRemaining(data.WeeklyReset, false, out rem) && Math.Abs(rem.TotalMinutes) < 10)
                    return true;
            }
            return false;
        }

        static bool IsVeryNearReset(UsageData data)
        {
            bool fiveEmpty = data.FiveHourRemainingPercent().HasValue && data.FiveHourRemainingPercent().Value <= 0;
            bool weekEmpty = data.WeeklyRemainingPercent().HasValue && data.WeeklyRemainingPercent().Value <= 0;
            if (!fiveEmpty && !weekEmpty) return false;
            TimeSpan rem;
            if (fiveEmpty && !string.IsNullOrEmpty(data.FiveHourReset)
                && TryGetResetRemaining(data.FiveHourReset, false, out rem) && Math.Abs(rem.TotalMinutes) < 1)
                return true;
            if (weekEmpty && !string.IsNullOrEmpty(data.WeeklyReset)
                && TryGetResetRemaining(data.WeeklyReset, false, out rem) && Math.Abs(rem.TotalMinutes) < 1)
                return true;
            return false;
        }

        int RefreshIntervalMinutes(ServiceState service)
        {
            if (service.BoostUntil.HasValue) return settings.BoostIntervalMinutes;
            return settings.NormalIntervalMinutes;
        }

        static void UpdateBarAnimation(ServiceState svc, bool showUsed)
        {
            const double EaseFactor = 0.18;
            const double SnapThreshold = 0.08;
            int? target5 = svc.Data.FiveHourDisplayPercent(showUsed);
            int? targetW = svc.Data.WeeklyDisplayPercent(showUsed);
            if (target5.HasValue)
            {
                if (!svc.DisplayedFivePct.HasValue) svc.DisplayedFivePct = 0;
                double cur = svc.DisplayedFivePct.Value;
                double diff = target5.Value - cur;
                svc.DisplayedFivePct = Math.Abs(diff) < SnapThreshold ? (double)target5.Value : cur + diff * EaseFactor;
            }
            else
            {
                svc.DisplayedFivePct = null;
            }
            if (targetW.HasValue)
            {
                if (!svc.DisplayedWeekPct.HasValue) svc.DisplayedWeekPct = 0;
                double cur = svc.DisplayedWeekPct.Value;
                double diff = targetW.Value - cur;
                svc.DisplayedWeekPct = Math.Abs(diff) < SnapThreshold ? (double)targetW.Value : cur + diff * EaseFactor;
            }
            else
            {
                svc.DisplayedWeekPct = null;
            }
        }

        async Task RefreshAllAsync(bool manual)
        {
            var tasks = new List<Task>();
            if (settings.ShowCodex)  tasks.Add(RefreshServiceAsync(codex, manual));
            if (settings.ShowClaude) tasks.Add(RefreshServiceAsync(claude, manual));
            await Task.WhenAll(tasks);
        }

        async Task RefreshServiceAsync(ServiceState service, bool manual)
        {
            if (service.RateLimitedUntil.HasValue && service.RateLimitedUntil.Value > DateTime.Now)
            {
                service.Status = "rate_limited";
                Invalidate();
                return;
            }
            service.RateLimitedUntil = null;
            if (service.Name == "Claude") await RefreshClaudeViaApiAsync(service, manual);
            else                          await RefreshCodexViaApiAsync(service, manual);
        }

        static void LoadFixture(ServiceState service, string name, string fileName, Func<string, UsageData> parser)
        {
            string path = Path.Combine(HeadroomOptions.FixtureDir, fileName);
            if (!File.Exists(path))
            {
                service.Data = new UsageData
                {
                    Name = name,
                    Source = "Fixture",
                    UpdatedAt = DateTime.Now,
                    Status = "fixture_missing"
                };
                service.Status = "fixture_missing";
                service.LastRefresh = DateTime.Now;
                return;
            }

            string json = File.ReadAllText(path);
            if (Regex.IsMatch(json, "\"status\"\\s*:\\s*\"login_required\"", RegexOptions.IgnoreCase))
            {
                service.Data = new UsageData
                {
                    Name = name,
                    Source = "Fixture",
                    UpdatedAt = DateTime.Now,
                    Status = "login_required"
                };
            }
            else
            {
                service.Data = parser(json);
                service.Data.Name = name;
                service.Data.Source = "Fixture";
            }
            service.Status = service.Data.Status;
            service.RateLimitedUntil = null;
            service.LastRefresh = DateTime.Now;
        }

        static void ApplyRateLimit(ServiceState service, HttpResponseMessage resp, string debugName, int code)
        {
            DateTime until = RateLimitUntil(resp);
            service.RateLimitedUntil = until;
            service.Status = "rate_limited";
            service.LastRefresh = DateTime.Now;

            string retryAfter = resp.Headers.RetryAfter == null ? "" : resp.Headers.RetryAfter.ToString();
            WriteDebug(debugName,
                "HTTP " + code + "\r\n" +
                "Retry-After: " + retryAfter + "\r\n" +
                "Backoff until: " + until.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        }

        static DateTime RateLimitUntil(HttpResponseMessage resp)
        {
            var retryAfter = resp.Headers.RetryAfter;
            if (retryAfter != null)
            {
                if (retryAfter.Delta.HasValue)
                {
                    double seconds = Math.Max(60, Math.Min(7200, retryAfter.Delta.Value.TotalSeconds));
                    return DateTime.Now.AddSeconds(seconds);
                }
                if (retryAfter.Date.HasValue)
                {
                    DateTime target = retryAfter.Date.Value.LocalDateTime;
                    if (target > DateTime.Now)
                        return target;
                }
            }
            return DateTime.Now.AddMinutes(DefaultRateLimitBackoffMinutes);
        }

        static string TryReadFileWithRetry(string path)
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

        static bool IsCliAvailable(string exeName)
        {
            lock (cliCacheLock)
            {
                bool cached;
                if (cliAvailabilityCache.TryGetValue(exeName, out cached)) return cached;
            }
            bool result = false;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c where " + exeName)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    if (p.WaitForExit(2000)) result = p.ExitCode == 0;
                    else { try { p.Kill(); } catch { } }
                }
            }
            catch { result = false; }
            lock (cliCacheLock) { cliAvailabilityCache[exeName] = result; }
            return result;
        }

        static void InvalidateCliCache(string exeName)
        {
            lock (cliCacheLock) { cliAvailabilityCache.Remove(exeName); }
        }

        static string GeneratePkceVerifier()
        {
            var bytes = new byte[32];
            using (var rng = new System.Security.Cryptography.RNGCryptoServiceProvider())
                rng.GetBytes(bytes);
            return Base64UrlEncode(bytes);
        }

        static string GeneratePkceChallenge(string verifier)
        {
            using (var sha = new System.Security.Cryptography.SHA256Managed())
            {
                byte[] hash = sha.ComputeHash(System.Text.Encoding.ASCII.GetBytes(verifier));
                return Base64UrlEncode(hash);
            }
        }

        static string Base64UrlEncode(byte[] data)
        {
            return Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        static int GetFreeLocalPort()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        static bool TryStartOAuthListener(int port, out HttpListener listener, out Exception error)
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

        static async Task<string> WaitForOAuthCallbackAsync(HttpListener listener, string expectedState, int timeoutMs)
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
                var cm = Regex.Match(query, "[?&]code=([^&]+)");
                var sm = Regex.Match(query, "[?&]state=([^&]+)");
                var em = Regex.Match(query, "[?&]error=([^&]+)");
                if (em.Success) error = Uri.UnescapeDataString(em.Groups[1].Value);
                if (!sm.Success) error = error ?? "state_missing";
                else if (Uri.UnescapeDataString(sm.Groups[1].Value) != expectedState) error = error ?? "state_mismatch";
                if (cm.Success && error == null) code = Uri.UnescapeDataString(cm.Groups[1].Value);

                string body = error == null
                    ? "<!doctype html><html lang='ja'><head><meta charset='utf-8'><title>Headroom</title></head><body style='font-family:Segoe UI,sans-serif;text-align:center;padding-top:80px;color:#222;'><h2>Headroom ログイン完了</h2><p>このタブを閉じてください。</p></body></html>"
                    : "<!doctype html><html lang='ja'><head><meta charset='utf-8'><title>Headroom</title></head><body style='font-family:Segoe UI,sans-serif;text-align:center;padding-top:80px;color:#b00;'><h2>Headroom ログイン失敗</h2><p>" + System.Net.WebUtility.HtmlEncode(error) + "</p></body></html>";
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

        static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new System.Text.StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        static string ExtractJsonString(string json, string key)
        {
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        static long ExtractJsonLong(string json, string key)
        {
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?\\d+)");
            long v;
            if (m.Success && long.TryParse(m.Groups[1].Value, out v)) return v;
            return 0;
        }

        async Task OpenLoginAsync(ServiceState service)
        {
            service.ManuallyLoggedOut = false;
            SetLoggedOutSetting(service, false);
            string cliName = service.Name == "Claude" ? "claude" : "codex";
            string loginMethod = service.Name == "Claude" ? settings.ClaudeLoginMethod : settings.CodexLoginMethod;

            bool useCli = string.Equals(loginMethod, "cli", StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(loginMethod, "auto", StringComparison.OrdinalIgnoreCase) && IsCliAvailable(cliName));
            bool useBrowser = string.Equals(loginMethod, "browser", StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(loginMethod, "auto", StringComparison.OrdinalIgnoreCase) && !IsCliAvailable(cliName));

            if (useCli)
            {
                if (!IsCliAvailable(cliName))
                {
                    service.Status = "login_required";
                    Invalidate();
                    MessageBox.Show(
                        T("CLI が見つかりません。設定でログイン方法をブラウザOAuthに変更してください。",
                          "CLI was not found. Change the login method to Browser OAuth in Settings."),
                        "Headroom", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                string title  = T("Headroom: 認証後このウィンドウを閉じてOK",
                                  "Headroom: close this window after sign-in");
                string banner = service.Name == "Claude"
                    ? T("[Headroom] /login と入力して認証してください。完了したらこのウィンドウは閉じてOKです。",
                        "[Headroom] Type /login below to sign in. You can close this window once login completes.")
                    : T("[Headroom] ブラウザで認証してください。完了したらこのウィンドウは閉じてOKです。",
                        "[Headroom] A browser will open for sign-in. You can close this window once login completes.");
                string cliExec = service.Name == "Claude" ? "claude" : "codex login";
                string cliCommand = "chcp 65001 >nul && title " + title + " && echo. && echo " + banner + " && echo. && " + cliExec;
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/k " + cliCommand)
                    {
                        UseShellExecute = true,
                        WindowStyle = System.Diagnostics.ProcessWindowStyle.Normal,
                    };
                    System.Diagnostics.Process.Start(psi);
                    service.Status = "login_pending";
                    Invalidate();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(T("CLI を起動できませんでした: ", "Failed to launch CLI: ") + ex.Message,
                        "Headroom", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return;
            }

            if (!useBrowser)
                useBrowser = true;

            service.Status = "login_pending";
            Invalidate();

            bool ok = false;
            try
            {
                if (service.Name == "Claude")
                    ok = await StartClaudePkceLoginAsync(service);
                else
                    ok = await StartCodexPkceLoginAsync(service);
            }
            catch (Exception ex)
            {
                WriteDebug(cliName + "-pkce-error.txt", ex.ToString());
            }

            if (ok)
            {
                SetupCredentialWatchers();
                service.ManuallyLoggedOut = false;
                service.RateLimitedUntil = null;
                await RefreshServiceAsync(service, true);
            }
            else if (service.Status == "login_pending")
            {
                service.Status = "login_required";
                Invalidate();
            }
        }

        Task LogoutServiceAsync(ServiceState service)
        {
            MarkLoggedOut(service);
            service.ManuallyLoggedOut = true;
            SetLoggedOutSetting(service, true);
            Invalidate();
            return Task.CompletedTask;
        }

        void MarkLoggedOut(ServiceState service)
        {
            service.Data = new UsageData { Name = service.Name, Source = service.Name + " API", UpdatedAt = DateTime.Now, Status = "login_required" };
            service.Status = "login_required";
            service.LastRefresh = DateTime.MinValue;
            service.RateLimitedUntil = null;
            service.DisplayedFivePct = null;
            service.DisplayedWeekPct = null;
        }

        void SetLoggedOutSetting(ServiceState service, bool value)
        {
            if (service.Name == "Claude") settings.ClaudeLoggedOut = value;
            else settings.CodexLoggedOut = value;
            settings.Save();
        }

        void SetupCredentialWatchers()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            try
            {
                string claudeDir = Path.Combine(userProfile, ".claude");
                Directory.CreateDirectory(claudeDir);
                if (claudeCredWatcher != null)
                {
                    try { claudeCredWatcher.Dispose(); } catch { }
                    claudeCredWatcher = null;
                }
                claudeCredWatcher = new FileSystemWatcher(claudeDir, ".credentials.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                };
                claudeCredWatcher.Changed += (s, e) => OnClaudeCredentialChanged();
                claudeCredWatcher.Created += (s, e) => OnClaudeCredentialChanged();
            }
            catch (Exception ex)
            {
                WriteDebug("watcher-claude-error.txt", ex.ToString());
            }
            try
            {
                string codexDir = Path.Combine(userProfile, ".codex");
                Directory.CreateDirectory(codexDir);
                if (codexCredWatcher != null)
                {
                    try { codexCredWatcher.Dispose(); } catch { }
                    codexCredWatcher = null;
                }
                codexCredWatcher = new FileSystemWatcher(codexDir, "auth.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                };
                codexCredWatcher.Changed += (s, e) => OnCodexCredentialChanged();
                codexCredWatcher.Created += (s, e) => OnCodexCredentialChanged();
            }
            catch (Exception ex)
            {
                WriteDebug("watcher-codex-error.txt", ex.ToString());
            }
        }

        void SetupFixtureWatcher()
        {
            try
            {
                Directory.CreateDirectory(HeadroomOptions.FixtureDir);
                fixtureWatcher = new FileSystemWatcher(HeadroomOptions.FixtureDir, "*.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };
                fixtureWatcher.Changed += (s, e) => OnFixtureChanged();
                fixtureWatcher.Created += (s, e) => OnFixtureChanged();
                fixtureWatcher.Deleted += (s, e) => OnFixtureChanged();
                fixtureWatcher.Renamed += (s, e) => OnFixtureChanged();
            }
            catch (Exception ex)
            {
                WriteDebug("watcher-fixture-error.txt", ex.ToString());
            }
        }

        void OnClaudeCredentialChanged()
        {
            if ((DateTime.Now - lastClaudeCredNotify).TotalSeconds < 1) return;
            lastClaudeCredNotify = DateTime.Now;
            try
            {
                BeginInvoke(new Action(async () =>
                {
                    claude.ManuallyLoggedOut = false;
                    claude.RateLimitedUntil = null;
                    settings.ClaudeLoggedOut = false;
                    settings.Save();
                    await RefreshServiceAsync(claude, true);
                }));
            }
            catch { }
        }

        void OnCodexCredentialChanged()
        {
            if ((DateTime.Now - lastCodexCredNotify).TotalSeconds < 1) return;
            lastCodexCredNotify = DateTime.Now;
            try
            {
                BeginInvoke(new Action(async () =>
                {
                    codex.ManuallyLoggedOut = false;
                    codex.RateLimitedUntil = null;
                    settings.CodexLoggedOut = false;
                    settings.Save();
                    await RefreshServiceAsync(codex, true);
                }));
            }
            catch { }
        }

        void OnFixtureChanged()
        {
            if ((DateTime.Now - lastFixtureNotify).TotalMilliseconds < 500) return;
            lastFixtureNotify = DateTime.Now;
            try
            {
                BeginInvoke(new Action(async () => await RefreshAllAsync(true)));
            }
            catch { }
        }

        string T(string ja, string en)
        {
            return English ? en : ja;
        }

        internal static void WriteDebug(string name, string text)
        {
            string dir = DebugDirectory;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name), text ?? "");
        }

        internal static string DebugDirectory
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".headroom"); }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                Icon rawIco = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                Icon = rawIco;
                Icon icoSmall = new Icon(rawIco, 16, 16);
                Icon icoBig   = new Icon(rawIco, 48, 48);
                SendMessageIcon(Handle, 0x80, new IntPtr(0), icoSmall.Handle);
                SendMessageIcon(Handle, 0x80, new IntPtr(1), icoBig.Handle);
            }
            catch { }
        }
    }
}
