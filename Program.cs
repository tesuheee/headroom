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

namespace AiUsageWebView2
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
        const int WmNcLButtonDown = 0xA1;
        const int HtCaption = 0x2;

        readonly Timer paintTimer = new Timer();
        readonly Timer schedulerTimer = new Timer();
        readonly WebView2 claudeView = new WebView2();
        readonly WebView2 codexView = new WebView2();
        readonly Dictionary<string, Rectangle> hits = new Dictionary<string, Rectangle>();
        readonly WidgetSettings settings = WidgetSettings.Load();
        readonly ToolTip toolTip = new ToolTip { InitialDelay = 300, ReshowDelay = 100, ShowAlways = true };

        CoreWebView2Environment webEnv;
        ServiceState claude = new ServiceState("Claude", ClaudeUrl, Color.FromArgb(45, 132, 235));
        ServiceState codex = new ServiceState("Codex", CodexUrl, Color.FromArgb(26, 177, 92));
        bool resizing;
        Point resizeStartPoint;
        Size resizeStartSize;
        string hoverKey = "";
        int spinnerFrame;
        bool English
        {
            get { return string.Equals(settings.Language, "en", StringComparison.OrdinalIgnoreCase); }
        }

        public UsageForm()
        {
            Text = "AI Usage";
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
            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch { }

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

            paintTimer.Interval = 250;
            paintTimer.Tick += (s, e) =>
            {
                spinnerFrame = (spinnerFrame + 1) % 4;
                Invalidate();
            };
            paintTimer.Start();

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
            string userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AiUsageWebView2", "WebView2Profile");
            Directory.CreateDirectory(userData);
            webEnv = await CoreWebView2Environment.CreateAsync(null, userData);
            await claudeView.EnsureCoreWebView2Async(webEnv);
            await codexView.EnsureCoreWebView2Async(webEnv);
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
            if (service.LastRefresh == DateTime.MinValue || DateTime.Now - service.LastRefresh >= TimeSpan.FromMinutes(Math.Max(1, minutes)))
                await RefreshServiceAsync(service, view, false);
        }

        int RefreshIntervalMinutes(ServiceState service)
        {
            if (service.BoostUntil.HasValue) return settings.BoostIntervalMinutes;
            TimeSpan untilReset;
            if (TryGetResetRemaining(service.Data.FiveHourReset, out untilReset) &&
                untilReset.TotalMinutes > 0 &&
                untilReset.TotalMinutes <= settings.FinalRefreshWindowMinutes)
                return settings.FinalRefreshIntervalMinutes;
            return settings.NormalIntervalMinutes;
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
            login.FormClosed += async (s, e) =>
            {
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
                @"上限に達しました|上限に近づいています|制限に達しました|You'?ve reached.{0,20}limit|limit reached|approaching.{0,20}limit|at capacity",
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
            if (data.FiveHourNotStarted || LooksLikeInactiveFiveHourWindow(data))
            {
                data.FiveHourNotStarted = true;
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

        static bool LooksLikeInactiveFiveHourWindow(UsageData data)
        {
            TimeSpan remaining;
            if (!TryGetResetRemaining(data.FiveHourReset, out remaining)) return false;
            if (remaining.TotalHours <= 5.5) return false;
            if (data.FiveHourRemaining.HasValue && data.FiveHourRemaining.Value >= 99) return true;
            if (data.FiveHourUsed.HasValue && data.FiveHourUsed.Value <= 1) return true;
            return false;
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
                string tip = TooltipText(key);
                if (tip.Length > 0)
                {
                    Rectangle hr;
                    if (hits.TryGetValue(key, out hr))
                        toolTip.Show(tip, this, hr.X + hr.Width / 2, hr.Y + hr.Height + 4, 3000);
                    else
                        toolTip.Show(tip, this, e.X + 12, e.Y + 18, 3000);
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

            int y = 8;
            int gap = 10;
            int sideRail = 16;
            var visible = VisibleServices();
            int railLeft = ClientSize.Width - sideRail - 18;
            int contentW = Math.Max(240, railLeft - 10);
            int contentH = Math.Max(110, ClientSize.Height - y - 8);
            if (visible.Count == 1)
            {
                DrawService(g, visible[0].Item1, 10, y, contentW, contentH, visible[0].Item2);
            }
            else if (string.Equals(settings.LayoutMode, "vertical", StringComparison.OrdinalIgnoreCase))
            {
                int cardH = Math.Max(110, (contentH - gap) / 2);
                DrawService(g, visible[0].Item1, 10, y, contentW, cardH, visible[0].Item2);
                DrawService(g, visible[1].Item1, 10, y + cardH + gap, contentW, cardH, visible[1].Item2);
            }
            else
            {
                int cardW = Math.Max(240, (contentW - gap) / 2);
                DrawService(g, visible[0].Item1, 10, y, cardW, contentH, visible[0].Item2);
                DrawService(g, visible[1].Item1, 10 + cardW + gap, y, cardW, contentH, visible[1].Item2);
            }
            DrawSideRail(g);
            DrawResizeGrip(g);
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
            MinimumSize = vertical && twoServices ? new Size(360, 270) : new Size(640, 136);
            if (Width < MinimumSize.Width) Width = MinimumSize.Width;
            if (Height < MinimumSize.Height) Height = MinimumSize.Height;
        }

        void DrawSideRail(Graphics g)
        {
            int x = ClientSize.Width - 24;
            int closeY = 12;
            int pinY = 46;
            int settingsY = 80;
            hits["close"] = new Rectangle(x - 6, closeY - 6, 28, 28);
            hits["pin"] = new Rectangle(x - 6, pinY - 6, 28, 28);
            hits["settings"] = new Rectangle(x - 6, settingsY - 6, 28, 28);

            DrawIconButton(g, "close", x, closeY, Color.FromArgb(160, 160, 165), DrawCloseIcon);
            DrawIconButton(g, "pin", x, pinY, settings.AlwaysOnTop ? Color.FromArgb(100, 180, 255) : Color.FromArgb(100, 100, 105), DrawPinIcon);
            DrawIconButton(g, "settings", x, settingsY, Color.FromArgb(130, 130, 135), DrawGearIcon);
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
            bool exhausted = state.Data.HitLimit || (fiveRemain.HasValue && fiveRemain.Value <= 0) || (weekRemain.HasValue && weekRemain.Value <= 0);
            bool stale = state.LastRefresh != DateTime.MinValue &&
                DateTime.Now - state.LastRefresh > TimeSpan.FromMinutes(Math.Max(2, settings.NormalIntervalMinutes * 2));
            Color accent = exhausted ? Color.FromArgb(220, 77, 77) : state.Accent;

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
            using (var dotBrush = new SolidBrush(accent))
                g.FillEllipse(dotBrush, x + 16, y + 17, 8, 8);

            using (var title = new Font("Segoe UI", 13f, FontStyle.Bold))
            using (var label = new Font("Segoe UI", settings.LabelFontSize, FontStyle.Regular))
            using (var reset = new Font("Segoe UI", settings.ResetFontSize, FontStyle.Regular))
            using (var num = new Font("Segoe UI", settings.PercentFontSize, FontStyle.Bold))
            using (var white = new SolidBrush(Color.FromArgb(240, 242, 245)))
            using (var muted = new SolidBrush(Color.FromArgb(170, 175, 185)))
            using (var dim = new SolidBrush(Color.FromArgb(140, 145, 155)))
            {
                g.DrawString(state.Name, title, white, x + 30, y + 10);
                if (stale)
                    DrawBadge(g, T("古い", "Stale"), x + 100, y + 14, Color.FromArgb(110, 85, 20), Color.FromArgb(180, 150, 50));
                if (exhausted)
                {
                    DrawBadge(g, T("上限", "Limit"), x + 100, y + 14, Color.FromArgb(100, 35, 35), Color.FromArgb(220, 100, 100));
                    string limitReset = LimitResetText(state.Data, fiveRemain, weekRemain, English);
                    if (limitReset.Length > 0)
                    {
                        using (var badgeText = new Font("Segoe UI", 8.5f, FontStyle.Regular))
                            g.DrawString(limitReset, badgeText, dim, x + 140, y + 16);
                    }
                }
                DrawCardControls(g, state, x, y, w, keyPrefix);

                if (!state.Data.HasAnyValue())
                {
                    g.DrawString("--", num, white, x + 58, y + 54);
                    string status = state.IsRefreshing ? T("更新中", "Updating") : StatusText(state.Status ?? state.Data.Status ?? "no_data");
                    g.DrawString(status, label, muted, x + 20, y + 88);
                    DrawLoginButton(g, x + w - 100, y + h - 40, keyPrefix + "-login");
                    return;
                }

                int contentTop = y + Math.Max(44, (int)Math.Ceiling(settings.PercentFontSize + 24));
                int contentBottom = y + h - 10;
                int rowHeight = Math.Max(32, (int)Math.Ceiling(Math.Max(settings.PercentFontSize * 1.55, settings.LabelFontSize + settings.ResetFontSize + 14)));
                int totalRows = rowHeight * 2;
                int free = Math.Max(0, contentBottom - contentTop - totalRows);
                int topPad = free / 2;
                int rowGap = Math.Max(2, Math.Min(10, free / 4));
                int firstY = contentTop + topPad;
                int secondY = firstY + rowHeight + rowGap;
                DrawRow(g, T("5時間", "5h"), state.Data.FiveHourDisplayPercent(showUsed), showUsed, false, state.Data.FiveHourReset, state.Data.FiveHourNotStarted, settings.FiveHourResetMode, x, firstY, w, accent, label, reset, num, white, muted, dim);
                DrawRow(g, T("週", "Week"), state.Data.WeeklyDisplayPercent(showUsed), showUsed, true, state.Data.WeeklyReset, state.Data.WeeklyNotStarted, settings.WeeklyResetMode, x, secondY, w, accent, label, reset, num, white, muted, dim);
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
            if (TryParseResetTarget(cleaned, out target))
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

        void DrawRow(Graphics g, string label, int? pct, bool showUsed, bool weekly, string resetText, bool notStarted, string resetMode, int x, int y, int w, Color accent, Font labelFont, Font resetFont, Font numFont, Brush white, Brush muted, Brush dim)
        {
            bool empty = !pct.HasValue;
            bool limit = pct.HasValue && (showUsed ? 100 - pct.Value : pct.Value) <= settings.CriticalRemainingPercent;
            bool warning = pct.HasValue && !limit && (showUsed ? 100 - pct.Value : pct.Value) <= settings.WarningRemainingPercent;
            Color rowColor = RowColor(pct, showUsed, accent);
            Color numColor = limit ? Color.FromArgb(255, 130, 130) : (warning ? Color.FromArgb(245, 200, 100) : Color.FromArgb(240, 242, 248));
            using (var pctBrush = new SolidBrush(numColor))
            {
            int labelX = x + 10;
            int modeX = labelX + Math.Max(32, (int)Math.Ceiling(g.MeasureString("5時間", labelFont).Width)) + 4;
            string modeText = showUsed ? T("使用", "Used") : T("残り", "Left");
            int percentX = modeX + Math.Max(28, (int)Math.Ceiling(g.MeasureString(modeText, labelFont).Width)) + 4;
            int labelY = y + Math.Max(0, (int)Math.Round((numFont.Size - labelFont.Size) / 2.0));
            g.DrawString(label, labelFont, muted, labelX, labelY);
            g.DrawString(modeText, labelFont, dim, modeX, labelY);
            g.DrawString(empty ? "--" : pct.Value + "%", numFont, pctBrush, percentX, y - 4);

            int pctWidth = Math.Max(44, (int)Math.Ceiling(g.MeasureString("100%", numFont).Width));
            int barX = percentX + pctWidth + 6;
            int barY = y + Math.Max(5, (int)Math.Round(settings.PercentFontSize * 0.42));
            int barW = Math.Max(70, w - (barX - x) - 10);
            DrawBar(g, barX, barY, barW, 9, pct, rowColor);

            string reset = notStarted ? T("未開始", "Not started") : ResetText(resetText, resetMode, English, weekly);
            if (!string.IsNullOrEmpty(reset))
                g.DrawString(reset, resetFont, dim, barX, y + Math.Max(18, (int)Math.Round(settings.PercentFontSize * 1.0)));
            }
        }

        Color RowColor(int? pct, bool showUsed, Color normal)
        {
            if (!pct.HasValue) return normal;
            int remaining = showUsed ? 100 - pct.Value : pct.Value;
            if (remaining <= settings.CriticalRemainingPercent)
                return settings.ParseColor(settings.CriticalColor, Color.FromArgb(224, 73, 73));
            if (remaining <= settings.WarningRemainingPercent)
                return settings.ParseColor(settings.WarningColor, Color.FromArgb(232, 169, 36));
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
            if (TryParseResetTarget(cleaned, out target))
            {
                if (!weekly && !IsPlausibleFiveHourReset(target)) return "";
                if (preferAbsolute) return (english ? "Reset " : "リセット ") + FormatResetTime(target, english);
                return (english ? "Reset in " : "リセットまで ") + FormatDuration(target - DateTime.Now, english);
            }

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
            if (!weekly && span.TotalHours > 5.5) return "";
            if (preferAbsolute) return (english ? "Reset " : "リセット ") + FormatResetTime(DateTime.Now.Add(span), english);
            return (english ? "Reset in " : "リセットまで ") + FormatDuration(span, english);
        }

        static bool IsPlausibleFiveHourReset(DateTime target)
        {
            var remaining = target - DateTime.Now;
            return remaining.TotalMinutes >= -1 && remaining.TotalHours <= 5.5;
        }

        static bool TryParseResetTarget(string text, out DateTime target)
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
                if (target < now.AddMinutes(-1)) target = target.AddDays(1);
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
            using (var path = RoundRect(x, y, 38, 20, 10))
            {
                using (var bg = new SolidBrush(bgColor))
                    g.FillPath(bg, path);
                using (var border = new Pen(Color.FromArgb(40, textColor.R, textColor.G, textColor.B), 0.6f))
                    g.DrawPath(border, path);
            }
            using (var f = new Font("Segoe UI", 8.2f, FontStyle.Bold))
            using (var b = new SolidBrush(textColor))
                g.DrawString(text, f, b, x + 7, y + 3);
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
            Color boltColor = on ? Color.FromArgb(255, 220, 60) : (hover ? Color.FromArgb(210, 215, 220) : Color.FromArgb(160, 165, 172));
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

        static void DrawBar(Graphics g, int x, int y, int w, int h, int? pct, Color accent)
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
            int fillW = Math.Max(pct.Value <= 0 ? 0 : 6, (int)Math.Round(w * Math.Max(0, Math.Min(100, pct.Value)) / 100.0));
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
            using (var dlg = new SettingsForm(settings, () =>
            {
                ApplyLayoutMinimumSize();
                TopMost = settings.AlwaysOnTop;
                Invalidate();
            }))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    ApplyLayoutMinimumSize();
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
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ai-usage-widget");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name), text ?? "");
        }
    }

    sealed class SettingsForm : Form
    {
        readonly WidgetSettings settings;
        readonly WidgetSettings original;
        readonly Action preview;
        readonly RadioButton languageJa = new RadioButton();
        readonly RadioButton languageEn = new RadioButton();
        readonly NumericUpDown normal = new NumericUpDown();
        readonly NumericUpDown boostDuration = new NumericUpDown();
        readonly NumericUpDown boostInterval = new NumericUpDown();
        readonly NumericUpDown finalWindow = new NumericUpDown();
        readonly NumericUpDown finalInterval = new NumericUpDown();
        readonly CheckBox topMost = new CheckBox();
        readonly CheckBox showCodex = new CheckBox();
        readonly CheckBox showClaude = new CheckBox();
        readonly RadioButton layoutHorizontal = new RadioButton();
        readonly RadioButton layoutVertical = new RadioButton();
        readonly RadioButton codexRemaining = new RadioButton();
        readonly RadioButton codexUsed = new RadioButton();
        readonly RadioButton claudeRemaining = new RadioButton();
        readonly RadioButton claudeUsed = new RadioButton();
        readonly RadioButton fiveResetRelative = new RadioButton();
        readonly RadioButton fiveResetTime = new RadioButton();
        readonly RadioButton weeklyResetRelative = new RadioButton();
        readonly RadioButton weeklyResetTime = new RadioButton();
        readonly NumericUpDown labelSize = new NumericUpDown();
        readonly NumericUpDown percentSize = new NumericUpDown();
        readonly NumericUpDown resetSize = new NumericUpDown();
        readonly NumericUpDown warningPercent = new NumericUpDown();
        readonly NumericUpDown criticalPercent = new NumericUpDown();
        readonly TextBox warningColor = new TextBox();
        readonly TextBox criticalColor = new TextBox();

        public SettingsForm(WidgetSettings settings, Action preview)
        {
            this.settings = settings;
            this.original = settings.Clone();
            this.preview = preview;
            Text = T("設定", "Settings");
            Width = 880;
            Height = 620;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.FromArgb(24, 24, 26);
            ForeColor = Color.WhiteSmoke;
            Font = new Font("Segoe UI", 10f);

            int colL = 24;
            int colR = 450;
            int row = 42;
            int startY = 20;

            // Left column: Timing & General
            AddSectionLabel(T("更新設定", "Refresh"), colL, startY - 2);
            AddNumber(T("通常（分）", "Normal (min)"), normal, settings.NormalIntervalMinutes, colL, startY + 22);
            AddNumber(T("ブースト時間（分）", "Boost duration (min)"), boostDuration, settings.BoostDurationMinutes, colL, startY + 22 + row);
            AddNumber(T("ブースト更新（分）", "Boost refresh (min)"), boostInterval, settings.BoostIntervalMinutes, colL, startY + 22 + row * 2);
            AddNumber(T("直前更新開始（分）", "Final window (min)"), finalWindow, settings.FinalRefreshWindowMinutes, colL, startY + 22 + row * 3, 1, 120);
            AddNumber(T("直前更新間隔（分）", "Final interval (min)"), finalInterval, settings.FinalRefreshIntervalMinutes, colL, startY + 22 + row * 4, 1, 30);

            AddSectionLabel(T("表示対象", "Services"), colL, startY + 22 + row * 5 + 14);
            AddToggle(T("Codexを表示", "Show Codex"), showCodex, settings.ShowCodex, colL, startY + 22 + row * 5 + 42);
            AddToggle(T("Claudeを表示", "Show Claude"), showClaude, settings.ShowClaude, colL + 150, startY + 22 + row * 5 + 42);

            AddSectionLabel(T("一般", "General"), colL, startY + 22 + row * 6 + 22);
            AddSegment(T("言語", "Language"), languageJa, languageEn, "日本語", "English", settings.Language, "ja", "en", colL, startY + 22 + row * 6 + 46);

            topMost.Text = T("常に最前面に固定", "Always on top");
            topMost.Checked = settings.AlwaysOnTop;
            topMost.Location = new Point(colL + 4, startY + 22 + row * 6 + 46 + row);
            topMost.Width = 240;
            topMost.FlatStyle = FlatStyle.Flat;
            topMost.ForeColor = Color.FromArgb(210, 214, 222);
            Controls.Add(topMost);

            // Right column: Display
            AddSectionLabel(T("表示設定", "Display"), colR, startY - 2);
            AddSegment(T("配置", "Layout"), layoutHorizontal, layoutVertical, T("横", "Wide"), T("縦", "Tall"), settings.LayoutMode, "horizontal", "vertical", colR, startY + 22);
            AddSegment("Codex " + T("表示", "display"), codexRemaining, codexUsed, T("残量", "Left"), T("使用量", "Used"), settings.CodexShowUsed ? "used" : "remaining", "remaining", "used", colR, startY + 22 + row);
            AddSegment("Claude " + T("表示", "display"), claudeRemaining, claudeUsed, T("残量", "Left"), T("使用量", "Used"), settings.ClaudeShowUsed ? "used" : "remaining", "remaining", "used", colR, startY + 22 + row * 2);
            AddNumber(T("ラベル文字", "Label text"), labelSize, (int)Math.Round(settings.LabelFontSize), colR, startY + 22 + row * 3, 6, 32);
            AddNumber(T("パーセント文字", "Percent text"), percentSize, (int)Math.Round(settings.PercentFontSize), colR, startY + 22 + row * 4, 8, 42);
            AddNumber(T("リセット文字", "Reset text"), resetSize, (int)Math.Round(settings.ResetFontSize), colR, startY + 22 + row * 5, 6, 32);
            AddSegment(T("5時間リセット", "5h reset"), fiveResetRelative, fiveResetTime, T("残り時間", "Time left"), T("時刻", "Clock"), settings.FiveHourResetMode, "relative", "time", colR, startY + 22 + row * 6);
            AddSegment(T("週リセット", "Weekly reset"), weeklyResetRelative, weeklyResetTime, T("残り時間", "Time left"), T("時刻", "Clock"), settings.WeeklyResetMode, "relative", "time", colR, startY + 22 + row * 7);

            AddSectionLabel(T("しきい値", "Thresholds"), colR, startY + 22 + row * 8 + 14);
            AddNumberWithColor(T("黄色（残量%）", "Yellow (left %)"), warningPercent, settings.WarningRemainingPercent, warningColor, settings.WarningColor, colR, startY + 22 + row * 8 + 38, 1, 99);
            AddNumberWithColor(T("赤（残量%）", "Red (left %)"), criticalPercent, settings.CriticalRemainingPercent, criticalColor, settings.CriticalColor, colR, startY + 22 + row * 8 + 38 + row, 1, 99);

            // Bottom buttons
            int btnY = Height - 78;
            var ok = new Button { Text = T("保存", "Save"), DialogResult = DialogResult.OK, Location = new Point(Width - 220, btnY), Width = 90, Height = 34, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(45, 95, 180), ForeColor = Color.White, Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
            ok.FlatAppearance.BorderColor = Color.FromArgb(60, 120, 210);
            ok.FlatAppearance.MouseOverBackColor = Color.FromArgb(55, 110, 200);
            var cancel = new Button { Text = T("キャンセル", "Cancel"), DialogResult = DialogResult.Cancel, Location = new Point(Width - 118, btnY), Width = 96, Height = 34, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(48, 48, 54), ForeColor = Color.FromArgb(200, 205, 215), Font = new Font("Segoe UI", 10f) };
            cancel.FlatAppearance.BorderColor = Color.FromArgb(65, 65, 72);
            cancel.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 60, 68);
            ok.Click += (s, e) =>
            {
                ApplyToSettings();
            };
            Controls.Add(ok);
            Controls.Add(cancel);
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

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            using (var pen = new Pen(Color.FromArgb(42, 42, 50)))
            {
                int midX = 434;
                g.DrawLine(pen, midX, 16, midX, Height - 88);
                g.DrawLine(pen, 24, Height - 86, Width - 40, Height - 86);
            }
        }

        void AddSectionLabel(string text, int x, int y)
        {
            var label = new Label
            {
                Text = text,
                Location = new Point(x, y),
                Width = 200,
                Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(120, 150, 210)
            };
            Controls.Add(label);
        }

        void AddNumberWithColor(string text, NumericUpDown box, int value, TextBox colorBox, string colorValue, int x, int y, int min, int max)
        {
            AddNumber(text, box, value, x, y, min, max);
            colorBox.Text = colorValue;
            colorBox.Location = new Point(x + 308, y);
            colorBox.Width = 80;
            colorBox.BackColor = Color.FromArgb(38, 38, 44);
            colorBox.ForeColor = Color.FromArgb(230, 232, 238);
            colorBox.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(colorBox);
        }

        void AddNumber(string text, NumericUpDown box, int value, int x, int y)
        {
            AddNumber(text, box, value, x, y, 1, 240);
        }

        void AddNumber(string text, NumericUpDown box, int value, int x, int y, int min, int max)
        {
            var label = new Label { Text = text, Location = new Point(x, y + 3), Width = 220, ForeColor = Color.FromArgb(210, 214, 222) };
            box.Minimum = min;
            box.Maximum = max;
            box.Value = Math.Max(min, Math.Min(max, value));
            box.Location = new Point(x + 224, y);
            box.Width = 72;
            box.BackColor = Color.FromArgb(38, 38, 44);
            box.ForeColor = Color.FromArgb(230, 232, 238);
            box.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(label);
            Controls.Add(box);
        }

        void AddToggle(string text, CheckBox box, bool value, int x, int y)
        {
            box.Text = text;
            box.Checked = value;
            box.Location = new Point(x, y);
            box.Width = 136;
            box.Height = 30;
            box.Appearance = Appearance.Button;
            box.FlatStyle = FlatStyle.Flat;
            box.TextAlign = ContentAlignment.MiddleCenter;
            box.BackColor = value ? Color.FromArgb(45, 95, 180) : Color.FromArgb(38, 38, 44);
            box.ForeColor = Color.FromArgb(230, 232, 238);
            box.FlatAppearance.BorderColor = value ? Color.FromArgb(70, 130, 220) : Color.FromArgb(62, 62, 70);
            box.CheckedChanged += (s, e) => StyleToggle(box);
            Controls.Add(box);
        }

        void StyleToggle(CheckBox box)
        {
            box.BackColor = box.Checked ? Color.FromArgb(45, 95, 180) : Color.FromArgb(38, 38, 44);
            box.FlatAppearance.BorderColor = box.Checked ? Color.FromArgb(70, 130, 220) : Color.FromArgb(62, 62, 70);
        }

        void AddSegment(string text, RadioButton left, RadioButton right, string leftText, string rightText, string value, string leftValue, string rightValue, int x, int y)
        {
            var label = new Label { Text = text, Location = new Point(x, y + 4), Width = 180, ForeColor = Color.FromArgb(210, 214, 222) };
            Controls.Add(label);
            var panel = new Panel { Location = new Point(x + 184, y), Width = 166, Height = 31, BackColor = BackColor };
            Controls.Add(panel);
            left.Text = leftText;
            right.Text = rightText;
            left.Checked = string.Equals(value, leftValue, StringComparison.OrdinalIgnoreCase);
            right.Checked = !left.Checked;
            StyleSegmentButton(left, 0, 0, 82);
            StyleSegmentButton(right, 82, 0, 82);
            left.CheckedChanged += (s, e) => { StyleSegmentButton(left, left.Location.X, left.Location.Y, left.Width); StyleSegmentButton(right, right.Location.X, right.Location.Y, right.Width); };
            right.CheckedChanged += (s, e) => { StyleSegmentButton(left, left.Location.X, left.Location.Y, left.Width); StyleSegmentButton(right, right.Location.X, right.Location.Y, right.Width); };
            panel.Controls.Add(left);
            panel.Controls.Add(right);
        }

        void StyleSegmentButton(RadioButton button, int x, int y, int width)
        {
            button.Location = new Point(x, y);
            button.Width = width;
            button.Height = 30;
            button.Appearance = Appearance.Button;
            button.FlatStyle = FlatStyle.Flat;
            button.TextAlign = ContentAlignment.MiddleCenter;
            button.BackColor = button.Checked ? Color.FromArgb(55, 110, 200) : Color.FromArgb(38, 38, 44);
            button.ForeColor = button.Checked ? Color.White : Color.FromArgb(205, 210, 220);
            button.FlatAppearance.BorderColor = button.Checked ? Color.FromArgb(80, 145, 235) : Color.FromArgb(62, 62, 70);
        }

        void WireLivePreview()
        {
            EventHandler apply = (s, e) =>
            {
                ApplyToSettings();
                preview();
            };
            normal.ValueChanged += apply;
            languageJa.CheckedChanged += apply;
            languageEn.CheckedChanged += apply;
            boostDuration.ValueChanged += apply;
            boostInterval.ValueChanged += apply;
            finalWindow.ValueChanged += apply;
            finalInterval.ValueChanged += apply;
            showCodex.CheckedChanged += apply;
            showClaude.CheckedChanged += apply;
            layoutHorizontal.CheckedChanged += apply;
            layoutVertical.CheckedChanged += apply;
            codexRemaining.CheckedChanged += apply;
            codexUsed.CheckedChanged += apply;
            claudeRemaining.CheckedChanged += apply;
            claudeUsed.CheckedChanged += apply;
            fiveResetRelative.CheckedChanged += apply;
            fiveResetTime.CheckedChanged += apply;
            weeklyResetRelative.CheckedChanged += apply;
            weeklyResetTime.CheckedChanged += apply;
            labelSize.ValueChanged += apply;
            percentSize.ValueChanged += apply;
            resetSize.ValueChanged += apply;
            warningPercent.ValueChanged += apply;
            criticalPercent.ValueChanged += apply;
            topMost.CheckedChanged += apply;
            warningColor.TextChanged += apply;
            criticalColor.TextChanged += apply;
        }

        void ApplyToSettings()
        {
            settings.NormalIntervalMinutes = (int)normal.Value;
            settings.Language = languageEn.Checked ? "en" : "ja";
            settings.BoostDurationMinutes = (int)boostDuration.Value;
            settings.BoostIntervalMinutes = (int)boostInterval.Value;
            settings.FinalRefreshWindowMinutes = (int)finalWindow.Value;
            settings.FinalRefreshIntervalMinutes = (int)finalInterval.Value;
            if (!showCodex.Checked && !showClaude.Checked) showClaude.Checked = true;
            settings.ShowCodex = showCodex.Checked;
            settings.ShowClaude = showClaude.Checked;
            settings.LayoutMode = layoutVertical.Checked ? "vertical" : "horizontal";
            settings.CodexShowUsed = codexUsed.Checked;
            settings.ClaudeShowUsed = claudeUsed.Checked;
            settings.FiveHourResetMode = fiveResetTime.Checked ? "time" : "relative";
            settings.WeeklyResetMode = weeklyResetTime.Checked ? "time" : "relative";
            settings.LabelFontSize = (float)labelSize.Value;
            settings.PercentFontSize = (float)percentSize.Value;
            settings.ResetFontSize = (float)resetSize.Value;
            settings.WarningRemainingPercent = Math.Max((int)criticalPercent.Value, (int)warningPercent.Value);
            settings.CriticalRemainingPercent = Math.Min((int)criticalPercent.Value, settings.WarningRemainingPercent);
            settings.WarningColor = NormalizeColorText(warningColor.Text, settings.WarningColor);
            settings.CriticalColor = NormalizeColorText(criticalColor.Text, settings.CriticalColor);
            settings.AlwaysOnTop = topMost.Checked;
        }

        string T(string ja, string en)
        {
            return string.Equals(settings.Language, "en", StringComparison.OrdinalIgnoreCase) ? en : ja;
        }

        static string NormalizeColorText(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            var text = value.Trim();
            if (!text.StartsWith("#")) text = "#" + text;
            return Regex.IsMatch(text, "^#[0-9a-fA-F]{6}$") ? text.ToUpperInvariant() : fallback;
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
        public string Language = "ja";
        public int NormalIntervalMinutes = 15;
        public int BoostDurationMinutes = 30;
        public int BoostIntervalMinutes = 1;
        public int FinalRefreshWindowMinutes = 15;
        public int FinalRefreshIntervalMinutes = 1;
        public bool AlwaysOnTop = true;
        public bool ShowCodex = true;
        public bool ShowClaude = true;
        public string LayoutMode = "horizontal";
        public bool CodexShowUsed = false;
        public bool ClaudeShowUsed = false;
        public string FiveHourResetMode = "time";
        public string WeeklyResetMode = "time";
        public float LabelFontSize = 10.8f;
        public float PercentFontSize = 16.5f;
        public float ResetFontSize = 9.9f;
        public int WarningRemainingPercent = 50;
        public int CriticalRemainingPercent = 30;
        public string WarningColor = "#E8A924";
        public string CriticalColor = "#E04949";

        static string SettingsPath
        {
            get { return Path.Combine(Application.StartupPath, "settings.json"); }
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
                s.FinalRefreshWindowMinutes = ReadInt(json, "finalRefreshWindowMinutes", s.FinalRefreshWindowMinutes);
                s.FinalRefreshIntervalMinutes = ReadInt(json, "finalRefreshIntervalMinutes", s.FinalRefreshIntervalMinutes);
                s.AlwaysOnTop = ReadBool(json, "alwaysOnTop", s.AlwaysOnTop);
                s.ShowCodex = ReadBool(json, "showCodex", s.ShowCodex);
                s.ShowClaude = ReadBool(json, "showClaude", s.ShowClaude);
                s.LayoutMode = NormalizeLayoutMode(ReadString(json, "layoutMode", s.LayoutMode));
                s.CodexShowUsed = ReadBool(json, "codexShowUsed", s.CodexShowUsed);
                s.ClaudeShowUsed = ReadBool(json, "claudeShowUsed", s.ClaudeShowUsed);
                s.FiveHourResetMode = NormalizeResetMode(ReadString(json, "fiveHourResetMode", s.FiveHourResetMode));
                s.WeeklyResetMode = NormalizeResetMode(ReadString(json, "weeklyResetMode", s.WeeklyResetMode));
                s.LabelFontSize = ReadFloat(json, "labelFontSize", s.LabelFontSize);
                s.PercentFontSize = ReadFloat(json, "percentFontSize", s.PercentFontSize);
                s.ResetFontSize = ReadFloat(json, "resetFontSize", s.ResetFontSize);
                s.WarningRemainingPercent = ReadInt(json, "warningRemainingPercent", s.WarningRemainingPercent);
                s.CriticalRemainingPercent = ReadInt(json, "criticalRemainingPercent", s.CriticalRemainingPercent);
                s.WarningColor = ReadString(json, "warningColor", s.WarningColor);
                s.CriticalColor = ReadString(json, "criticalColor", s.CriticalColor);
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
            FinalRefreshWindowMinutes = other.FinalRefreshWindowMinutes;
            FinalRefreshIntervalMinutes = other.FinalRefreshIntervalMinutes;
            AlwaysOnTop = other.AlwaysOnTop;
            ShowCodex = other.ShowCodex;
            ShowClaude = other.ShowClaude;
            LayoutMode = other.LayoutMode;
            CodexShowUsed = other.CodexShowUsed;
            ClaudeShowUsed = other.ClaudeShowUsed;
            FiveHourResetMode = other.FiveHourResetMode;
            WeeklyResetMode = other.WeeklyResetMode;
            LabelFontSize = other.LabelFontSize;
            PercentFontSize = other.PercentFontSize;
            ResetFontSize = other.ResetFontSize;
            WarningRemainingPercent = other.WarningRemainingPercent;
            CriticalRemainingPercent = other.CriticalRemainingPercent;
            WarningColor = other.WarningColor;
            CriticalColor = other.CriticalColor;
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
                    "  \"finalRefreshWindowMinutes\": " + FinalRefreshWindowMinutes + ",\r\n" +
                    "  \"finalRefreshIntervalMinutes\": " + FinalRefreshIntervalMinutes + ",\r\n" +
                    "  \"alwaysOnTop\": " + (AlwaysOnTop ? "true" : "false") + ",\r\n" +
                    "  \"showCodex\": " + (ShowCodex ? "true" : "false") + ",\r\n" +
                    "  \"showClaude\": " + (ShowClaude ? "true" : "false") + ",\r\n" +
                    "  \"layoutMode\": \"" + Escape(LayoutMode) + "\",\r\n" +
                    "  \"codexShowUsed\": " + (CodexShowUsed ? "true" : "false") + ",\r\n" +
                    "  \"claudeShowUsed\": " + (ClaudeShowUsed ? "true" : "false") + ",\r\n" +
                    "  \"fiveHourResetMode\": \"" + Escape(FiveHourResetMode) + "\",\r\n" +
                    "  \"weeklyResetMode\": \"" + Escape(WeeklyResetMode) + "\",\r\n" +
                    "  \"labelFontSize\": " + FormatFloat(LabelFontSize) + ",\r\n" +
                    "  \"percentFontSize\": " + FormatFloat(PercentFontSize) + ",\r\n" +
                    "  \"resetFontSize\": " + FormatFloat(ResetFontSize) + ",\r\n" +
                    "  \"warningRemainingPercent\": " + WarningRemainingPercent + ",\r\n" +
                    "  \"criticalRemainingPercent\": " + CriticalRemainingPercent + ",\r\n" +
                    "  \"warningColor\": \"" + Escape(WarningColor) + "\",\r\n" +
                    "  \"criticalColor\": \"" + Escape(CriticalColor) + "\"\r\n" +
                    "}\r\n");
            }
            catch { }
        }

        public Color ParseColor(string value, Color fallback)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(value)) return fallback;
                var m = Regex.Match(value.Trim(), "^#?([0-9a-fA-F]{6})$");
                if (!m.Success) return fallback;
                int rgb = Convert.ToInt32(m.Groups[1].Value, 16);
                return Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
            }
            catch
            {
                return fallback;
            }
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

        static float ReadFloat(string json, string key, float fallback)
        {
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*([0-9]+(?:\\.[0-9]+)?)");
            if (!m.Success) return fallback;
            float value;
            return float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value) ? value : fallback;
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

        static string FormatFloat(float value)
        {
            return value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }

        static string Escape(string value)
        {
            return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
