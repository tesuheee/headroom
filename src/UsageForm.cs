using System;
using System.Collections.Generic;
using System.Drawing;
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
            DateTime now = DateTime.Now;
            schedulerTimer.Interval = RefreshPolicy.SchedulerIntervalMs(settings, claude, codex, now);

            if (settings.ShowCodex)  await MaybeRefreshAsync(codex);
            if (settings.ShowClaude) await MaybeRefreshAsync(claude);
        }

        async Task MaybeRefreshAsync(ServiceState service)
        {
            var decision = RefreshPolicy.Evaluate(service, settings, DateTime.Now);
            if (decision.RateLimited)
            {
                service.Status = "rate_limited";
                return;
            }
            if (decision.RateLimitExpired)
                service.RateLimitedUntil = null;
            if (decision.BoostExpired)
                service.BoostUntil = null;

            if (decision.ShouldRefresh)
                await RefreshServiceAsync(service, false);
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

        void ApplyFetchResult(ServiceState service, UsageFetchResult result)
        {
            if (!string.IsNullOrEmpty(result.DebugName))
                WriteDebug(result.DebugName, result.DebugText);
            if (result.Data != null)
                service.Data = result.Data;
            service.Status = result.Status ?? (service.Data == null ? "fetch_error" : service.Data.Status);
            if (result.RateLimitedUntil.HasValue)
            {
                service.RateLimitedUntil = result.RateLimitedUntil;
                service.Status = "rate_limited";
            }
            else if (service.Status != "fetch_error")
            {
                service.RateLimitedUntil = null;
            }
            service.LastRefresh = DateTime.Now;
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
            DebugLog.Write(name, text);
        }

        internal static string DebugDirectory
        {
            get { return DebugLog.DirectoryPath; }
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
