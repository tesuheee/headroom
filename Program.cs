using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Headroom
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new UsageForm());
        }
    }

    sealed class UsageForm : Form
    {
        const string ClaudeUrl = "https://claude.ai/settings/usage";
        const string CodexUrl = "https://chatgpt.com/codex/cloud/settings/analytics#usage";

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
        [DllImport("user32.dll", EntryPoint = "SendMessage")]
        static extern IntPtr SendMessageIcon(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        const int WmNcLButtonDown = 0xA1;
        const int HtCaption = 0x2;

        readonly Timer paintTimer = new Timer();
        readonly Timer schedulerTimer = new Timer();
        readonly WebView2 claudeView = new WebView2();
        readonly WebView2 codexView = new WebView2();
        readonly Dictionary<string, Rectangle> hits = new Dictionary<string, Rectangle>();
        readonly WidgetSettings settings = WidgetSettings.Load();
        readonly ToolTip toolTip = new ToolTip { InitialDelay = 2000, ReshowDelay = 100, ShowAlways = true };
        readonly Timer tooltipTimer = new Timer();
        string pendingTooltipText = "";
        Point pendingTooltipLocation;

        CoreWebView2Environment webEnv;
        ServiceState claude = new ServiceState("Claude", ClaudeUrl, Color.FromArgb(45, 132, 235));
        ServiceState codex = new ServiceState("Codex", CodexUrl, Color.FromArgb(26, 177, 92));
        bool resizing;
        Point resizeStartPoint;
        Size resizeStartSize;
        string hoverKey = "";
        int spinnerFrame;
        int paintSubtick;
        const float LabelFontSize   = 10.8f;
        const float PercentFontSize = 16.5f;
        const float ResetFontSize   =  9.9f;
        bool English
        {
            get { return string.Equals(settings.Language, "en", StringComparison.OrdinalIgnoreCase); }
        }

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

            foreach (var view in new[] { claudeView, codexView })
            {
                view.Size = new Size(1, 1);
                view.Location = new Point(-20, -20);
                view.Visible = false;
                Controls.Add(view);
            }

            MouseDown += OnMouseDown;
            MouseMove += OnMouseMove;
            MouseUp += (s, e) => resizing = false;
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
                if (e.KeyCode == Keys.S || e.KeyCode == Keys.F2) ShowSettingsDialog();
            };

            paintTimer.Interval = 40;
            paintTimer.Tick += (s, e) =>
            {
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
                await InitializeWebViewsAsync();
                await RefreshAllAsync(true);
            };
        }

        async Task InitializeWebViewsAsync()
        {
            if (webEnv != null) return;
            string userData = WebViewUserDataPath();
            Directory.CreateDirectory(userData);
            webEnv = await CoreWebView2Environment.CreateAsync(null, userData);
            await claudeView.EnsureCoreWebView2Async(webEnv);
            await codexView.EnsureCoreWebView2Async(webEnv);
        }

        static string WebViewUserDataPath()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string current = Path.Combine(local, "Headroom", "WebView2Profile");
            string legacy = Path.Combine(local, "AiUsageWebView2", "WebView2Profile");
            if (!Directory.Exists(current) && Directory.Exists(legacy))
            {
                try { CopyDirectory(legacy, current); }
                catch { }
            }
            return current;
        }

        static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(destination, dir.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = file.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, false);
            }
        }

        async Task RunScheduledRefreshAsync()
        {
            if (settings.ShowCodex) await MaybeRefreshAsync(codex, codexView);
            if (settings.ShowClaude) await MaybeRefreshAsync(claude, claudeView);
        }

        async Task MaybeRefreshAsync(ServiceState service, WebView2 view)
        {
            if (service.IsRefreshing) return;
            if (service.BoostUntil.HasValue && service.BoostUntil.Value <= DateTime.Now)
                service.BoostUntil = null;

            int minutes = RefreshIntervalMinutes(service);
            if (IsNearOrRecentReset(service.Data))
                minutes = Math.Min(minutes, 1);

            if (service.LastRefresh == DateTime.MinValue || DateTime.Now - service.LastRefresh >= TimeSpan.FromMinutes(Math.Max(1, minutes)))
                await RefreshServiceAsync(service, view, false);
        }

        static bool IsNearOrRecentReset(UsageData data)
        {
            bool fiveEmpty = data.FiveHourRemainingPercent().HasValue && data.FiveHourRemainingPercent().Value <= 0;
            bool weekEmpty = data.WeeklyRemainingPercent().HasValue && data.WeeklyRemainingPercent().Value <= 0;
            if (!fiveEmpty && !weekEmpty) return false;

            TimeSpan rem;
            if (fiveEmpty && !string.IsNullOrEmpty(data.FiveHourReset) &&
                TryGetResetRemaining(data.FiveHourReset, false, out rem) && Math.Abs(rem.TotalMinutes) < 10)
                return true;
            if (weekEmpty && !string.IsNullOrEmpty(data.WeeklyReset) &&
                TryGetResetRemaining(data.WeeklyReset, false, out rem) && Math.Abs(rem.TotalMinutes) < 10)
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
            if (settings.ShowCodex) tasks.Add(RefreshServiceAsync(codex, codexView, manual));
            if (settings.ShowClaude) tasks.Add(RefreshServiceAsync(claude, claudeView, manual));
            await Task.WhenAll(tasks);
        }

        async Task RefreshServiceAsync(ServiceState service, WebView2 view, bool manual)
        {
            if (service.IsRefreshing) return;
            service.IsRefreshing = true;
            service.Status = manual ? "updating" : service.Status;
            Invalidate();
            try
            {
                await InitializeWebViewsAsync();
                string text = await ScrapeAsync(view, service.Url);
                service.Data = service.Name == "Claude" ? ParseClaude(text) : ParseCodex(text);
                service.Status = service.Data.Status;
                service.LastRefresh = DateTime.Now;
                WriteDebug(service.Name.ToLowerInvariant() + "-webview2.txt", text);
            }
            catch (Exception ex)
            {
                service.Status = "fetch_error";
                WriteDebug(service.Name.ToLowerInvariant() + "-error.txt", ex.ToString());
            }
            finally
            {
                service.IsRefreshing = false;
                Invalidate();
            }
        }

        async Task OpenLoginAsync(ServiceState service)
        {
            await InitializeWebViewsAsync();
            var login = new Form
            {
                Text = service.Name + " Login",
                Width = 980,
                Height = 760,
                StartPosition = FormStartPosition.CenterScreen,
                TopMost = false
            };
            var view = new WebView2 { Dock = DockStyle.Fill };
            login.Controls.Add(view);
            login.Shown += async (s, e) =>
            {
                await view.EnsureCoreWebView2Async(webEnv);
                view.CoreWebView2.NavigationCompleted += (s2, e2) =>
                {
                    var uri = view.CoreWebView2.Source;
                    if (uri != null && (uri.Contains("/settings/usage") || uri.Contains("/settings/analytics")))
                    {
                        login.BeginInvoke(new Action(() =>
                        {
                            if (!login.IsDisposed) login.Close();
                        }));
                    }
                };
                view.CoreWebView2.Navigate(service.Url);
            };
            TopMost = false;
            login.FormClosed += async (s, e) =>
            {
                TopMost = settings.AlwaysOnTop;
                await RefreshServiceAsync(service, service.Name == "Claude" ? claudeView : codexView, true);
            };
            login.Show(this);
        }

        async Task<string> ScrapeAsync(WebView2 view, string url)
        {
            var tcs = new TaskCompletionSource<bool>();
            EventHandler<CoreWebView2NavigationCompletedEventArgs> handler = null;
            handler = (s, e) =>
            {
                view.CoreWebView2.NavigationCompleted -= handler;
                tcs.TrySetResult(true);
            };
            view.CoreWebView2.NavigationCompleted += handler;
            view.CoreWebView2.Navigate(WithCacheBuster(url));
            await Task.WhenAny(tcs.Task, Task.Delay(30000));

            string previous = "";
            string current = "";
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(1200);
                current = await GetBodyTextAsync(view);
                if (current.Length > 100 && current == previous) break;
                previous = current;
            }
            return current;
        }

        static string WithCacheBuster(string url)
        {
            string hash = "";
            int hashIndex = url.IndexOf('#');
            if (hashIndex >= 0)
            {
                hash = url.Substring(hashIndex);
                url = url.Substring(0, hashIndex);
            }
            string sep = url.Contains("?") ? "&" : "?";
            return url + sep + "_ai_usage_refresh=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + hash;
        }

        async Task<string> GetBodyTextAsync(WebView2 view)
        {
            string json = await view.CoreWebView2.ExecuteScriptAsync("document.body ? document.body.innerText : ''");
            return DecodeJsonString(json);
        }

        static string DecodeJsonString(string json)
        {
            if (string.IsNullOrEmpty(json) || json == "null") return "";
            var m = Regex.Match(json, "^\"(.*)\"$", RegexOptions.Singleline);
            if (!m.Success) return json;
            return Regex.Unescape(m.Groups[1].Value);
        }

        static UsageData ParseClaude(string text)
        {
            var data = new UsageData { Name = "Claude", Source = "Claude Web", UpdatedAt = DateTime.Now };
            if (string.IsNullOrWhiteSpace(text))
            {
                data.Status = "no_data";
                return data;
            }
            if (Regex.IsMatch(text, "ログイン|サインイン|Claude を試す|Claudeを体験する") &&
                !Regex.IsMatch(text, "プラン使用制限|現在のセッション|週間制限"))
            {
                data.Status = "login_required";
                return data;
            }

            var lines = SplitLines(text);
            int sessionStart = FindIndex(lines, "現在のセッション|Current session");
            int weeklyStart = FindIndex(lines, "週間制限|すべてのモデル|Weekly limit|All models");
            int designStart = FindIndex(lines, "Claude Design");

            if (sessionStart >= 0)
            {
                int end = FirstPositive(lines.Count, weeklyStart, designStart);
                data.FiveHourUsed = FindUsedPercent(lines, sessionStart, end);
                data.FiveHourReset = FindResetInRange(lines, sessionStart, end);
                data.FiveHourNotStarted = RangeHasNotStartedText(lines, sessionStart, end);
            }
            if (weeklyStart >= 0)
            {
                int end = FirstPositive(lines.Count, designStart);
                data.WeeklyUsed = FindUsedPercent(lines, weeklyStart, end);
                data.WeeklyReset = FindResetInRange(lines, weeklyStart, end);
                data.WeeklyNotStarted = RangeHasNotStartedText(lines, weeklyStart, end);
            }
            ApplyNotStartedDefaults(data);
            data.HitLimit = DetectLimitHit(text);
            if (!data.HasAnyValue()) data.Status = "no_usage_text";
            return data;
        }

        static UsageData ParseCodex(string text)
        {
            var data = new UsageData { Name = "Codex", Source = "Codex Web", UpdatedAt = DateTime.Now };
            if (string.IsNullOrWhiteSpace(text))
            {
                data.Status = "no_data";
                return data;
            }
            if (Regex.IsMatch(text, "ログイン|サインイン|Log in|Sign in", RegexOptions.IgnoreCase) &&
                !Regex.IsMatch(text, "残高|使用制限|Codex|usage|limit", RegexOptions.IgnoreCase))
            {
                data.Status = "login_required";
                return data;
            }

            var lines = SplitLines(text);
            int fiveStart = FindIndex(lines, "5時間の使用制限|5 hour");
            int weeklyStart = FindIndex(lines, "週あたりの使用制限|weekly");
            int creditStart = FindIndex(lines, "残りのクレジット|credits");

            if (fiveStart >= 0)
            {
                int end = FirstPositive(lines.Count, weeklyStart, creditStart);
                data.FiveHourRemaining = FindRemainingPercent(lines, fiveStart, end);
                data.FiveHourReset = FindResetInRange(lines, fiveStart, end);
                data.FiveHourNotStarted = RangeHasNotStartedText(lines, fiveStart, end);
            }
            if (weeklyStart >= 0)
            {
                int end = FirstPositive(lines.Count, creditStart);
                data.WeeklyRemaining = FindRemainingPercent(lines, weeklyStart, end);
                data.WeeklyReset = FindResetInRange(lines, weeklyStart, end);
                data.WeeklyNotStarted = RangeHasNotStartedText(lines, weeklyStart, end);
            }
            ApplyNotStartedDefaults(data);
            data.HitLimit = DetectLimitHit(text);
            if (!data.HasAnyValue()) data.Status = "no_usage_text";
            return data;
        }

        static bool DetectLimitHit(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return Regex.IsMatch(text,
                @"上限に達しました|制限に達しました|You'?ve reached.{0,20}limit|limit reached|at capacity",
                RegexOptions.IgnoreCase);
        }

        static List<string> SplitLines(string text)
        {
            return text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        }

        static int FindIndex(List<string> lines, string pattern)
        {
            for (int i = 0; i < lines.Count; i++)
                if (Regex.IsMatch(lines[i], pattern, RegexOptions.IgnoreCase))
                    return i;
            return -1;
        }

        static int FirstPositive(int fallback, params int[] values)
        {
            return values.Where(x => x >= 0).DefaultIfEmpty(fallback).Min();
        }

        static int? FindUsedPercent(List<string> lines, int start, int end)
        {
            for (int i = start; i < Math.Min(end, lines.Count); i++)
            {
                var m = Regex.Match(lines[i], @"(\d+)\s*%\s*使用済み");
                if (m.Success) return int.Parse(m.Groups[1].Value);
            }
            return null;
        }

        static int? FindRemainingPercent(List<string> lines, int start, int end)
        {
            for (int i = start; i < Math.Min(end, lines.Count); i++)
            {
                var m = Regex.Match(lines[i], @"(\d+)\s*%\s*(残り|remaining)?", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                bool hasRemainingWord = m.Groups[2].Success ||
                    lines.Skip(i + 1).Take(Math.Min(2, end - i - 1)).Any(x => Regex.IsMatch(x, "^(残り|remaining)$", RegexOptions.IgnoreCase));
                if (hasRemainingWord) return int.Parse(m.Groups[1].Value);
            }
            return null;
        }

        static string FindResetInRange(List<string> lines, int start, int end)
        {
            for (int i = start; i < Math.Min(end, lines.Count); i++)
                if (Regex.IsMatch(lines[i], "リセット|reset", RegexOptions.IgnoreCase))
                    return lines[i];
            return null;
        }

        static bool RangeHasNotStartedText(List<string> lines, int start, int end)
        {
            for (int i = start; i < Math.Min(end, lines.Count); i++)
                if (Regex.IsMatch(lines[i], "使い始めたら|使用を開始|開始されます|まだ.*利用|start(?:s|ed)? when|start using|not started", RegexOptions.IgnoreCase))
                    return true;
            return false;
        }

        static void ApplyNotStartedDefaults(UsageData data)
        {
            if (data.FiveHourNotStarted)
            {
                if (!data.FiveHourUsed.HasValue) data.FiveHourUsed = 0;
                if (!data.FiveHourRemaining.HasValue || data.FiveHourRemaining.Value < 99) data.FiveHourRemaining = 100;
                data.FiveHourReset = null;
            }
            if (data.WeeklyNotStarted)
            {
                if (!data.WeeklyUsed.HasValue) data.WeeklyUsed = 0;
                if (!data.WeeklyRemaining.HasValue) data.WeeklyRemaining = 100;
                data.WeeklyReset = null;
            }
        }

        void OnMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            string key = HitKey(e.Location);
            if (key.Length > 0)
            {
                BeginInvoke(new Action(async () => await HandleClickAsync(key)));
                return;
            }

            if (IsResizeGrip(e.Location))
            {
                resizing = true;
                resizeStartPoint = e.Location;
                resizeStartSize = Size;
                return;
            }

            ReleaseCapture();
            SendMessage(Handle, WmNcLButtonDown, HtCaption, 0);
        }

        async Task HandleClickAsync(string key)
        {
            if (key == "pin")
            {
                settings.AlwaysOnTop = !settings.AlwaysOnTop;
                TopMost = settings.AlwaysOnTop;
                settings.Save();
                Invalidate();
                return;
            }
            if (key == "token")
            {
                bool next = !settings.ClaudeShowUsed;
                settings.ClaudeShowUsed = next;
                settings.CodexShowUsed  = next;
                settings.Save();
                Invalidate();
                return;
            }
            if (key == "fiveReset")
            {
                settings.FiveHourResetMode = string.Equals(settings.FiveHourResetMode, "relative", StringComparison.OrdinalIgnoreCase) ? "time" : "relative";
                settings.Save();
                Invalidate();
                return;
            }
            if (key == "weekReset")
            {
                settings.WeeklyResetMode = string.Equals(settings.WeeklyResetMode, "relative", StringComparison.OrdinalIgnoreCase) ? "time" : "relative";
                settings.Save();
                Invalidate();
                return;
            }
            if (key == "settings")
            {
                ShowSettingsDialog();
                return;
            }
            if (key == "close")
            {
                Close();
                return;
            }

            ServiceState service = key.StartsWith("codex") ? codex : claude;
            WebView2 view = service == codex ? codexView : claudeView;
            if (key.EndsWith("refresh"))
                await RefreshServiceAsync(service, view, true);
            else if (key.EndsWith("boost"))
                ToggleBoost(service);
            else if (key.EndsWith("login"))
                await OpenLoginAsync(service);
        }

        string TooltipText(string key)
        {
            if (key == "close") return T("閉じる", "Close");
            if (key == "pin") return T(settings.AlwaysOnTop ? "最前面を解除" : "最前面に固定", settings.AlwaysOnTop ? "Unpin from top" : "Always on top");
            if (key == "token") return T(settings.ClaudeShowUsed ? "残量表示に切り替え" : "使用量表示に切り替え", settings.ClaudeShowUsed ? "Switch to remaining" : "Switch to used");
            if (key == "fiveReset") return T(string.Equals(settings.FiveHourResetMode, "relative", StringComparison.OrdinalIgnoreCase) ? "5時間リセット: カウントダウン→時刻表示" : "5時間リセット: 時刻→カウントダウン表示", string.Equals(settings.FiveHourResetMode, "relative", StringComparison.OrdinalIgnoreCase) ? "5h reset: countdown → clock time" : "5h reset: clock time → countdown");
            if (key == "weekReset") return T(string.Equals(settings.WeeklyResetMode, "relative", StringComparison.OrdinalIgnoreCase) ? "週リセット: カウントダウン→時刻表示" : "週リセット: 時刻→カウントダウン表示", string.Equals(settings.WeeklyResetMode, "relative", StringComparison.OrdinalIgnoreCase) ? "Weekly reset: countdown → clock time" : "Weekly reset: clock time → countdown");
            if (key == "settings") return T("設定", "Settings");
            if (key.EndsWith("-refresh")) return T("更新", "Refresh");
            if (key.EndsWith("-boost"))
            {
                ServiceState svc = key.StartsWith("codex") ? codex : claude;
                return svc.BoostActive ? T("ブースト解除", "Stop boost") : T("ブースト（高頻度更新）", "Boost (frequent refresh)");
            }
            if (key.EndsWith("-login")) return T("ログイン", "Login");
            return "";
        }

        void ToggleBoost(ServiceState service)
        {
            if (service.BoostUntil.HasValue && service.BoostUntil.Value > DateTime.Now)
                service.BoostUntil = null;
            else
                service.BoostUntil = DateTime.Now.AddMinutes(Math.Max(1, settings.BoostDurationMinutes));
            Invalidate();
        }

        void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (resizing)
            {
                Width = Math.Max(MinimumSize.Width, resizeStartSize.Width + e.X - resizeStartPoint.X);
                Height = Math.Max(MinimumSize.Height, resizeStartSize.Height + e.Y - resizeStartPoint.Y);
                Invalidate();
                return;
            }

            string key = HitKey(e.Location);
            Cursor = key.Length > 0 ? Cursors.Hand : (IsResizeGrip(e.Location) ? Cursors.SizeNWSE : Cursors.Default);
            if (hoverKey != key)
            {
                hoverKey = key;
                toolTip.Hide(this);
                tooltipTimer.Stop();
                pendingTooltipText = "";
                string tip = TooltipText(key);
                if (tip.Length > 0)
                {
                    Rectangle hr;
                    if (hits.TryGetValue(key, out hr))
                        pendingTooltipLocation = new Point(hr.X + hr.Width / 2, hr.Y + hr.Height + 4);
                    else
                        pendingTooltipLocation = new Point(e.X + 12, e.Y + 18);
                    pendingTooltipText = tip;
                    tooltipTimer.Start();
                }
                Invalidate();
            }
        }

        string HitKey(Point point)
        {
            foreach (var hit in hits)
            {
                if (hit.Value.Contains(point)) return hit.Key;
            }
            return "";
        }

        bool IsResizeGrip(Point p)
        {
            return p.X >= ClientSize.Width - 18 && p.Y >= ClientSize.Height - 18;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.Black);
            hits.Clear();

            int y = 0;
            int gap = 10;
            int sideRail = 16;
            var visible = VisibleServices();
            int railLeft = ClientSize.Width - sideRail - 14;
            int contentW = Math.Max(240, railLeft);
            int contentH = Math.Max(110, ClientSize.Height);
            if (visible.Count == 1)
            {
                DrawService(g, visible[0].Item1, 0, y, contentW, contentH, visible[0].Item2);
            }
            else if (string.Equals(settings.LayoutMode, "vertical", StringComparison.OrdinalIgnoreCase))
            {
                int cardH = Math.Max(110, (contentH - gap) / 2);
                DrawService(g, visible[0].Item1, 0, y, contentW, cardH, visible[0].Item2);
                DrawService(g, visible[1].Item1, 0, y + cardH + gap, contentW, cardH, visible[1].Item2);
            }
            else
            {
                int cardW = Math.Max(240, (contentW - gap) / 2);
                DrawService(g, visible[0].Item1, 0, y, cardW, contentH, visible[0].Item2);
                DrawService(g, visible[1].Item1, cardW + gap, y, cardW, contentH, visible[1].Item2);
            }
            DrawSideRail(g);
        }

        List<Tuple<ServiceState, string>> VisibleServices()
        {
            var items = new List<Tuple<ServiceState, string>>();
            if (settings.ShowCodex) items.Add(Tuple.Create(codex, "codex"));
            if (settings.ShowClaude) items.Add(Tuple.Create(claude, "claude"));
            if (items.Count == 0) items.Add(Tuple.Create(claude, "claude"));
            return items;
        }

        void ApplyLayoutMinimumSize()
        {
            bool vertical = string.Equals(settings.LayoutMode, "vertical", StringComparison.OrdinalIgnoreCase);
            bool twoServices = settings.ShowClaude && settings.ShowCodex;
            if (twoServices && vertical)  MinimumSize = new Size(270, 250);
            else if (twoServices)         MinimumSize = new Size(520, 154);
            else                          MinimumSize = new Size(270, 154);
            if (Width < MinimumSize.Width) Width = MinimumSize.Width;
            if (Height < MinimumSize.Height) Height = MinimumSize.Height;
        }

        void ApplyIdealSize()
        {
            bool vertical = string.Equals(settings.LayoutMode, "vertical", StringComparison.OrdinalIgnoreCase);
            bool twoServices = settings.ShowClaude && settings.ShowCodex;
            int idealW, idealH;
            // card unit = 360px: W = 360+30 = 390; horizontal W = 2*360+10+30 = 760; H = 154
            // vertical H: 2*154+10 = 318
            if      (twoServices && !vertical) { idealW = 760; idealH = 154; }
            else if (twoServices)              { idealW = 390; idealH = 318; }
            else                               { idealW = 390; idealH = 154; }
            Width  = Math.Max(MinimumSize.Width,  idealW);
            Height = Math.Max(MinimumSize.Height, idealH);
            settings.Width  = Width;
            settings.Height = Height;
        }

        void DrawSideRail(Graphics g)
        {
            int x         = ClientSize.Width - 24;
            int closeY    = 4;
            int pinY      = 30;
            int tokenY    = 56;
            int fiveY     = ClientSize.Height - 72;
            int weekY     = ClientSize.Height - 46;
            int settingsY = ClientSize.Height - 20;

            hits["close"]     = new Rectangle(x - 6, closeY    - 6, 28, 28);
            hits["pin"]       = new Rectangle(x - 6, pinY      - 6, 28, 28);
            hits["token"]     = new Rectangle(x - 6, tokenY    - 6, 28, 28);
            hits["fiveReset"] = new Rectangle(x - 6, fiveY     - 6, 28, 28);
            hits["weekReset"] = new Rectangle(x - 6, weekY     - 6, 28, 28);
            hits["settings"]  = new Rectangle(x - 6, settingsY - 6, 28, 28);

            DrawIconButton(g, "close",     x, closeY,    Color.FromArgb(160, 160, 165), DrawCloseIcon);
            DrawIconButton(g, "pin",       x, pinY,       settings.AlwaysOnTop ? Color.FromArgb(100, 180, 255) : Color.FromArgb(100, 100, 105), DrawPinIcon);
            DrawIconButton(g, "token",     x, tokenY,     Color.FromArgb(130, 145, 165), DrawTokenToggleIcon);
            DrawIconButton(g, "fiveReset", x, fiveY,      Color.FromArgb(110, 125, 145), DrawFiveResetIcon);
            DrawIconButton(g, "weekReset", x, weekY,      Color.FromArgb(110, 125, 145), DrawWeekResetIcon);
            DrawIconButton(g, "settings",  x, settingsY,  Color.FromArgb(130, 130, 135), DrawGearIcon);
        }

        delegate void IconPainter(Graphics g, Rectangle r, Color color);

        void DrawIconButton(Graphics g, string key, int x, int y, Color color, IconPainter painter)
        {
            var r = new Rectangle(x - 1, y - 1, 20, 20);
            if (hoverKey == key)
            {
                Color hoverBg = key == "close" ? Color.FromArgb(190, 58, 58) : Color.FromArgb(50, 50, 54);
                using (var bg = new SolidBrush(hoverBg))
                using (var path = RoundRect(r.X - 4, r.Y - 4, r.Width + 8, r.Height + 8, 12))
                    g.FillPath(bg, path);
                color = key == "close" ? Color.White : Color.FromArgb(Math.Min(255, color.R + 40), Math.Min(255, color.G + 40), Math.Min(255, color.B + 40));
            }
            painter(g, r, color);
        }

        void DrawService(Graphics g, ServiceState state, int x, int y, int w, int h, string keyPrefix)
        {
            bool showUsed = state.Name == "Codex" ? settings.CodexShowUsed : settings.ClaudeShowUsed;
            int? fiveRemain = state.Data.FiveHourRemainingPercent();
            int? weekRemain = state.Data.WeeklyRemainingPercent();
            bool quotaExhausted = (fiveRemain.HasValue && fiveRemain.Value <= 0) || (weekRemain.HasValue && weekRemain.Value <= 0);
            bool exhausted = quotaExhausted || (state.Data.HitLimit && !fiveRemain.HasValue && !weekRemain.HasValue);
            bool stale = state.LastRefresh != DateTime.MinValue &&
                DateTime.Now - state.LastRefresh > TimeSpan.FromMinutes(Math.Max(2, settings.NormalIntervalMinutes * 2));
            Color cardAccent = exhausted ? Color.FromArgb(220, 77, 77) : state.Accent;

            // Card with gradient background
            using (var path = RoundRect(x, y, w, h, 12))
            {
                Color topColor = exhausted ? Color.FromArgb(42, 22, 22) : (stale ? Color.FromArgb(38, 34, 22) : Color.FromArgb(30, 30, 34));
                Color bottomColor = exhausted ? Color.FromArgb(32, 18, 18) : (stale ? Color.FromArgb(30, 28, 18) : Color.FromArgb(22, 22, 26));
                using (var grad = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(x, y, w, h), topColor, bottomColor, 90f))
                    g.FillPath(grad, path);

                // Subtle top highlight
                using (var highlight = new Pen(Color.FromArgb(exhausted ? 18 : 28, 255, 255, 255)))
                    g.DrawPath(highlight, path);
            }
            // Border
            using (var path = RoundRect(x, y, w, h, 12))
            using (var border = new Pen(exhausted ? Color.FromArgb(80, 40, 40) : (stale ? Color.FromArgb(80, 68, 30) : Color.FromArgb(48, 48, 54)), 0.8f))
                g.DrawPath(border, path);

            // Accent indicator dot
            using (var dotBrush = new SolidBrush(cardAccent))
                g.FillEllipse(dotBrush, x + 16, y + 17, 8, 8);

            using (var title = new Font("Segoe UI", 13f, FontStyle.Bold))
            using (var label = new Font("Segoe UI", LabelFontSize, FontStyle.Regular))
            using (var reset = new Font("Segoe UI", ResetFontSize, FontStyle.Regular))
            using (var num = new Font("Segoe UI", PercentFontSize, FontStyle.Bold))
            using (var white = new SolidBrush(Color.FromArgb(240, 242, 245)))
            using (var muted = new SolidBrush(Color.FromArgb(210, 215, 228)))
            using (var dim = new SolidBrush(Color.FromArgb(185, 190, 205)))
            {
                g.DrawString(state.Name, title, white, x + 30, y + 10);
                if (stale)
                    DrawBadge(g, T("古い", "Stale"), x + 100, y + 14, Color.FromArgb(110, 85, 20), Color.FromArgb(180, 150, 50));
                if (exhausted)
                    DrawBadge(g, T("上限", "Limit"), x + 100, y + 14, Color.FromArgb(100, 35, 35), Color.FromArgb(220, 100, 100));
                DrawCardControls(g, state, x, y, w, keyPrefix);

                if (!state.Data.HasAnyValue())
                {
                    g.DrawString("--", num, white, x + 58, y + 54);
                    string status = state.IsRefreshing ? T("更新中", "Updating") : StatusText(state.Status ?? state.Data.Status ?? "no_data");
                    g.DrawString(status, label, muted, x + 20, y + 88);
                    DrawLoginButton(g, x + w - 100, y + h - 40, keyPrefix + "-login");
                    return;
                }

                int contentTop = y + Math.Max(44, (int)Math.Ceiling(PercentFontSize + 24));
                int contentBottom = y + h - 10;
                int rowHeight = Math.Max(32, (int)Math.Ceiling(Math.Max(PercentFontSize * 1.55, LabelFontSize + ResetFontSize + 14)));
                int totalRows = rowHeight * 2;
                int free = Math.Max(0, contentBottom - contentTop - totalRows);
                int topPad = free / 2;
                int rowGap = Math.Max(2, Math.Min(10, free / 4));
                int firstY = contentTop + topPad;
                int secondY = firstY + rowHeight + rowGap;
                DrawRow(g, T("5時間", "5h"), state.Data.FiveHourDisplayPercent(showUsed), state.DisplayedFivePct, showUsed, false, state.Data.FiveHourReset, state.Data.FiveHourNotStarted, settings.FiveHourResetMode, x, firstY, w, state.Accent, label, reset, num, white, muted, dim);
                DrawRow(g, T("週", "Week"), state.Data.WeeklyDisplayPercent(showUsed), state.DisplayedWeekPct, showUsed, true, state.Data.WeeklyReset, state.Data.WeeklyNotStarted, settings.WeeklyResetMode, x, secondY, w, state.Accent, label, reset, num, white, muted, dim);
            }
        }

        void DrawCardControls(Graphics g, ServiceState state, int x, int y, int w, string keyPrefix)
        {
            int refreshX = x + w - 34;
            int controlY = y + 12;
            int boostX = refreshX - 28;
            var boostText = BoostText(state);
            int boostTextW = boostText.Length > 0 ? 52 : 0;
            int boostTextX = boostX - boostTextW - 4;

            hits[keyPrefix + "-refresh"] = new Rectangle(refreshX - 4, controlY - 3, 30, 28);
            hits[keyPrefix + "-boost"] = new Rectangle(boostX - 4, controlY - 3, 28, 28);

            using (var small = new Font("Segoe UI", 8.5f, FontStyle.Regular))
            using (var textBrush = new SolidBrush(Color.FromArgb(140, 150, 165)))
            {
                if (boostText.Length > 0)
                    g.DrawString(boostText, small, textBrush, boostTextX, controlY + 3);
            }

            DrawBoostIcon(g, boostX, controlY, state.BoostActive, hoverKey == keyPrefix + "-boost");
            DrawRefreshIcon(g, refreshX, controlY, keyPrefix + "-refresh", state.IsRefreshing);
        }

        string T(string ja, string en)
        {
            return English ? en : ja;
        }

        string StatusText(string status)
        {
            switch (status)
            {
                case "updating": return T("更新中", "Updating");
                case "fetch_error": return T("取得エラー", "Fetch error");
                case "no_data": return T("データなし", "No data");
                case "login_required": return T("ログインが必要", "Login required");
                case "no_usage_text": return T("使用量テキストなし", "No usage text");
                case "starting": return T("起動中", "Starting");
                default: return status ?? T("データなし", "No data");
            }
        }

        string LimitResetText(UsageData data, int? fiveRemain, int? weekRemain, bool english)
        {
            string raw = "";
            string mode = settings.FiveHourResetMode;
            if (fiveRemain.HasValue && fiveRemain.Value <= 0) raw = data.FiveHourReset;
            else if (weekRemain.HasValue && weekRemain.Value <= 0)
            {
                raw = data.WeeklyReset;
                mode = settings.WeeklyResetMode;
            }
            string text = ResetText(raw, mode, english);
            return english ? text.Replace("Reset in ", "in ") : text.Replace("リセットまで ", "あと ");
        }

        static bool TryGetResetRemaining(string raw, out TimeSpan remaining)
        {
            return TryGetResetRemaining(raw, true, out remaining);
        }

        static bool TryGetResetRemaining(string raw, bool rollTimeOnlyToTomorrow, out TimeSpan remaining)
        {
            remaining = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            string cleaned = Regex.Replace(raw, @"^\s*リセット\s*[：:]\s*", "").Trim();

            var relative = Regex.Match(cleaned, @"(?:(\d+)\s*時間)?\s*(?:(\d+)\s*分)?\s*後にリセット");
            if (relative.Success)
            {
                int hours = relative.Groups[1].Success ? int.Parse(relative.Groups[1].Value) : 0;
                int minutes = relative.Groups[2].Success ? int.Parse(relative.Groups[2].Value) : 0;
                remaining = new TimeSpan(hours, minutes, 0);
                return true;
            }

            DateTime target;
            if (TryParseResetTarget(cleaned, rollTimeOnlyToTomorrow, out target))
            {
                remaining = target - DateTime.Now;
                return true;
            }
            return false;
        }

        string BoostText(ServiceState state)
        {
            if (!state.BoostUntil.HasValue || state.BoostUntil.Value <= DateTime.Now) return "";
            int min = Math.Max(1, (int)Math.Ceiling((state.BoostUntil.Value - DateTime.Now).TotalMinutes));
            return English ? min + "m left" : "残り" + min + "分";
        }

        void DrawRow(Graphics g, string label, int? pct, double? barPct, bool showUsed, bool weekly, string resetText, bool notStarted, string resetMode, int x, int y, int w, Color accent, Font labelFont, Font resetFont, Font numFont, Brush white, Brush muted, Brush dim)
        {
            bool empty = !pct.HasValue;
            bool limit = pct.HasValue && (showUsed ? 100 - pct.Value : pct.Value) <= settings.CriticalRemainingPercent;
            bool atLimit = pct.HasValue && (showUsed ? pct.Value >= 100 : pct.Value <= 0);
            bool warning = pct.HasValue && !limit && (showUsed ? 100 - pct.Value : pct.Value) <= settings.WarningRemainingPercent;
            Color rowColor = RowColor(pct, showUsed, accent);
            Color numColor = Color.FromArgb(240, 242, 248);
            using (var pctBrush = new SolidBrush(numColor))
            {
            int labelX = x + 10;
            int label5hW = (int)Math.Ceiling(g.MeasureString(T("5時間", "5h"), labelFont).Width);
            int labelWkW = (int)Math.Ceiling(g.MeasureString(T("週", "Week"), labelFont).Width);
            int labelColW = Math.Max(32, Math.Max(label5hW, labelWkW));
            int curLabelW = (int)Math.Ceiling(g.MeasureString(label, labelFont).Width);
            int modeX = labelX + labelColW + 4;
            string modeText = showUsed ? T("使用", "Used") : T("残り", "Left");
            int modeColW = Math.Max(28, Math.Max(
                (int)Math.Ceiling(g.MeasureString(T("使用", "Used"), labelFont).Width),
                (int)Math.Ceiling(g.MeasureString(T("残り", "Left"), labelFont).Width)));
            int percentX = modeX + modeColW + 4;
            int labelY = y + Math.Max(0, (int)Math.Round((numFont.Size - labelFont.Size) / 2.0));
            g.DrawString(label, labelFont, muted, labelX + labelColW - curLabelW, labelY);
            g.DrawString(modeText, labelFont, dim, modeX, labelY);
            string pctStr = empty ? "--" : pct.Value + "%";
            int pctColW = Math.Max(48, (int)Math.Ceiling(g.MeasureString("100%", numFont).Width));
            using (var pctFormat = new StringFormat())
            {
                pctFormat.Alignment = StringAlignment.Far;
                pctFormat.LineAlignment = StringAlignment.Near;
                pctFormat.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString(pctStr, numFont, pctBrush, new RectangleF(percentX, y - 4, pctColW, numFont.Height + 4), pctFormat);
            }
            int barX = percentX + pctColW + 12;
            int barY = y + Math.Max(5, (int)Math.Round(PercentFontSize * 0.42));
            int barW = Math.Max(70, w - (barX - x) - 10);
            DrawBar(g, barX, barY, barW, 9, barPct, rowColor);

            string reset = notStarted ? T("未開始", "Not started") : ResetText(resetText, resetMode, English, weekly);
            if (!string.IsNullOrEmpty(reset))
            {
                if (atLimit)
                    using (var boldReset = new Font(resetFont, FontStyle.Bold))
                        g.DrawString(reset, boldReset, white, barX, y + Math.Max(18, (int)Math.Round(PercentFontSize * 1.0)));
                else
                    g.DrawString(reset, resetFont, dim, barX, y + Math.Max(18, (int)Math.Round(PercentFontSize * 1.0)));
            }
            }
        }

        Color RowColor(int? pct, bool showUsed, Color normal)
        {
            if (!pct.HasValue) return normal;
            int remaining = showUsed ? 100 - pct.Value : pct.Value;
            if (remaining <= settings.CriticalRemainingPercent)
                return Color.FromArgb(224, 73, 73);
            if (remaining <= settings.WarningRemainingPercent)
                return Color.FromArgb(232, 169, 36);
            return normal;
        }

        static string ResetText(string raw)
        {
            return ResetText(raw, false, false);
        }

        static string ResetText(string raw, string mode, bool english)
        {
            return ResetText(raw, string.Equals(mode, "time", StringComparison.OrdinalIgnoreCase), english);
        }

        static string ResetText(string raw, string mode, bool english, bool weekly)
        {
            return ResetText(raw, string.Equals(mode, "time", StringComparison.OrdinalIgnoreCase), english, weekly);
        }

        static string ResetText(string raw, bool preferAbsolute, bool english)
        {
            return ResetText(raw, preferAbsolute, english, true);
        }

        static string ResetText(string raw, bool preferAbsolute, bool english, bool weekly)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var lower = raw.ToLowerInvariant();
            string cleaned = Regex.Replace(raw, @"^\s*リセット\s*[：:]\s*", "").Trim();
            if (cleaned.Contains("リセットまで")) return english ? cleaned.Replace("リセットまで", "Reset in") : cleaned;
            var relative = RelativeResetText(cleaned, preferAbsolute, english, weekly);
            if (!string.IsNullOrEmpty(relative)) return relative;

            DateTime target;
            if (TryParseResetTarget(cleaned, weekly, out target))
            {
                if (preferAbsolute) return (english ? "Reset " : "リセット ") + FormatResetTime(target, english);
                return (english ? "Reset in " : "リセットまで ") + FormatDuration(target - DateTime.Now, english);
            }
            if (!weekly && IsTimeOnly(cleaned)) return "";

            if (cleaned.Contains("後にリセット"))
            {
                var s = cleaned.Replace("後にリセット", "").Replace("リセット", "").Trim();
                return english ? "Reset in " + TranslateDurationText(s) : "リセットまで " + s;
            }
            if (lower.Contains("reset"))
            {
                var m = Regex.Match(cleaned, @"(\d{1,2}/\d{1,2}\s+\d{1,2}:\d{2})");
                if (m.Success) return (english ? "Reset " : "リセット ") + m.Groups[1].Value;
                return cleaned;
            }
            return (english ? "Reset " : "リセット ") + cleaned;
        }

        static string RelativeResetText(string text, bool preferAbsolute, bool english)
        {
            return RelativeResetText(text, preferAbsolute, english, true);
        }

        static string RelativeResetText(string text, bool preferAbsolute, bool english, bool weekly)
        {
            var m = Regex.Match(text, @"(?:(\d+)\s*時間)?\s*(?:(\d+)\s*分)?\s*後にリセット");
            if (!m.Success) return "";
            int hours = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0;
            int minutes = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
            var span = new TimeSpan(hours, minutes, 0);
            if (preferAbsolute) return (english ? "Reset " : "リセット ") + FormatResetTime(DateTime.Now.Add(span), english);
            return (english ? "Reset in " : "リセットまで ") + FormatDuration(span, english);
        }

        static bool IsTimeOnly(string text)
        {
            return Regex.IsMatch(text.Trim(), @"^\d{1,2}:\d{2}$");
        }

        static bool TryParseResetTarget(string text, out DateTime target)
        {
            return TryParseResetTarget(text, true, out target);
        }

        static bool TryParseResetTarget(string text, bool rollTimeOnlyToTomorrow, out DateTime target)
        {
            target = DateTime.MinValue;
            var now = DateTime.Now;
            var dateTime = Regex.Match(text, @"(?:(\d{4})/)?(\d{1,2})/(\d{1,2})\s+(\d{1,2}):(\d{2})");
            if (dateTime.Success)
            {
                int year = dateTime.Groups[1].Success ? int.Parse(dateTime.Groups[1].Value) : now.Year;
                target = new DateTime(year, int.Parse(dateTime.Groups[2].Value), int.Parse(dateTime.Groups[3].Value), int.Parse(dateTime.Groups[4].Value), int.Parse(dateTime.Groups[5].Value), 0);
                if (!dateTime.Groups[1].Success && target < now.AddMinutes(-1)) target = target.AddYears(1);
                return true;
            }

            var weekdayTime = Regex.Match(text, @"(\d{1,2}):(\d{2})\s*(?:[（(]\s*([月火水木金土日])(?:曜(?:日)?)?\s*[）)]|([月火水木金土日])曜(?:日)?)");
            if (weekdayTime.Success)
            {
                int hour = int.Parse(weekdayTime.Groups[1].Value);
                int minute = int.Parse(weekdayTime.Groups[2].Value);
                string dayText = weekdayTime.Groups[3].Success ? weekdayTime.Groups[3].Value : weekdayTime.Groups[4].Value;
                DayOfWeek day;
                if (TryParseJapaneseWeekday(dayText, out day))
                {
                    int days = ((int)day - (int)now.DayOfWeek + 7) % 7;
                    target = now.Date.AddDays(days).AddHours(hour).AddMinutes(minute);
                    if (target < now.AddMinutes(-1)) target = target.AddDays(7);
                    return true;
                }
            }

            var time = Regex.Match(text, @"(?:^|[^\d])(\d{1,2}):(\d{2})(?:$|[^\d])");
            if (time.Success)
            {
                target = new DateTime(now.Year, now.Month, now.Day, int.Parse(time.Groups[1].Value), int.Parse(time.Groups[2].Value), 0);
                if (target < now.AddMinutes(-1))
                {
                    if (!rollTimeOnlyToTomorrow) return false;
                    target = target.AddDays(1);
                }
                return true;
            }
            return false;
        }

        static bool TryParseJapaneseWeekday(string text, out DayOfWeek day)
        {
            switch (text)
            {
                case "日": day = DayOfWeek.Sunday; return true;
                case "月": day = DayOfWeek.Monday; return true;
                case "火": day = DayOfWeek.Tuesday; return true;
                case "水": day = DayOfWeek.Wednesday; return true;
                case "木": day = DayOfWeek.Thursday; return true;
                case "金": day = DayOfWeek.Friday; return true;
                case "土": day = DayOfWeek.Saturday; return true;
                default: day = DayOfWeek.Sunday; return false;
            }
        }

        static string FormatDuration(TimeSpan span)
        {
            return FormatDuration(span, false);
        }

        static string FormatDuration(TimeSpan span, bool english)
        {
            if (span.TotalSeconds <= 0) return english ? "0m" : "0分";
            int totalMinutes = Math.Max(0, (int)Math.Ceiling(span.TotalMinutes));
            int hours = totalMinutes / 60;
            int minutes = totalMinutes % 60;
            if (english)
            {
                if (hours > 0) return hours + "h " + minutes + "m";
                return minutes + "m";
            }
            if (hours > 0) return hours + "時間" + minutes + "分";
            return minutes + "分";
        }

        static string TranslateDurationText(string text)
        {
            return text.Replace("時間", "h ").Replace("分", "m").Trim();
        }

        static string FormatResetTime(DateTime value, bool english)
        {
            var today = DateTime.Now.Date;
            if (value.Date == today) return value.ToString("H:mm");
            if (value.Date == today.AddDays(1)) return (english ? "Tomorrow " : "明日 ") + value.ToString("H:mm");
            return value.ToString("M/d H:mm");
        }

        void DrawLoginButton(Graphics g, int x, int y, string key)
        {
            int buttonW = English ? 86 : 80;
            int buttonH = 28;
            hits[key] = new Rectangle(x, y, buttonW, buttonH);
            bool hover = hoverKey == key;
            using (var p = RoundRect(x, y, buttonW, buttonH, 10))
            {
                Color top = hover ? Color.FromArgb(72, 72, 78) : Color.FromArgb(52, 52, 58);
                Color bottom = hover ? Color.FromArgb(58, 58, 64) : Color.FromArgb(40, 40, 46);
                using (var grad = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(x, y, buttonW, buttonH), top, bottom, 90f))
                    g.FillPath(grad, p);
                using (var border = new Pen(Color.FromArgb(hover ? 90 : 65, 90, 100), 0.8f))
                    g.DrawPath(border, p);
            }
            using (var f = new Font("Segoe UI", 9.2f, FontStyle.Bold))
            using (var white = new SolidBrush(Color.FromArgb(235, 238, 242)))
                g.DrawString(T("ログイン", "Login"), f, white, x + 14, y + 5);
        }

        void DrawBadge(Graphics g, string text, int x, int y, Color bgColor, Color textColor)
        {
            using (var f = new Font("Segoe UI", 8.2f, FontStyle.Bold))
            {
                int badgeW = Math.Max(32, (int)Math.Ceiling(g.MeasureString(text, f).Width) + 16);
                using (var path = RoundRect(x, y, badgeW, 20, 10))
                {
                    using (var bg = new SolidBrush(bgColor))
                        g.FillPath(bg, path);
                    using (var border = new Pen(Color.FromArgb(40, textColor.R, textColor.G, textColor.B), 0.6f))
                        g.DrawPath(border, path);
                }
                using (var b = new SolidBrush(textColor))
                using (var sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    sf.FormatFlags = StringFormatFlags.NoWrap;
                    g.DrawString(text, f, b, new RectangleF(x, y, badgeW, 20), sf);
                }
            }
        }

        void DrawServiceMark(Graphics g, string name, int x, int y, Color accent)
        {
            using (var b = new SolidBrush(Color.FromArgb(42, 42, 44)))
            using (var p = RoundRect(x - 9, y - 9, 18, 18, 5))
                g.FillPath(b, p);
            using (var pen = new Pen(accent, 1.8f))
            {
                if (name == "Codex")
                {
                    g.DrawLine(pen, x - 4, y - 4, x + 1, y);
                    g.DrawLine(pen, x + 1, y, x - 4, y + 4);
                    g.DrawLine(pen, x + 3, y + 5, x + 8, y + 5);
                }
                else
                {
                    g.DrawLine(pen, x, y - 7, x + 3, y - 1);
                    g.DrawLine(pen, x + 3, y - 1, x + 8, y);
                    g.DrawLine(pen, x + 8, y, x + 3, y + 1);
                    g.DrawLine(pen, x + 3, y + 1, x, y + 7);
                    g.DrawLine(pen, x, y + 7, x - 3, y + 1);
                    g.DrawLine(pen, x - 3, y + 1, x - 8, y);
                    g.DrawLine(pen, x - 8, y, x - 3, y - 1);
                    g.DrawLine(pen, x - 3, y - 1, x, y - 7);
                }
            }
        }

        void DrawRefreshIcon(Graphics g, int x, int y, string key, bool active)
        {
            var r = new Rectangle(x - 2, y - 2, 24, 24);
            if (hoverKey == key && !active)
            {
                using (var bg = new SolidBrush(Color.FromArgb(48, 48, 54)))
                using (var path = RoundRect(r.X - 1, r.Y - 1, r.Width + 2, r.Height + 2, 12))
                    g.FillPath(bg, path);
            }

            string[] frames = { "◜", "◝", "◞", "◟" };
            string s = active ? frames[spinnerFrame] : "↻";
            Color iconColor = active ? Color.FromArgb(100, 180, 255) : Color.FromArgb(200, 205, 212);
            using (var f = new Font("Segoe UI Symbol", active ? 13f : 12.5f, FontStyle.Regular))
            using (var b = new SolidBrush(iconColor))
                g.DrawString(s, f, b, x + (active ? 2 : 1), y + (active ? 0 : -1));
        }

        void DrawBoostIcon(Graphics g, int x, int y, bool on, bool hover)
        {
            var r = new Rectangle(x - 2, y - 2, 24, 24);
            if (hover)
            {
                using (var bg = new SolidBrush(Color.FromArgb(48, 48, 54)))
                using (var path = RoundRect(r.X - 1, r.Y - 1, r.Width + 2, r.Height + 2, 12))
                    g.FillPath(bg, path);
            }
            if (on)
            {
                using (var glow = new SolidBrush(Color.FromArgb(30, 255, 210, 50)))
                    g.FillEllipse(glow, x - 1, y - 1, 22, 22);
            }
            int cx = x + 9;
            int cy = y + 10;
            var bolt = new Point[]
            {
                new Point(cx + 1, cy - 8),
                new Point(cx - 3, cy + 1),
                new Point(cx + 1, cy + 1),
                new Point(cx - 1, cy + 8),
                new Point(cx + 3, cy - 1),
                new Point(cx - 1, cy - 1),
            };
            Color boltColor = on ? Color.FromArgb(255, 220, 60) : (hover ? Color.FromArgb(225, 228, 232) : Color.FromArgb(200, 205, 212));
            using (var brush = new SolidBrush(boltColor))
                g.FillPolygon(brush, bolt);
            using (var pen = new Pen(on ? Color.FromArgb(200, 170, 30) : Color.FromArgb(100, 105, 112), 0.6f))
                g.DrawPolygon(pen, bolt);
        }

        void DrawCloseIcon(Graphics g, Rectangle r, Color color)
        {
            using (var pen = new Pen(color, 1.6f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
            {
                g.DrawLine(pen, r.X + 6, r.Y + 6, r.X + 14, r.Y + 14);
                g.DrawLine(pen, r.X + 14, r.Y + 6, r.X + 6, r.Y + 14);
            }
        }

        void DrawPinIcon(Graphics g, Rectangle r, Color color)
        {
            using (var pen = new Pen(color, 1.6f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
            {
                // Pin head (rounded rectangle area)
                g.DrawLine(pen, r.X + 7, r.Y + 4, r.X + 14, r.Y + 4);
                g.DrawLine(pen, r.X + 14, r.Y + 4, r.X + 14, r.Y + 10);
                g.DrawLine(pen, r.X + 14, r.Y + 10, r.X + 7, r.Y + 10);
                g.DrawLine(pen, r.X + 7, r.Y + 10, r.X + 7, r.Y + 4);
                // Pin needle
                g.DrawLine(pen, r.X + 10, r.Y + 10, r.X + 10, r.Y + 16);
                // Pin point
                g.FillEllipse(new SolidBrush(color), r.X + 9, r.Y + 15, 3, 3);
            }
        }

        void DrawGearIcon(Graphics g, Rectangle r, Color color)
        {
            int cx = r.X + r.Width / 2;
            int cy = r.Y + r.Height / 2;
            using (var pen = new Pen(color, 1.5f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
            {
                // Outer teeth
                for (int i = 0; i < 6; i++)
                {
                    double a = i * Math.PI / 3.0;
                    int x1 = cx + (int)Math.Round(Math.Cos(a) * 5.5);
                    int y1 = cy + (int)Math.Round(Math.Sin(a) * 5.5);
                    int x2 = cx + (int)Math.Round(Math.Cos(a) * 8.5);
                    int y2 = cy + (int)Math.Round(Math.Sin(a) * 8.5);
                    g.DrawLine(pen, x1, y1, x2, y2);
                }
                g.DrawEllipse(pen, cx - 5, cy - 5, 10, 10);
                using (var fill = new SolidBrush(color))
                    g.FillEllipse(fill, cx - 2, cy - 2, 4, 4);
            }
        }

        void DrawLayoutIcon(Graphics g, Rectangle r, Color color)
        {
            bool vertical = string.Equals(settings.LayoutMode, "vertical", StringComparison.OrdinalIgnoreCase);
            using (var pen = new Pen(color, 1.3f))
            using (var fill = new SolidBrush(Color.FromArgb(60, color)))
            {
                if (vertical)
                {
                    // 縦→横 への切り替えを示す: 左右に並んだ2矩形
                    int bw = 7, bh = 10, gap = 2;
                    int left  = r.X + r.Width / 2 - bw - gap / 2;
                    int top   = r.Y + r.Height / 2 - bh / 2;
                    g.FillRectangle(fill, left,          top, bw, bh);
                    g.DrawRectangle(pen,  left,          top, bw, bh);
                    g.FillRectangle(fill, left + bw + gap, top, bw, bh);
                    g.DrawRectangle(pen,  left + bw + gap, top, bw, bh);
                }
                else
                {
                    // 横→縦 への切り替えを示す: 上下に並んだ2矩形
                    int bw = 10, bh = 7, gap = 2;
                    int left = r.X + r.Width  / 2 - bw / 2;
                    int top  = r.Y + r.Height / 2 - bh - gap / 2;
                    g.FillRectangle(fill, left, top,          bw, bh);
                    g.DrawRectangle(pen,  left, top,          bw, bh);
                    g.FillRectangle(fill, left, top + bh + gap, bw, bh);
                    g.DrawRectangle(pen,  left, top + bh + gap, bw, bh);
                }
            }
        }

        static void DrawBar(Graphics g, int x, int y, int w, int h, double? pct, Color accent)
        {
            int barH = Math.Max(h, 9);
            // Track background with subtle inset
            using (var bgPath = RoundRect(x, y, w, barH, barH / 2))
            {
                using (var bg = new SolidBrush(Color.FromArgb(20, 20, 24)))
                    g.FillPath(bg, bgPath);
                using (var inset = new Pen(Color.FromArgb(35, 35, 40), 0.6f))
                    g.DrawPath(inset, bgPath);
            }
            if (!pct.HasValue) return;
            double clamped = Math.Max(0, Math.Min(100, pct.Value));
            int fillW = Math.Max(clamped <= 0 ? 0 : 6, (int)Math.Round(w * clamped / 100.0));
            if (fillW <= 0) return;

            // Glow beneath the bar
            using (var glowPath = RoundRect(x, y + 1, fillW, barH, barH / 2))
            using (var glow = new SolidBrush(Color.FromArgb(30, accent.R, accent.G, accent.B)))
                g.FillPath(glow, glowPath);

            // Gradient fill
            using (var fillPath = RoundRect(x, y, fillW, barH, barH / 2))
            {
                Color lighter = Color.FromArgb(Math.Min(255, accent.R + 40), Math.Min(255, accent.G + 40), Math.Min(255, accent.B + 40));
                using (var grad = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(x, y, fillW, barH), lighter, accent, 90f))
                    g.FillPath(grad, fillPath);

                // Top highlight on bar
                using (var hlPath = RoundRect(x + 1, y + 1, Math.Max(4, fillW - 2), barH / 2, barH / 4))
                using (var hl = new SolidBrush(Color.FromArgb(45, 255, 255, 255)))
                    g.FillPath(hl, hlPath);
            }
        }

        void DrawTokenToggleIcon(Graphics g, Rectangle r, Color color)
        {
            bool showUsed = settings.ClaudeShowUsed;
            string text = showUsed ? T("使", "U") : T("残", "R");
            using (var f = new Font("Segoe UI", 9f, FontStyle.Bold))
            using (var br = new SolidBrush(color))
            {
                SizeF sz = g.MeasureString(text, f);
                g.DrawString(text, f, br, r.X + (r.Width - sz.Width) / 2f, r.Y + (r.Height - sz.Height) / 2f);
            }
        }

        void DrawFiveResetIcon(Graphics g, Rectangle r, Color color)
        {
            bool isRelative = string.Equals(settings.FiveHourResetMode, "relative", StringComparison.OrdinalIgnoreCase);
            int cx = r.X + r.Width / 2;
            int cr = 6;
            int cy = r.Y + cr + 1;
            using (var pen = new Pen(color, 1.3f))
            {
                g.DrawEllipse(pen, cx - cr, cy - cr, cr * 2, cr * 2);
                if (isRelative)
                    g.DrawLine(pen, cx, cy, cx, cy - cr + 2);
                else
                    g.DrawLine(pen, cx, cy, cx + cr - 2, cy);
            }
            using (var f = new Font("Segoe UI", 6.5f, FontStyle.Bold))
            using (var br = new SolidBrush(color))
            {
                string label = "5h";
                SizeF sz = g.MeasureString(label, f);
                g.DrawString(label, f, br, cx - sz.Width / 2f, r.Bottom - sz.Height);
            }
        }

        void DrawWeekResetIcon(Graphics g, Rectangle r, Color color)
        {
            bool isRelative = string.Equals(settings.WeeklyResetMode, "relative", StringComparison.OrdinalIgnoreCase);
            int cx = r.X + r.Width / 2;
            int cr = 6;
            int cy = r.Y + cr + 1;
            using (var pen = new Pen(color, 1.3f))
            {
                g.DrawEllipse(pen, cx - cr, cy - cr, cr * 2, cr * 2);
                if (isRelative)
                    g.DrawLine(pen, cx, cy, cx, cy - cr + 2);
                else
                    g.DrawLine(pen, cx, cy, cx + cr - 2, cy);
            }
            using (var f = new Font("Segoe UI", 6f, FontStyle.Bold))
            using (var br = new SolidBrush(color))
            {
                string label = T("週", "Wk");
                SizeF sz = g.MeasureString(label, f);
                g.DrawString(label, f, br, cx - sz.Width / 2f, r.Bottom - sz.Height);
            }
        }

        void DrawResizeGrip(Graphics g)
        {
            int right = ClientSize.Width - 6;
            int bottom = ClientSize.Height - 6;
            using (var dotBrush = new SolidBrush(Color.FromArgb(70, 75, 82)))
            {
                for (int row = 0; row < 3; row++)
                    for (int col = 2 - row; col < 3; col++)
                        g.FillEllipse(dotBrush, right - (2 - col) * 5 - 2, bottom - (2 - row) * 5 - 2, 3, 3);
            }
        }

        void ShowSettingsDialog()
        {
            string prevLayout     = settings.LayoutMode;
            bool   prevShowCodex  = settings.ShowCodex;
            bool   prevShowClaude = settings.ShowClaude;

            using (var dlg = new SettingsForm(settings, () =>
            {
                ApplyLayoutMinimumSize();
                ApplyIdealSize();
                TopMost = settings.AlwaysOnTop;
                Invalidate();
            }))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    bool layoutChanged = settings.LayoutMode    != prevLayout     ||
                                        settings.ShowCodex      != prevShowCodex  ||
                                        settings.ShowClaude     != prevShowClaude;
                    ApplyLayoutMinimumSize();
                    if (layoutChanged) ApplyIdealSize();
                    TopMost = settings.AlwaysOnTop;
                    settings.Save();
                    Invalidate();
                }
            }
        }

        static System.Drawing.Drawing2D.GraphicsPath RoundRect(int x, int y, int w, int h, int r)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            int d = Math.Max(1, r * 2);
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + w - d, y, d, d, 270, 90);
            path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            path.AddArc(x, y + h - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        static void WriteDebug(string name, string text)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".headroom");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name), text ?? "");
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

    sealed class SettingsForm : Form
    {
        readonly WidgetSettings settings;
        readonly WidgetSettings original;
        readonly Action preview;
        readonly ToolTip tooltips = new ToolTip();
        bool _updatingLanguage;
        DarkScrollContainer scrollContainer;
        bool _resizing;
        Point _resizeStart;
        Size  _resizeStartSize;

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int sz);

        readonly DarkComboBox language = new DarkComboBox();
        readonly DarkComboBox layoutMode = new DarkComboBox();
        readonly DarkComboBox codexMode = new DarkComboBox();
        readonly DarkComboBox claudeMode = new DarkComboBox();
        readonly DarkComboBox fiveResetMode = new DarkComboBox();
        readonly DarkComboBox weeklyResetMode = new DarkComboBox();
        readonly DarkTextBox normal = new DarkTextBox();
        readonly DarkTextBox boostDuration = new DarkTextBox();
        readonly DarkTextBox boostInterval = new DarkTextBox();
        readonly DarkComboBox topMost   = new DarkComboBox();
        readonly DarkComboBox showCodex  = new DarkComboBox();
        readonly DarkComboBox showClaude = new DarkComboBox();
        readonly DarkTextBox warningPercent = new DarkTextBox();
        readonly DarkTextBox criticalPercent = new DarkTextBox();

        public SettingsForm(WidgetSettings settings, Action preview)
        {
            this.settings = settings;
            this.original = settings.Clone();
            this.preview = preview;
            Text = T("設定", "Settings");
            Width = 880;
            Height = Math.Min(640, Screen.PrimaryScreen.WorkingArea.Height - 80);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.FromArgb(14, 14, 18);
            ForeColor = Color.WhiteSmoke;
            Font = new Font("Yu Gothic UI", 9.5f);

            var title = new Label
            {
                Text = T("設定", "Settings"),
                Tag = "設定|Settings",
                Location = new Point(28, 16),
                Width = 300,
                Height = 26,
                Font = new Font("Yu Gothic UI", 13f, FontStyle.Bold),
                ForeColor = Color.FromArgb(235, 238, 244),
                BackColor = Color.Transparent
            };
            Controls.Add(title);

            var cancel = new RoundButton {
                Text = T("キャンセル", "Cancel"), Tag = "キャンセル|Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(Width - 218, 12), Width = 96, Height = 34,
                BackColor = Color.FromArgb(32, 32, 38),
                ForeColor = Color.FromArgb(200, 206, 218),
                Font = new Font("Yu Gothic UI", 10.5f),
                CornerRadius = 14,
                HoverBackColor   = Color.FromArgb(48, 48, 56),
                PressedBackColor = Color.FromArgb(28, 28, 34),
                BorderColorNormal = Color.FromArgb(60, 60, 68)
            };
            var ok = new RoundButton {
                Text = T("保存", "Save"), Tag = "保存|Save",
                DialogResult = DialogResult.OK,
                Location = new Point(Width - 114, 12), Width = 90, Height = 34,
                BackColor = Color.FromArgb(45, 132, 235),
                ForeColor = Color.White,
                Font = new Font("Yu Gothic UI", 10.5f, FontStyle.Bold),
                CornerRadius = 14,
                HoverBackColor   = Color.FromArgb(72, 152, 250),
                PressedBackColor = Color.FromArgb(35, 112, 210),
                BorderColorNormal = Color.FromArgb(45, 132, 235)
            };
            ok.Click += (s, e) => ApplyToSettings();
            Controls.Add(cancel);
            Controls.Add(ok);

            Controls.Add(new Panel { Location = new Point(0, 56), Width = Width, Height = 1, BackColor = Color.FromArgb(42, 42, 48) });

            scrollContainer = new DarkScrollContainer { Location = new Point(0, 57), Size = new Size(880, Height - 57) };
            Controls.Add(scrollContainer);
            var body = scrollContainer.Content;

            var vDivider = new Panel { Location = new Point(440, 0), Width = 1, Height = 2000, BackColor = Color.FromArgb(32, 32, 38) };
            body.Controls.Add(vDivider);
            var leftCard = SettingsCard(0, 0, 440, 2000);
            var rightCard = SettingsCard(441, 0, 439, 2000);
            body.Controls.Add(leftCard);
            body.Controls.Add(rightCard);

            int leftY = 12;
            AddSection(leftCard, "一般", "General", ref leftY);
            AddRow(leftCard, "Language", "Language", "", "", language, ref leftY);
            SetupCombo(topMost, settings.AlwaysOnTop ? "enabled" : "disabled", new[] { T("有効", "Enabled"), T("無効", "Disabled") });
            AddRow(leftCard, "最前面に固定", "Always on top", "", "", topMost, ref leftY);
            SetupCombo(showCodex,  settings.ShowCodex  ? "enabled" : "disabled", new[] { T("有効", "Enabled"), T("無効", "Disabled") });
            SetupCombo(showClaude, settings.ShowClaude ? "enabled" : "disabled", new[] { T("有効", "Enabled"), T("無効", "Disabled") });
            AddRow(leftCard, "Codex 表示", "Codex display", "", "", showCodex,  ref leftY);
            AddRow(leftCard, "Claude 表示", "Claude display", "", "", showClaude, ref leftY);
            AddSection(leftCard, "レイアウト", "Layout", ref leftY);
            AddRow(leftCard, "配置", "Arrangement", "", "", layoutMode, ref leftY);
            AddRow(leftCard, "Codex トークン表示", "Codex token display", "残量 / 使用量", "remaining / used", codexMode, ref leftY);
            AddRow(leftCard, "Claude トークン表示", "Claude token display", "残量 / 使用量", "remaining / used", claudeMode, ref leftY);
            AddRow(leftCard, "5時間リセット表示", "5h reset display", "", "", fiveResetMode, ref leftY);
            AddRow(leftCard, "週リセット表示", "Weekly reset display", "", "", weeklyResetMode, ref leftY);

            int rightY = 12;
            AddSection(rightCard, "更新", "Refresh", ref rightY);
            AddRow(rightCard, "通常更新間隔 (分)", "Normal interval (min)", "", "", normal, ref rightY);
            AddRow(rightCard, "ブースト時間 (分)", "Boost duration (min)", "", "", boostDuration, ref rightY);
            AddRow(rightCard, "ブースト更新間隔 (分)", "Boost interval (min)", "", "", boostInterval, ref rightY);
            AddSection(rightCard, "閾値", "Thresholds", ref rightY);
            AddNumberWithColor(rightCard, "黄色になる残量 (%)", "Yellow threshold (%)", "", "", warningPercent, settings.WarningRemainingPercent, ref rightY, 1, 99);
            AddNumberWithColor(rightCard, "赤になる残量 (%)", "Red threshold (%)", "", "", criticalPercent, settings.CriticalRemainingPercent, ref rightY, 1, 99);

            SetupCombo(layoutMode, settings.LayoutMode, new[] { T("横", "Wide"), T("縦", "Tall") });
            SetupCombo(codexMode, settings.CodexShowUsed ? "used" : "remaining", new[] { T("残量", "Remaining"), T("使用量", "Used") });
            SetupCombo(claudeMode, settings.ClaudeShowUsed ? "used" : "remaining", new[] { T("残量", "Remaining"), T("使用量", "Used") });
            SetupCombo(fiveResetMode, settings.FiveHourResetMode, new[] { T("リセット時刻", "Clock time"), T("残り時間", "Time left") });
            SetupCombo(weeklyResetMode, settings.WeeklyResetMode, new[] { T("リセット時刻", "Clock time"), T("残り時間", "Time left") });
            SetupCombo(language, settings.Language, new[] { "日本語", "English" });
            SetupNumbers();

            int contentH = Math.Max(leftY + 16, rightY + 16);
            leftCard.Height = contentH;
            rightCard.Height = contentH;
            vDivider.Height = contentH;
            scrollContainer.SetContentHeight(contentH);
            scrollContainer.AttachWheelToChildren();

            // ウィンドウ高をコンテンツに合わせて隙間をなくす
            int idealFormH = Math.Min(Screen.PrimaryScreen.WorkingArea.Height - 80, contentH + 57);
            Height = idealFormH;
            scrollContainer.Size = new Size(Width, Height - 57);

            AcceptButton = ok;
            CancelButton = cancel;

            WireLivePreview();
            FormClosed += (s, e) =>
            {
                if (DialogResult != DialogResult.OK)
                {
                    settings.CopyFrom(original);
                    preview();
                }
            };
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            try { int r = 3; DwmSetWindowAttribute(Handle, 33, ref r, 4); } catch { }
            var wa = Screen.FromControl(this).WorkingArea;
            if (Bottom > wa.Bottom) Top = wa.Bottom - Height;
            if (Top < wa.Top) Top = wa.Top;
            if (Right > wa.Right) Left = wa.Right - Width;
            if (Left < wa.Left) Left = wa.Left;
            MinimumSize = new Size(600, 300);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (scrollContainer != null)
                scrollContainer.Size = new Size(Width, Height - 57);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            using (var hg = new System.Drawing.Drawing2D.LinearGradientBrush(
                new Rectangle(0, 0, Width, 57),
                Color.FromArgb(30, 30, 34),
                Color.FromArgb(22, 22, 26),
                90f))
                g.FillRectangle(hg, 0, 0, Width, 56);

            using (var b1 = new Pen(Color.FromArgb(48, 48, 54)))
                g.DrawRectangle(b1, 0, 0, Width - 1, Height - 1);
            using (var b2 = new Pen(Color.FromArgb(28, 255, 255, 255)))
                g.DrawRectangle(b2, 1, 1, Width - 3, Height - 3);

            // Resize grip (bottom-right)
            int gx = Width - 14, gy = Height - 14;
            using (var p = new Pen(Color.FromArgb(70, 70, 80), 1.2f))
            {
                for (int i = 0; i < 3; i++)
                {
                    int d = i * 5;
                    g.DrawLine(p, gx + d, Height - 4, Width - 4, gy + d);
                }
            }
        }

        bool IsSettingsResizeGrip(Point pt) { return pt.X >= Width - 18 && pt.Y >= Height - 18; }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && IsSettingsResizeGrip(e.Location))
            {
                _resizing = true;
                _resizeStart = e.Location;
                _resizeStartSize = Size;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = IsSettingsResizeGrip(e.Location) ? Cursors.SizeNWSE : Cursors.Default;
            if (!_resizing) return;
            int dw = e.X - _resizeStart.X;
            int dh = e.Y - _resizeStart.Y;
            Width  = Math.Max(MinimumSize.Width,  _resizeStartSize.Width  + dw);
            Height = Math.Max(MinimumSize.Height, _resizeStartSize.Height + dh);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _resizing = false;
        }

        const int WM_NCHITTEST = 0x84;
        const int HTCAPTION = 2;

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_NCHITTEST && m.Result == (IntPtr)1)
            {
                short cx = (short)(m.LParam.ToInt32() & 0xFFFF);
                short cy = (short)((m.LParam.ToInt32() >> 16) & 0xFFFF);
                var pt = PointToClient(new Point(cx, cy));
                if (pt.Y >= 0 && pt.Y < 56 && pt.X < Width - 225)
                    m.Result = (IntPtr)HTCAPTION;
            }
        }

        void AddSection(Panel parent, string ja, string en, ref int y)
        {
            y += 10;
            var bar = new Panel { Location = new Point(24, y + 2), Width = 3, Height = 14, BackColor = Color.FromArgb(45, 132, 235) };
            parent.Controls.Add(bar);
            var label = new Label
            {
                Text = T(ja, en),
                Tag = ja + "|" + en,
                Location = new Point(34, y),
                Width = 240,
                Height = 20,
                Font = new Font("Yu Gothic UI", 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(168, 174, 200)
            };
            parent.Controls.Add(label);
            y += 28;
        }

        void AddRow(Panel parent, string titleJa, string titleEn, string descJa, string descEn, Control control, ref int y)
        {
            int rowH = 50;
            bool hasDesc = !string.IsNullOrEmpty(descJa);
            var titleLabel = new Label { Text = T(titleJa, titleEn), Tag = titleJa + "|" + titleEn, Location = new Point(24, hasDesc ? y + 9 : y + 15), Width = 210, Height = 20, Font = new Font("Yu Gothic UI", 9.2f), ForeColor = Color.FromArgb(210, 214, 226) };
            parent.Controls.Add(titleLabel);
            if (hasDesc)
            {
                var descLabel = new Label { Text = T(descJa, descEn), Tag = descJa + "|" + descEn, Location = new Point(24, y + 28), Width = 210, Height = 17, Font = new Font("Yu Gothic UI", 7.8f), ForeColor = Color.FromArgb(96, 104, 120) };
                parent.Controls.Add(descLabel);
            }
            control.Location = new Point(parent.Width - 200, y + 11);
            parent.Controls.Add(control);
            var line = new Panel { Location = new Point(24, y + rowH - 1), Width = parent.Width - 48, Height = 1, BackColor = Color.FromArgb(42, 42, 52) };
            parent.Controls.Add(line);
            y += rowH;
        }

        Panel SettingsCard(int x, int y, int width, int height)
        {
            return new Panel
            {
                Location = new Point(x, y),
                Width = width,
                Height = height,
                BackColor = Color.FromArgb(20, 20, 24)
            };
        }

        void AddNumberWithColor(Panel parent, string titleJa, string titleEn, string descJa, string descEn, TextBox box, int value, ref int y, int min, int max)
        {
            StyleNumber(box, value, min, max);
            AddRow(parent, titleJa, titleEn, descJa, descEn, box, ref y);
        }

        void SetupCombo(DarkComboBox box, string value, string[] items)
        {
            box.Font = new Font("Yu Gothic UI", 9.5f);
            box.Width = 175;
            box.Items.Clear();
            box.Items.AddRange(items);
            if (box == layoutMode)                              box.SelectedIndex = string.Equals(value, "vertical",  StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            else if (box == codexMode || box == claudeMode)    box.SelectedIndex = string.Equals(value, "used",      StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            else if (box == fiveResetMode || box == weeklyResetMode) box.SelectedIndex = string.Equals(value, "relative", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            else if (box == topMost)                           box.SelectedIndex = string.Equals(value, "enabled",   StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            else if (box == showCodex || box == showClaude)   box.SelectedIndex = string.Equals(value, "enabled",   StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            else if (box == language)                          box.SelectedIndex = string.Equals(value, "en",        StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }

        void SetupNumbers()
        {
            StyleNumber(normal, settings.NormalIntervalMinutes, 1, 240);
            StyleNumber(boostDuration, settings.BoostDurationMinutes, 1, 240);
            StyleNumber(boostInterval, settings.BoostIntervalMinutes, 1, 240);
        }

        void StyleNumber(TextBox box, int value, int min, int max)
        {
            box.Text = Math.Max(min, Math.Min(max, value)).ToString();
            box.Width = 175;
            box.Height = 28;
            box.TextAlign = HorizontalAlignment.Right;
            box.BackColor = Color.FromArgb(26, 28, 44);
            box.ForeColor = Color.FromArgb(210, 214, 226);
            box.Font = new Font("Yu Gothic UI", 11f);
            box.BorderStyle = BorderStyle.FixedSingle;
        }

        static int ReadBoxInt(TextBox box, int fallback, int min, int max)
        {
            int value;
            if (!int.TryParse(box.Text.Trim(), out value)) return fallback;
            return Math.Max(min, Math.Min(max, value));
        }

        void StyleTextBox(TextBox box)
        {
            box.BackColor = Color.FromArgb(26, 28, 44);
            box.ForeColor = Color.FromArgb(210, 214, 226);
            box.Font = new Font("Yu Gothic UI", 9.5f);
            box.BorderStyle = BorderStyle.FixedSingle;
        }

        void WireLivePreview()
        {
            EventHandler apply = (s, e) =>
            {
                if (_updatingLanguage) return;
                ApplyToSettings();
                preview();
            };
            EventHandler applyLanguage = (s, e) =>
            {
                if (_updatingLanguage) return;
                _updatingLanguage = true;
                try
                {
                    ApplyToSettings();
                    bool en = string.Equals(settings.Language, "en", StringComparison.OrdinalIgnoreCase);
                    ReloadComboItems();
                    UpdateTaggedControls(this, en);
                    preview();
                }
                finally { _updatingLanguage = false; }
            };
            normal.TextChanged += apply;
            language.SelectedIndexChanged += applyLanguage;
            boostDuration.TextChanged += apply;
            boostInterval.TextChanged += apply;
            showCodex.SelectedIndexChanged  += apply;
            showClaude.SelectedIndexChanged += apply;
            layoutMode.SelectedIndexChanged += apply;
            codexMode.SelectedIndexChanged += apply;
            claudeMode.SelectedIndexChanged += apply;
            fiveResetMode.SelectedIndexChanged += apply;
            weeklyResetMode.SelectedIndexChanged += apply;
            warningPercent.TextChanged += apply;
            criticalPercent.TextChanged += apply;
            topMost.SelectedIndexChanged += apply;
        }

        void ReloadComboItems()
        {
            int layoutSel   = layoutMode.SelectedIndex;
            int codexSel    = codexMode.SelectedIndex;
            int claudeSel   = claudeMode.SelectedIndex;
            int fiveSel     = fiveResetMode.SelectedIndex;
            int weeklySel   = weeklyResetMode.SelectedIndex;
            int topMostSel  = topMost.SelectedIndex;
            int showCxSel   = showCodex.SelectedIndex;
            int showClSel   = showClaude.SelectedIndex;

            layoutMode.Items.Clear();
            layoutMode.Items.AddRange(new[] { T("横", "Wide"), T("縦", "Tall") });
            layoutMode.SelectedIndex = Math.Max(0, Math.Min(1, layoutSel));

            codexMode.Items.Clear();
            codexMode.Items.AddRange(new[] { T("残量", "Remaining"), T("使用量", "Used") });
            codexMode.SelectedIndex = Math.Max(0, Math.Min(1, codexSel));

            claudeMode.Items.Clear();
            claudeMode.Items.AddRange(new[] { T("残量", "Remaining"), T("使用量", "Used") });
            claudeMode.SelectedIndex = Math.Max(0, Math.Min(1, claudeSel));

            fiveResetMode.Items.Clear();
            fiveResetMode.Items.AddRange(new[] { T("リセット時刻", "Clock time"), T("残り時間", "Time left") });
            fiveResetMode.SelectedIndex = Math.Max(0, Math.Min(1, fiveSel));

            weeklyResetMode.Items.Clear();
            weeklyResetMode.Items.AddRange(new[] { T("リセット時刻", "Clock time"), T("残り時間", "Time left") });
            weeklyResetMode.SelectedIndex = Math.Max(0, Math.Min(1, weeklySel));

            topMost.Items.Clear();
            topMost.Items.AddRange(new[] { T("有効", "Enabled"), T("無効", "Disabled") });
            topMost.SelectedIndex = Math.Max(0, Math.Min(1, topMostSel));

            showCodex.Items.Clear();
            showCodex.Items.AddRange(new[] { T("有効", "Enabled"), T("無効", "Disabled") });
            showCodex.SelectedIndex = Math.Max(0, Math.Min(1, showCxSel));

            showClaude.Items.Clear();
            showClaude.Items.AddRange(new[] { T("有効", "Enabled"), T("無効", "Disabled") });
            showClaude.SelectedIndex = Math.Max(0, Math.Min(1, showClSel));
        }

        void UpdateTaggedControls(Control parent, bool en)
        {
            foreach (Control c in parent.Controls)
            {
                var tag = c.Tag as string;
                if (tag != null)
                {
                    int pipe = tag.IndexOf('|');
                    if (pipe >= 0)
                        c.Text = en ? tag.Substring(pipe + 1) : tag.Substring(0, pipe);
                }
                if (c.Controls.Count > 0)
                    UpdateTaggedControls(c, en);
            }
        }

        void ApplyToSettings()
        {
            settings.NormalIntervalMinutes = ReadBoxInt(normal, settings.NormalIntervalMinutes, 1, 240);
            settings.Language = language.SelectedIndex == 1 ? "en" : "ja";
            settings.BoostDurationMinutes = ReadBoxInt(boostDuration, settings.BoostDurationMinutes, 1, 240);
            settings.BoostIntervalMinutes = ReadBoxInt(boostInterval, settings.BoostIntervalMinutes, 1, 240);
            if (showCodex.SelectedIndex == 1 && showClaude.SelectedIndex == 1) showClaude.SelectedIndex = 0;
            settings.ShowCodex  = showCodex.SelectedIndex  == 0;
            settings.ShowClaude = showClaude.SelectedIndex == 0;
            settings.LayoutMode = layoutMode.SelectedIndex == 1 ? "vertical" : "horizontal";
            settings.CodexShowUsed = codexMode.SelectedIndex == 1;
            settings.ClaudeShowUsed = claudeMode.SelectedIndex == 1;
            settings.FiveHourResetMode = fiveResetMode.SelectedIndex == 1 ? "relative" : "time";
            settings.WeeklyResetMode   = weeklyResetMode.SelectedIndex == 1 ? "relative" : "time";
            int warning = ReadBoxInt(warningPercent, settings.WarningRemainingPercent, 1, 99);
            int critical = ReadBoxInt(criticalPercent, settings.CriticalRemainingPercent, 1, 99);
            settings.WarningRemainingPercent = Math.Max(critical, warning);
            settings.CriticalRemainingPercent = Math.Min(critical, settings.WarningRemainingPercent);
            settings.AlwaysOnTop = topMost.SelectedIndex == 0;
        }

        string T(string ja, string en)
        {
            return string.Equals(settings.Language, "en", StringComparison.OrdinalIgnoreCase) ? en : ja;
        }

    }

    sealed class ServiceState
    {
        public readonly string Name;
        public readonly string Url;
        public readonly Color Accent;
        public UsageData Data;
        public string Status;
        public bool IsRefreshing;
        public DateTime LastRefresh = DateTime.MinValue;
        public DateTime? BoostUntil;
        public double? DisplayedFivePct;
        public double? DisplayedWeekPct;

        public ServiceState(string name, string url, Color accent)
        {
            Name = name;
            Url = url;
            Accent = accent;
            Data = new UsageData { Name = name, Source = "starting", Status = "starting" };
            Status = "starting";
        }

        public bool BoostActive
        {
            get { return BoostUntil.HasValue && BoostUntil.Value > DateTime.Now; }
        }
    }

    sealed class UsageData
    {
        public string Name;
        public string Source;
        public string Status;
        public DateTime UpdatedAt;
        public double? FiveHourUsed;
        public double? WeeklyUsed;
        public int? FiveHourRemaining;
        public int? WeeklyRemaining;
        public string FiveHourReset;
        public string WeeklyReset;
        public bool FiveHourNotStarted;
        public bool WeeklyNotStarted;
        public bool HitLimit;

        public bool HasAnyValue()
        {
            return FiveHourUsed.HasValue || WeeklyUsed.HasValue || FiveHourRemaining.HasValue || WeeklyRemaining.HasValue;
        }

        public int? FiveHourRemainingPercent()
        {
            if (FiveHourRemaining.HasValue) return FiveHourRemaining.Value;
            if (FiveHourUsed.HasValue) return Clamp(100 - FiveHourUsed.Value);
            return null;
        }

        public int? FiveHourUsedPercent()
        {
            if (FiveHourUsed.HasValue) return Clamp(FiveHourUsed.Value);
            if (FiveHourRemaining.HasValue) return Clamp(100 - FiveHourRemaining.Value);
            return null;
        }

        public int? FiveHourDisplayPercent(bool showUsed)
        {
            return showUsed ? FiveHourUsedPercent() : FiveHourRemainingPercent();
        }

        public int? WeeklyRemainingPercent()
        {
            if (WeeklyRemaining.HasValue) return WeeklyRemaining.Value;
            if (WeeklyUsed.HasValue) return Clamp(100 - WeeklyUsed.Value);
            return null;
        }

        public int? WeeklyUsedPercent()
        {
            if (WeeklyUsed.HasValue) return Clamp(WeeklyUsed.Value);
            if (WeeklyRemaining.HasValue) return Clamp(100 - WeeklyRemaining.Value);
            return null;
        }

        public int? WeeklyDisplayPercent(bool showUsed)
        {
            return showUsed ? WeeklyUsedPercent() : WeeklyRemainingPercent();
        }

        static int Clamp(double value)
        {
            return Math.Max(0, Math.Min(100, (int)Math.Round(value)));
        }
    }

    sealed class WidgetSettings
    {
        public int Width = 760;
        public int Height = 170;
        public string Language = DefaultLanguage();
        public int NormalIntervalMinutes = 15;
        public int BoostDurationMinutes = 30;
        public int BoostIntervalMinutes = 1;
        public bool AlwaysOnTop = false;
        public bool ShowCodex = true;
        public bool ShowClaude = true;
        public string LayoutMode = "horizontal";
        public bool CodexShowUsed = false;
        public bool ClaudeShowUsed = false;
        public string FiveHourResetMode = "relative";
        public string WeeklyResetMode = "time";
        public int WarningRemainingPercent = 50;
        public int CriticalRemainingPercent = 30;

        static string SettingsPath
        {
            get { return Path.Combine(Application.StartupPath, "settings.json"); }
        }

        static string DefaultLanguage()
        {
            return string.Equals(System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "ja", StringComparison.OrdinalIgnoreCase)
                ? "ja"
                : "en";
        }

        public static WidgetSettings Load()
        {
            var s = new WidgetSettings();
            try
            {
                if (!File.Exists(SettingsPath))
                {
                    s.Save();
                    return s;
                }
                string json = File.ReadAllText(SettingsPath);
                s.Width = ReadInt(json, "width", s.Width);
                s.Height = ReadInt(json, "height", s.Height);
                s.Language = ReadString(json, "language", s.Language);
                s.NormalIntervalMinutes = ReadInt(json, "normalIntervalMinutes", s.NormalIntervalMinutes);
                s.BoostDurationMinutes = ReadInt(json, "boostDurationMinutes", s.BoostDurationMinutes);
                s.BoostIntervalMinutes = ReadInt(json, "boostIntervalMinutes", s.BoostIntervalMinutes);
                s.AlwaysOnTop = ReadBool(json, "alwaysOnTop", s.AlwaysOnTop);
                s.ShowCodex = ReadBool(json, "showCodex", s.ShowCodex);
                s.ShowClaude = ReadBool(json, "showClaude", s.ShowClaude);
                s.LayoutMode = NormalizeLayoutMode(ReadString(json, "layoutMode", s.LayoutMode));
                s.CodexShowUsed = ReadBool(json, "codexShowUsed", s.CodexShowUsed);
                s.ClaudeShowUsed = ReadBool(json, "claudeShowUsed", s.ClaudeShowUsed);
                s.FiveHourResetMode = NormalizeResetMode(ReadString(json, "fiveHourResetMode", s.FiveHourResetMode));
                s.WeeklyResetMode = NormalizeResetMode(ReadString(json, "weeklyResetMode", s.WeeklyResetMode));
                s.WarningRemainingPercent = ReadInt(json, "warningRemainingPercent", s.WarningRemainingPercent);
                s.CriticalRemainingPercent = ReadInt(json, "criticalRemainingPercent", s.CriticalRemainingPercent);
            }
            catch { }
            return s;
        }

        public WidgetSettings Clone()
        {
            var copy = new WidgetSettings();
            copy.CopyFrom(this);
            return copy;
        }

        public void CopyFrom(WidgetSettings other)
        {
            Width = other.Width;
            Height = other.Height;
            Language = other.Language;
            NormalIntervalMinutes = other.NormalIntervalMinutes;
            BoostDurationMinutes = other.BoostDurationMinutes;
            BoostIntervalMinutes = other.BoostIntervalMinutes;
            AlwaysOnTop = other.AlwaysOnTop;
            ShowCodex = other.ShowCodex;
            ShowClaude = other.ShowClaude;
            LayoutMode = other.LayoutMode;
            CodexShowUsed = other.CodexShowUsed;
            ClaudeShowUsed = other.ClaudeShowUsed;
            FiveHourResetMode = other.FiveHourResetMode;
            WeeklyResetMode = other.WeeklyResetMode;
            WarningRemainingPercent = other.WarningRemainingPercent;
            CriticalRemainingPercent = other.CriticalRemainingPercent;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Application.StartupPath);
                File.WriteAllText(SettingsPath,
                    "{\r\n" +
                    "  \"width\": " + Width + ",\r\n" +
                    "  \"height\": " + Height + ",\r\n" +
                    "  \"language\": \"" + Escape(Language) + "\",\r\n" +
                    "  \"normalIntervalMinutes\": " + NormalIntervalMinutes + ",\r\n" +
                    "  \"boostDurationMinutes\": " + BoostDurationMinutes + ",\r\n" +
                    "  \"boostIntervalMinutes\": " + BoostIntervalMinutes + ",\r\n" +
                    "  \"alwaysOnTop\": " + (AlwaysOnTop ? "true" : "false") + ",\r\n" +
                    "  \"showCodex\": " + (ShowCodex ? "true" : "false") + ",\r\n" +
                    "  \"showClaude\": " + (ShowClaude ? "true" : "false") + ",\r\n" +
                    "  \"layoutMode\": \"" + Escape(LayoutMode) + "\",\r\n" +
                    "  \"codexShowUsed\": " + (CodexShowUsed ? "true" : "false") + ",\r\n" +
                    "  \"claudeShowUsed\": " + (ClaudeShowUsed ? "true" : "false") + ",\r\n" +
                    "  \"fiveHourResetMode\": \"" + Escape(FiveHourResetMode) + "\",\r\n" +
                    "  \"weeklyResetMode\": \"" + Escape(WeeklyResetMode) + "\",\r\n" +
                    "  \"warningRemainingPercent\": " + WarningRemainingPercent + ",\r\n" +
                    "  \"criticalRemainingPercent\": " + CriticalRemainingPercent + "\r\n" +
                    "}\r\n");
            }
            catch { }
        }

        static int ReadInt(string json, string key, int fallback)
        {
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(\\d+)");
            if (!m.Success) return fallback;
            int value;
            return int.TryParse(m.Groups[1].Value, out value) ? value : fallback;
        }

        static bool ReadBool(string json, string key, bool fallback)
        {
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
            if (!m.Success) return fallback;
            return string.Equals(m.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
        }

        static string ReadString(string json, string key, string fallback)
        {
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"");
            if (!m.Success) return fallback;
            return m.Groups[1].Value;
        }

        static string NormalizeResetMode(string value)
        {
            return string.Equals(value, "time", StringComparison.OrdinalIgnoreCase) ? "time" : "relative";
        }

        static string NormalizeLayoutMode(string value)
        {
            return string.Equals(value, "vertical", StringComparison.OrdinalIgnoreCase) ? "vertical" : "horizontal";
        }

        static string Escape(string value)
        {
            return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }

    sealed class DarkComboBox : ComboBox
    {
        static readonly Color BgColor     = Color.FromArgb(26, 28, 38);
        static readonly Color TextColor   = Color.FromArgb(210, 214, 226);
        static readonly Color BorderColor = Color.FromArgb(48, 58, 80);
        static readonly Color ArrowBg    = Color.FromArgb(32, 36, 52);
        static readonly Color ArrowFg    = Color.FromArgb(120, 140, 175);
        static readonly Color SelColor   = Color.FromArgb(30, 88, 160);

        [DllImport("uxtheme.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string app, string id);

        public DarkComboBox()
        {
            DrawMode      = DrawMode.OwnerDrawFixed;
            DropDownStyle = ComboBoxStyle.DropDownList;
            FlatStyle     = FlatStyle.Flat;
            ItemHeight    = 24;
            BackColor     = BgColor;
            ForeColor     = TextColor;
            DrawItem     += OnDrawItem;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetWindowTheme(Handle, "", "");
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x14) { m.Result = (IntPtr)1; return; } // WM_ERASEBKGND: suppress
            base.WndProc(ref m);
            if (m.Msg == 0x07 || m.Msg == 0x08 || m.Msg == 0x0F || m.Msg == 0x200 || m.Msg == 0x2A3)
                using (var g = Graphics.FromHwnd(Handle))
                    PaintFace(g);
        }

        void PaintFace(Graphics g)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int w = Width, h = Height, aw = 26;

            using (var b = new SolidBrush(BgColor))
                g.FillRectangle(b, 0, 0, w, h);

            if (SelectedIndex >= 0)
            {
                string text = Items[SelectedIndex].ToString();
                TextRenderer.DrawText(g, text, Font,
                    new Point(10, (h - Font.Height) / 2 - 1), TextColor);
            }

            int ax = w - aw - 1;
            using (var b = new SolidBrush(ArrowBg))
                g.FillRectangle(b, ax + 1, 1, aw - 1, h - 2);
            using (var p = new Pen(BorderColor))
                g.DrawLine(p, ax, 1, ax, h - 2);

            int cx = ax + aw / 2, cy = h / 2 + 1;
            var pts = new[] { new Point(cx - 4, cy - 3), new Point(cx + 4, cy - 3), new Point(cx, cy + 2) };
            using (var b = new SolidBrush(ArrowFg))
                g.FillPolygon(b, pts);

            using (var p = new Pen(BorderColor))
                g.DrawRectangle(p, 0, 0, w - 1, h - 1);
        }

        static void OnDrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var box = (ComboBox)sender;
            bool sel = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            using (var bg = new SolidBrush(sel ? SelColor : Color.FromArgb(22, 24, 34)))
                e.Graphics.FillRectangle(bg, e.Bounds);
            TextRenderer.DrawText(e.Graphics, box.Items[e.Index].ToString(), box.Font,
                new Point(e.Bounds.X + 10, e.Bounds.Y + (e.Bounds.Height - box.Font.Height) / 2),
                TextColor);
        }
    }

    sealed class DarkTextBox : TextBox
    {
        static readonly Color BorderColor = Color.FromArgb(48, 58, 80);

        [DllImport("user32.dll")] static extern IntPtr GetWindowDC(IntPtr h);
        [DllImport("user32.dll")] static extern int    ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("user32.dll")] static extern bool   GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, int wParam, int lParam);
        [DllImport("uxtheme.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string app, string id);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        public DarkTextBox() { AutoSize = false; }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetWindowTheme(Handle, "", "");
            SendMessage(Handle, 0xD3, 3, (8 << 16) | 4);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x85) { DrawBorder(); return; }              // WM_NCPAINT: skip system grey border
            if (m.Msg == 0x86) { m.Result = (IntPtr)1; DrawBorder(); return; } // WM_NCACTIVATE: suppress reframe
            base.WndProc(ref m);
            if (m.Msg == 0x0F) DrawBorder(); // WM_PAINT: re-apply after client repaint
        }

        void DrawBorder()
        {
            RECT r;
            GetWindowRect(Handle, out r);
            IntPtr dc = GetWindowDC(Handle);
            if (dc == IntPtr.Zero) return;
            try
            {
                using (var g = Graphics.FromHdc(dc))
                using (var p = new Pen(BorderColor))
                    g.DrawRectangle(p, 0, 0, r.Right - r.Left - 1, r.Bottom - r.Top - 1);
            }
            finally { ReleaseDC(Handle, dc); }
        }
    }

    sealed class RoundButton : Button
    {
        public int CornerRadius = 8;
        public Color HoverBackColor = Color.Empty;
        public Color PressedBackColor = Color.Empty;
        public Color BorderColorNormal = Color.Empty;
        bool _hover;
        bool _pressed;

        public RoundButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                   | ControlStyles.SupportsTransparentBackColor, true);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; base.OnMouseEnter(e); Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; base.OnMouseLeave(e); Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; base.OnMouseDown(e); Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; base.OnMouseUp(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            int d = Math.Max(1, CornerRadius * 2);
            int w = Width - 1, h = Height - 1;
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                path.AddArc(0, 0, d, d, 180, 90);
                path.AddArc(w - d, 0, d, d, 270, 90);
                path.AddArc(w - d, h - d, d, d, 0, 90);
                path.AddArc(0, h - d, d, d, 90, 90);
                path.CloseFigure();

                Color fill = BackColor;
                if (_pressed && PressedBackColor != Color.Empty) fill = PressedBackColor;
                else if (_hover && HoverBackColor != Color.Empty) fill = HoverBackColor;

                Color topFill = _pressed ? fill : Color.FromArgb(
                    Math.Min(255, fill.R + 18),
                    Math.Min(255, fill.G + 18),
                    Math.Min(255, fill.B + 22));
                using (var grad = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new Rectangle(0, 0, w + 1, h + 1), topFill, fill, 90f))
                    g.FillPath(grad, path);
                if (BorderColorNormal != Color.Empty)
                    using (var pen = new Pen(BorderColorNormal, 1f))
                        g.DrawPath(pen, path);
                if (!_pressed)
                    using (var hl = new Pen(Color.FromArgb(50, 255, 255, 255), 1f))
                        g.DrawLine(hl, CornerRadius / 2, 2, w - CornerRadius / 2, 2);
                using (var sh = new Pen(Color.FromArgb(24, 0, 0, 0), 1f))
                    g.DrawLine(sh, CornerRadius / 2, h - 1, w - CornerRadius / 2, h - 1);
            }

            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                      | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, ForeColor, flags);
        }
    }

    sealed class DarkScrollContainer : Panel
    {
        public readonly Panel Content;
        readonly Panel track  = new Panel();
        readonly Panel thumb  = new Panel();

        int scrollY;
        int contentH;
        bool dragging;
        int  dragStartScreenY;
        int  dragStartScrollY;

        const int TrackW    = 7;
        const int MinThumbH = 24;

        public DarkScrollContainer()
        {
            Content = new Panel { Location = Point.Empty, BackColor = Color.Transparent };
            track.BackColor = Color.FromArgb(20, 20, 26);
            thumb.BackColor = Color.FromArgb(52, 60, 80);
            thumb.Width     = TrackW - 2;

            Controls.Add(Content);
            Controls.Add(track);
            track.Controls.Add(thumb);

            thumb.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                dragging = true;
                dragStartScreenY = thumb.PointToScreen(e.Location).Y;
                dragStartScrollY = scrollY;
            };
            thumb.MouseMove += (s, e) =>
            {
                if (!dragging) return;
                int dy = thumb.PointToScreen(e.Location).Y - dragStartScreenY;
                int trackRange = Math.Max(1, Height - thumb.Height);
                DoScroll(dragStartScrollY + (int)((double)dy * (contentH - Height) / trackRange));
            };
            thumb.MouseUp   += (s, e) => dragging = false;
            track.MouseDown += (s, e) =>
            {
                if (e.Y < thumb.Top)    DoScroll(scrollY - Height);
                else if (e.Y > thumb.Bottom) DoScroll(scrollY + Height);
            };
            MouseWheel += (s, e) => DoScroll(scrollY - e.Delta / 3);
        }

        public void SetContentHeight(int h)
        {
            contentH = h;
            UpdateLayout();
        }

        public void AttachWheelToChildren()
        {
            AttachWheel(this);
        }

        void AttachWheel(Control c)
        {
            foreach (Control child in c.Controls)
            {
                if (child == track) continue;
                child.MouseWheel += (s, e) => DoScroll(scrollY - e.Delta / 3);
                AttachWheel(child);
            }
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); UpdateLayout(); }

        void DoScroll(int target)
        {
            int maxScroll = Math.Max(0, contentH - Height);
            scrollY = Math.Max(0, Math.Min(target, maxScroll));
            UpdateLayout();
        }

        void UpdateLayout()
        {
            if (Width <= 0 || Height <= 0) return;
            bool need = contentH > Height;
            track.Visible = need;
            if (need)
            {
                Content.Size = new Size(Width - TrackW - 1, contentH);
                track.SetBounds(Width - TrackW, 0, TrackW, Height);
                double ratio  = (double)Height / contentH;
                int thumbH    = Math.Max(MinThumbH, (int)(Height * ratio));
                double frac   = contentH > Height ? (double)scrollY / (contentH - Height) : 0;
                int thumbTop  = (int)(frac * (Height - thumbH));
                thumb.SetBounds(1, thumbTop, TrackW - 2, thumbH);
            }
            else
            {
                scrollY = 0;
                Content.Size = new Size(Width, contentH);
            }
            Content.Location = new Point(0, -scrollY);
        }
    }
}
