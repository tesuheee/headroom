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

        CoreWebView2Environment webEnv;
        ServiceState claude = new ServiceState("Claude", ClaudeUrl, Color.FromArgb(45, 132, 235));
        ServiceState codex = new ServiceState("Codex", CodexUrl, Color.FromArgb(26, 177, 92));
        bool resizing;
        Point resizeStartPoint;
        Size resizeStartSize;
        string hoverKey = "";
        int spinnerFrame;

        public UsageForm()
        {
            Text = "AI Usage";
            Width = settings.Width;
            Height = settings.Height;
            MinimumSize = new Size(560, 136);
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Color.Black;
            TransparencyKey = Color.Black;
            TopMost = settings.AlwaysOnTop;
            DoubleBuffered = true;
            KeyPreview = true;

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
            await MaybeRefreshAsync(codex, codexView);
            await MaybeRefreshAsync(claude, claudeView);
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
            await Task.WhenAll(
                RefreshServiceAsync(codex, codexView, manual),
                RefreshServiceAsync(claude, claudeView, manual));
        }

        async Task RefreshServiceAsync(ServiceState service, WebView2 view, bool manual)
        {
            if (service.IsRefreshing) return;
            service.IsRefreshing = true;
            service.Status = manual ? "更新中" : service.Status;
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
                service.Status = "取得エラー";
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
                StartPosition = FormStartPosition.CenterScreen
            };
            var view = new WebView2 { Dock = DockStyle.Fill };
            login.Controls.Add(view);
            login.Shown += async (s, e) =>
            {
                await view.EnsureCoreWebView2Async(webEnv);
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
                data.Status = "データなし";
                return data;
            }
            if (Regex.IsMatch(text, "ログイン|サインイン|Claude を試す|Claudeを体験する") &&
                !Regex.IsMatch(text, "プラン使用制限|現在のセッション|週間制限"))
            {
                data.Status = "ログインが必要";
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
            }
            if (weeklyStart >= 0)
            {
                int end = FirstPositive(lines.Count, designStart);
                data.WeeklyUsed = FindUsedPercent(lines, weeklyStart, end);
                data.WeeklyReset = FindResetInRange(lines, weeklyStart, end);
            }
            if (!data.HasAnyValue()) data.Status = "使用量テキストなし";
            return data;
        }

        static UsageData ParseCodex(string text)
        {
            var data = new UsageData { Name = "Codex", Source = "Codex Web", UpdatedAt = DateTime.Now };
            if (string.IsNullOrWhiteSpace(text))
            {
                data.Status = "データなし";
                return data;
            }
            if (Regex.IsMatch(text, "ログイン|サインイン|Log in|Sign in", RegexOptions.IgnoreCase) &&
                !Regex.IsMatch(text, "残高|使用制限|Codex|usage|limit", RegexOptions.IgnoreCase))
            {
                data.Status = "ログインが必要";
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
            }
            if (weeklyStart >= 0)
            {
                int end = FirstPositive(lines.Count, creditStart);
                data.WeeklyRemaining = FindRemainingPercent(lines, weeklyStart, end);
                data.WeeklyReset = FindResetInRange(lines, weeklyStart, end);
            }
            if (!data.HasAnyValue()) data.Status = "使用量テキストなし";
            return data;
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
            g.Clear(Color.Black);
            hits.Clear();

            int y = 6;
            int gap = 10;
            int sideRail = 14;
            int cardW = Math.Max(240, (ClientSize.Width - sideRail - 22 - gap) / 2);
            int cardH = ClientSize.Height - y - 5;
            DrawService(g, codex, 8, y, cardW, cardH, "codex");
            DrawService(g, claude, 18 + cardW, y, cardW, cardH, "claude");
            DrawSideRail(g);
            DrawResizeGrip(g);
        }

        void DrawSideRail(Graphics g)
        {
            int x = ClientSize.Width - 22;
            int closeY = 10;
            int pinY = 42;
            int settingsY = 74;
            hits["close"] = new Rectangle(x - 4, closeY - 4, 24, 24);
            hits["pin"] = new Rectangle(x - 4, pinY - 4, 24, 24);
            hits["settings"] = new Rectangle(x - 4, settingsY - 4, 24, 24);

            DrawIconButton(g, "close", x, closeY, Color.FromArgb(170, 170, 170), DrawCloseIcon);
            DrawIconButton(g, "pin", x, pinY, settings.AlwaysOnTop ? Color.FromArgb(245, 245, 245) : Color.FromArgb(125, 125, 125), DrawPinIcon);
            DrawIconButton(g, "settings", x, settingsY, Color.FromArgb(150, 150, 150), DrawGearIcon);
        }

        delegate void IconPainter(Graphics g, Rectangle r, Color color);

        void DrawIconButton(Graphics g, string key, int x, int y, Color color, IconPainter painter)
        {
            var r = new Rectangle(x - 1, y - 1, 20, 20);
            if (hoverKey == key)
            {
                using (var bg = new SolidBrush(Color.FromArgb(44, 44, 46)))
                using (var path = RoundRect(r.X - 2, r.Y - 2, r.Width + 4, r.Height + 4, 10))
                    g.FillPath(bg, path);
            }
            painter(g, r, color);
        }

        void DrawService(Graphics g, ServiceState state, int x, int y, int w, int h, string keyPrefix)
        {
            bool showUsed = state.Name == "Codex" ? settings.CodexShowUsed : settings.ClaudeShowUsed;
            int? fiveRemain = state.Data.FiveHourRemainingPercent();
            int? weekRemain = state.Data.WeeklyRemainingPercent();
            bool exhausted = (fiveRemain.HasValue && fiveRemain.Value <= 0) || (weekRemain.HasValue && weekRemain.Value <= 0);
            bool stale = state.LastRefresh != DateTime.MinValue &&
                DateTime.Now - state.LastRefresh > TimeSpan.FromMinutes(Math.Max(2, settings.NormalIntervalMinutes * 2));
            Color cardColor = exhausted ? Color.FromArgb(38, 24, 24) : (stale ? Color.FromArgb(34, 31, 23) : Color.FromArgb(28, 28, 30));
            Color accent = exhausted ? Color.FromArgb(220, 77, 77) : state.Accent;

            using (var bg = new SolidBrush(cardColor))
            using (var border = new Pen(exhausted ? Color.FromArgb(100, 45, 45) : (stale ? Color.FromArgb(115, 92, 38) : Color.FromArgb(38, 38, 40))))
            using (var path = RoundRect(x, y, w, h, 8))
            {
                g.FillPath(bg, path);
                g.DrawPath(border, path);
            }

            using (var title = new Font("Yu Gothic UI", 13.5f, FontStyle.Bold))
            using (var label = new Font("Yu Gothic UI", settings.LabelFontSize, FontStyle.Regular))
            using (var reset = new Font("Yu Gothic UI", settings.ResetFontSize, FontStyle.Regular))
            using (var num = new Font("Segoe UI", settings.PercentFontSize, FontStyle.Bold))
            using (var white = new SolidBrush(Color.FromArgb(248, 248, 248)))
            using (var muted = new SolidBrush(Color.FromArgb(205, 205, 205)))
            using (var dim = new SolidBrush(Color.FromArgb(205, 205, 205)))
            {
                g.DrawString(state.Name, title, white, x + 18, y + 11);
                if (stale)
                    DrawBadge(g, "古い", x + 93, y + 16, Color.FromArgb(130, 92, 25));
                if (exhausted)
                {
                    DrawBadge(g, "上限", x + 93, y + 16, Color.FromArgb(116, 42, 42));
                    string limitReset = LimitResetText(state.Data, fiveRemain, weekRemain);
                    if (limitReset.Length > 0)
                    {
                        using (var badgeText = new Font("Yu Gothic UI", 8.8f, FontStyle.Regular))
                            g.DrawString(limitReset, badgeText, dim, x + 132, y + 16);
                    }
                }
                DrawCardControls(g, state, x, y, w, keyPrefix);

                if (!state.Data.HasAnyValue())
                {
                    g.DrawString("--", num, white, x + 58, y + 54);
                    string status = state.IsRefreshing ? "更新中" : (state.Status ?? state.Data.Status ?? "データなし");
                    g.DrawString(status, label, muted, x + 20, y + 88);
                    DrawLoginButton(g, x + w - 96, y + h - 38, keyPrefix + "-login");
                    return;
                }

                int contentTop = y + Math.Max(42, (int)Math.Ceiling(settings.PercentFontSize + 22));
                int contentBottom = y + h - 12;
                int rowHeight = Math.Max(30, (int)Math.Ceiling(Math.Max(settings.PercentFontSize * 1.55, settings.LabelFontSize + settings.ResetFontSize + 12)));
                int usable = Math.Max(rowHeight * 2, contentBottom - contentTop);
                int free = usable - rowHeight * 2;
                int gapY = Math.Max(4, free / 3);
                int firstY = contentTop + gapY;
                int secondY = firstY + rowHeight + gapY;
                DrawRow(g, "5時間", state.Data.FiveHourDisplayPercent(showUsed), showUsed, false, state.Data.FiveHourReset, x, firstY, w, accent, label, reset, num, white, muted, dim);
                DrawRow(g, "週", state.Data.WeeklyDisplayPercent(showUsed), showUsed, true, state.Data.WeeklyReset, x, secondY, w, accent, label, reset, num, white, muted, dim);
            }
        }

        void DrawCardControls(Graphics g, ServiceState state, int x, int y, int w, string keyPrefix)
        {
            int refreshX = x + w - 32;
            int controlY = y + 13;
            int toggleW = 31;
            int toggleH = 17;
            int toggleX = refreshX - toggleW - 11;
            var boostText = BoostText(state);
            int boostTextW = boostText.Length > 0 ? 50 : 0;
            int boostTextX = toggleX - boostTextW - 6;

            hits[keyPrefix + "-refresh"] = new Rectangle(refreshX - 4, controlY - 3, 30, 28);
            hits[keyPrefix + "-boost"] = new Rectangle(toggleX - 3, controlY + 1, toggleW + 6, toggleH + 6);

            using (var small = new Font("Yu Gothic UI", 8.8f, FontStyle.Regular))
            using (var textBrush = new SolidBrush(Color.FromArgb(218, 218, 218)))
            {
                if (boostText.Length > 0)
                    g.DrawString(boostText, small, textBrush, boostTextX, controlY + 3);
            }

            DrawToggle(g, toggleX, controlY + 3, toggleW, toggleH, state.BoostActive, hoverKey == keyPrefix + "-boost");
            DrawRefreshIcon(g, refreshX, controlY, keyPrefix + "-refresh", state.IsRefreshing);
        }

        static string LimitResetText(UsageData data, int? fiveRemain, int? weekRemain)
        {
            string raw = "";
            if (fiveRemain.HasValue && fiveRemain.Value <= 0) raw = data.FiveHourReset;
            else if (weekRemain.HasValue && weekRemain.Value <= 0) raw = data.WeeklyReset;
            string text = ResetText(raw);
            return text.Replace("リセットまで ", "あと ");
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
            return "残り" + min + "分";
        }

        void DrawRow(Graphics g, string label, int? pct, bool showUsed, bool weekly, string resetText, int x, int y, int w, Color accent, Font labelFont, Font resetFont, Font numFont, Brush white, Brush muted, Brush dim)
        {
            bool empty = !pct.HasValue;
            bool limit = pct.HasValue && (showUsed ? 100 - pct.Value : pct.Value) <= settings.CriticalRemainingPercent;
            Color rowColor = RowColor(pct, showUsed, accent);
            using (var pctBrush = new SolidBrush(limit ? Color.FromArgb(255, 145, 145) : Color.FromArgb(248, 248, 248)))
            {
            int labelX = x + 18;
            int modeX = labelX + Math.Max(34, (int)Math.Ceiling(g.MeasureString("5時間", labelFont).Width)) + 7;
            int percentX = modeX + Math.Max(30, (int)Math.Ceiling(g.MeasureString(showUsed ? "使用" : "残り", labelFont).Width)) + 7;
            int labelY = y + Math.Max(0, (int)Math.Round((numFont.Size - labelFont.Size) / 2.0));
            g.DrawString(label, labelFont, muted, labelX, labelY);
            g.DrawString(showUsed ? "使用" : "残り", labelFont, muted, modeX, labelY);
            g.DrawString(empty ? "--" : pct.Value + "%", numFont, pctBrush, percentX, y - 4);

            int pctWidth = Math.Max(42, (int)Math.Ceiling(g.MeasureString("100%", numFont).Width));
            int barX = percentX + pctWidth + 8;
            int barY = y + Math.Max(5, (int)Math.Round(settings.PercentFontSize * 0.45));
            int barW = Math.Max(70, w - (barX - x) - 17);
            DrawBar(g, barX, barY, barW, 7, pct, rowColor);

            string reset = ResetText(resetText, weekly);
            if (!string.IsNullOrEmpty(reset))
                g.DrawString(reset, resetFont, dim, barX, y + Math.Max(16, (int)Math.Round(settings.PercentFontSize * 0.95)));
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
            return ResetText(raw, false);
        }

        static string ResetText(string raw, bool preferAbsolute)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var lower = raw.ToLowerInvariant();
            string cleaned = Regex.Replace(raw, @"^\s*リセット\s*[：:]\s*", "").Trim();
            if (cleaned.Contains("リセットまで")) return cleaned;
            var relative = RelativeResetText(cleaned, preferAbsolute);
            if (!string.IsNullOrEmpty(relative)) return relative;

            DateTime target;
            if (Regex.IsMatch(cleaned, @"(?:\d{4}/)?\d{1,2}/\d{1,2}\s+\d{1,2}:\d{2}") &&
                TryParseResetTarget(cleaned, out target))
                return "リセット " + FormatDateTime(target);

            if (TryParseResetTarget(cleaned, out target))
                return "リセットまで " + FormatDuration(target - DateTime.Now);

            if (cleaned.Contains("後にリセット"))
            {
                var s = cleaned.Replace("後にリセット", "").Replace("リセット", "").Trim();
                return "リセットまで " + s;
            }
            if (lower.Contains("reset"))
            {
                var m = Regex.Match(cleaned, @"(\d{1,2}/\d{1,2}\s+\d{1,2}:\d{2})");
                if (m.Success) return "リセット " + m.Groups[1].Value;
                return cleaned;
            }
            return "リセット " + cleaned;
        }

        static string RelativeResetText(string text, bool preferAbsolute)
        {
            var m = Regex.Match(text, @"(?:(\d+)\s*時間)?\s*(?:(\d+)\s*分)?\s*後にリセット");
            if (!m.Success) return "";
            int hours = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0;
            int minutes = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
            var span = new TimeSpan(hours, minutes, 0);
            if (preferAbsolute) return "リセット " + FormatDateTime(DateTime.Now.Add(span));
            return "リセットまで " + FormatDuration(span);
        }

        static bool TryParseResetTarget(string text, out DateTime target)
        {
            target = DateTime.MinValue;
            var dateTime = Regex.Match(text, @"(?:(\d{4})/)?(\d{1,2})/(\d{1,2})\s+(\d{1,2}):(\d{2})");
            if (dateTime.Success)
            {
                int year = dateTime.Groups[1].Success ? int.Parse(dateTime.Groups[1].Value) : DateTime.Now.Year;
                target = new DateTime(year, int.Parse(dateTime.Groups[2].Value), int.Parse(dateTime.Groups[3].Value), int.Parse(dateTime.Groups[4].Value), int.Parse(dateTime.Groups[5].Value), 0);
                return true;
            }

            var time = Regex.Match(text, @"^(\d{1,2}):(\d{2})$");
            if (time.Success)
            {
                var now = DateTime.Now;
                target = new DateTime(now.Year, now.Month, now.Day, int.Parse(time.Groups[1].Value), int.Parse(time.Groups[2].Value), 0);
                if (target < now.AddMinutes(-1)) target = target.AddDays(1);
                return true;
            }
            return false;
        }

        static string FormatDuration(TimeSpan span)
        {
            if (span.TotalSeconds <= 0) return "0分";
            int totalMinutes = Math.Max(0, (int)Math.Ceiling(span.TotalMinutes));
            int hours = totalMinutes / 60;
            int minutes = totalMinutes % 60;
            if (hours > 0) return hours + "時間" + minutes + "分";
            return minutes + "分";
        }

        static string FormatDateTime(DateTime value)
        {
            return value.ToString("M/d H:mm");
        }

        void DrawLoginButton(Graphics g, int x, int y, string key)
        {
            hits[key] = new Rectangle(x, y, 76, 25);
            Color bg = hoverKey == key ? Color.FromArgb(64, 64, 66) : Color.FromArgb(46, 46, 48);
            using (var b = new SolidBrush(bg))
            using (var p = RoundRect(x, y, 76, 25, 8))
                g.FillPath(b, p);
            using (var f = new Font("Yu Gothic UI", 9.5f, FontStyle.Bold))
            using (var white = new SolidBrush(Color.WhiteSmoke))
                g.DrawString("ログイン", f, white, x + 13, y + 3);
        }

        void DrawBadge(Graphics g, string text, int x, int y, Color color)
        {
            using (var bg = new SolidBrush(color))
            using (var path = RoundRect(x, y, 34, 18, 9))
                g.FillPath(bg, path);
            using (var f = new Font("Yu Gothic UI", 8.5f, FontStyle.Bold))
            using (var b = new SolidBrush(Color.WhiteSmoke))
                g.DrawString(text, f, b, x + 6, y + 1);
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
                using (var bg = new SolidBrush(Color.FromArgb(42, 42, 44)))
                using (var path = RoundRect(r.X, r.Y, r.Width, r.Height, 12))
                    g.FillPath(bg, path);
            }

            string[] frames = { "◜", "◝", "◞", "◟" };
            string s = active ? frames[spinnerFrame] : "↻";
            using (var f = new Font("Segoe UI Symbol", active ? 13f : 12.5f, FontStyle.Regular))
            using (var b = new SolidBrush(Color.FromArgb(238, 238, 238)))
                g.DrawString(s, f, b, x + (active ? 2 : 1), y + (active ? 0 : -1));
        }

        void DrawToggle(Graphics g, int x, int y, int w, int h, bool on, bool hover)
        {
            Color bg = on ? Color.FromArgb(45, 132, 235) : Color.FromArgb(62, 62, 64);
            if (hover && !on) bg = Color.FromArgb(78, 78, 80);
            using (var b = new SolidBrush(bg))
            using (var p = RoundRect(x, y, w, h, h / 2))
                g.FillPath(b, p);
            int knob = h - 5;
            int knobX = on ? x + w - knob - 3 : x + 3;
            using (var b = new SolidBrush(Color.WhiteSmoke))
            using (var p = RoundRect(knobX, y + 2, knob, knob, knob / 2))
                g.FillPath(b, p);
        }

        void DrawCloseIcon(Graphics g, Rectangle r, Color color)
        {
            using (var pen = new Pen(color, 1.8f))
            {
                g.DrawLine(pen, r.X + 5, r.Y + 5, r.X + 15, r.Y + 15);
                g.DrawLine(pen, r.X + 15, r.Y + 5, r.X + 5, r.Y + 15);
            }
        }

        void DrawPinIcon(Graphics g, Rectangle r, Color color)
        {
            using (var brush = new SolidBrush(color))
            using (var pen = new Pen(color, 1.5f))
            {
                var head = new Point[]
                {
                    new Point(r.X + 7, r.Y + 3),
                    new Point(r.X + 15, r.Y + 7),
                    new Point(r.X + 11, r.Y + 11),
                    new Point(r.X + 4, r.Y + 7)
                };
                g.FillPolygon(brush, head);
                g.DrawLine(pen, r.X + 10, r.Y + 10, r.X + 5, r.Y + 17);
                g.DrawLine(pen, r.X + 8, r.Y + 14, r.X + 12, r.Y + 18);
            }
        }

        void DrawGearIcon(Graphics g, Rectangle r, Color color)
        {
            int cx = r.X + r.Width / 2;
            int cy = r.Y + r.Height / 2;
            using (var pen = new Pen(color, 1.4f))
            {
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4.0;
                    int x1 = cx + (int)Math.Round(Math.Cos(a) * 6);
                    int y1 = cy + (int)Math.Round(Math.Sin(a) * 6);
                    int x2 = cx + (int)Math.Round(Math.Cos(a) * 9);
                    int y2 = cy + (int)Math.Round(Math.Sin(a) * 9);
                    g.DrawLine(pen, x1, y1, x2, y2);
                }
                g.DrawEllipse(pen, cx - 6, cy - 6, 12, 12);
                g.DrawEllipse(pen, cx - 2, cy - 2, 4, 4);
            }
        }

        static void DrawBar(Graphics g, int x, int y, int w, int h, int? pct, Color accent)
        {
            using (var bg = new SolidBrush(Color.FromArgb(44, 44, 44)))
            using (var path = RoundRect(x, y, w, h, h / 2))
                g.FillPath(bg, path);
            if (!pct.HasValue) return;
            int fillW = Math.Max(pct.Value <= 0 ? 0 : 5, (int)Math.Round(w * Math.Max(0, Math.Min(100, pct.Value)) / 100.0));
            if (fillW <= 0) return;
            using (var fg = new SolidBrush(accent))
            using (var path = RoundRect(x, y, fillW, h, h / 2))
                g.FillPath(fg, path);
        }

        void DrawResizeGrip(Graphics g)
        {
            using (var pen = new Pen(Color.FromArgb(120, 120, 120), 1.2f))
            {
                int right = ClientSize.Width - 4;
                int bottom = ClientSize.Height - 4;
                g.DrawLine(pen, right - 13, bottom, right, bottom - 13);
                g.DrawLine(pen, right - 8, bottom, right, bottom - 8);
                g.DrawLine(pen, right - 3, bottom, right, bottom - 3);
            }
        }

        void ShowSettingsDialog()
        {
            using (var dlg = new SettingsForm(settings, () =>
            {
                TopMost = settings.AlwaysOnTop;
                Invalidate();
            }))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
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
        readonly NumericUpDown normal = new NumericUpDown();
        readonly NumericUpDown boostDuration = new NumericUpDown();
        readonly NumericUpDown boostInterval = new NumericUpDown();
        readonly NumericUpDown finalWindow = new NumericUpDown();
        readonly NumericUpDown finalInterval = new NumericUpDown();
        readonly CheckBox topMost = new CheckBox();
        readonly ComboBox codexMode = new ComboBox();
        readonly ComboBox claudeMode = new ComboBox();
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
            Text = "設定";
            Width = 430;
            Height = 590;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.FromArgb(24, 24, 26);
            ForeColor = Color.WhiteSmoke;

            AddNumber("通常更新（分）", normal, settings.NormalIntervalMinutes, 18, 18);
            AddNumber("ブースト時間（分）", boostDuration, settings.BoostDurationMinutes, 18, 60);
            AddNumber("ブースト更新（分）", boostInterval, settings.BoostIntervalMinutes, 18, 102);
            AddNumber("直前更新開始（分）", finalWindow, settings.FinalRefreshWindowMinutes, 18, 144, 1, 120);
            AddNumber("直前更新間隔（分）", finalInterval, settings.FinalRefreshIntervalMinutes, 18, 186, 1, 30);
            AddMode("Codex 表示", codexMode, settings.CodexShowUsed, 18, 228);
            AddMode("Claude 表示", claudeMode, settings.ClaudeShowUsed, 18, 270);
            AddNumber("ラベル文字", labelSize, (int)Math.Round(settings.LabelFontSize), 18, 312, 6, 32);
            AddNumber("パーセント文字", percentSize, (int)Math.Round(settings.PercentFontSize), 18, 354, 8, 42);
            AddNumber("リセット文字", resetSize, (int)Math.Round(settings.ResetFontSize), 18, 396, 6, 32);
            AddNumber("黄色しきい値（残量%）", warningPercent, settings.WarningRemainingPercent, 18, 438, 1, 99);
            AddNumber("赤しきい値（残量%）", criticalPercent, settings.CriticalRemainingPercent, 18, 480, 1, 99);
            AddText("黄色", warningColor, settings.WarningColor, 270, 438);
            AddText("赤", criticalColor, settings.CriticalColor, 270, 480);

            topMost.Text = "常に最前面に固定";
            topMost.Checked = settings.AlwaysOnTop;
            topMost.Location = new Point(22, 516);
            topMost.Width = 180;
            Controls.Add(topMost);

            var ok = new Button { Text = "保存", DialogResult = DialogResult.OK, Location = new Point(242, 516), Width = 72 };
            var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, Location = new Point(324, 516), Width = 82 };
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

        void AddNumber(string text, NumericUpDown box, int value, int x, int y)
        {
            AddNumber(text, box, value, x, y, 1, 240);
        }

        void AddNumber(string text, NumericUpDown box, int value, int x, int y, int min, int max)
        {
            var label = new Label { Text = text, Location = new Point(x, y + 4), Width = 150, ForeColor = Color.WhiteSmoke };
            box.Minimum = min;
            box.Maximum = max;
            box.Value = Math.Max(min, Math.Min(max, value));
            box.Location = new Point(180, y);
            box.Width = 70;
            Controls.Add(label);
            Controls.Add(box);
        }

        void AddMode(string text, ComboBox box, bool showUsed, int x, int y)
        {
            var label = new Label { Text = text, Location = new Point(x, y + 4), Width = 150, ForeColor = Color.WhiteSmoke };
            box.DropDownStyle = ComboBoxStyle.DropDownList;
            box.Items.Add("残量表示");
            box.Items.Add("使用量表示");
            box.SelectedIndex = showUsed ? 1 : 0;
            box.Location = new Point(180, y);
            box.Width = 120;
            Controls.Add(label);
            Controls.Add(box);
        }

        void AddText(string text, TextBox box, string value, int x, int y)
        {
            var label = new Label { Text = text, Location = new Point(x, y + 4), Width = 40, ForeColor = Color.WhiteSmoke };
            box.Text = value;
            box.Location = new Point(x + 42, y);
            box.Width = 78;
            Controls.Add(label);
            Controls.Add(box);
        }

        void WireLivePreview()
        {
            EventHandler apply = (s, e) =>
            {
                ApplyToSettings();
                preview();
            };
            normal.ValueChanged += apply;
            boostDuration.ValueChanged += apply;
            boostInterval.ValueChanged += apply;
            finalWindow.ValueChanged += apply;
            finalInterval.ValueChanged += apply;
            codexMode.SelectedIndexChanged += apply;
            claudeMode.SelectedIndexChanged += apply;
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
            settings.BoostDurationMinutes = (int)boostDuration.Value;
            settings.BoostIntervalMinutes = (int)boostInterval.Value;
            settings.FinalRefreshWindowMinutes = (int)finalWindow.Value;
            settings.FinalRefreshIntervalMinutes = (int)finalInterval.Value;
            settings.CodexShowUsed = codexMode.SelectedIndex == 1;
            settings.ClaudeShowUsed = claudeMode.SelectedIndex == 1;
            settings.LabelFontSize = (float)labelSize.Value;
            settings.PercentFontSize = (float)percentSize.Value;
            settings.ResetFontSize = (float)resetSize.Value;
            settings.WarningRemainingPercent = Math.Max((int)criticalPercent.Value, (int)warningPercent.Value);
            settings.CriticalRemainingPercent = Math.Min((int)criticalPercent.Value, settings.WarningRemainingPercent);
            settings.WarningColor = NormalizeColorText(warningColor.Text, settings.WarningColor);
            settings.CriticalColor = NormalizeColorText(criticalColor.Text, settings.CriticalColor);
            settings.AlwaysOnTop = topMost.Checked;
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
            Data = new UsageData { Name = name, Source = "starting", Status = "起動中" };
            Status = "起動中";
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
        public int Width = 680;
        public int Height = 170;
        public int NormalIntervalMinutes = 15;
        public int BoostDurationMinutes = 15;
        public int BoostIntervalMinutes = 1;
        public int FinalRefreshWindowMinutes = 15;
        public int FinalRefreshIntervalMinutes = 1;
        public bool AlwaysOnTop = true;
        public bool CodexShowUsed = false;
        public bool ClaudeShowUsed = false;
        public float LabelFontSize = 10.8f;
        public float PercentFontSize = 16.5f;
        public float ResetFontSize = 9.9f;
        public int WarningRemainingPercent = 30;
        public int CriticalRemainingPercent = 15;
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
                s.NormalIntervalMinutes = ReadInt(json, "normalIntervalMinutes", s.NormalIntervalMinutes);
                s.BoostDurationMinutes = ReadInt(json, "boostDurationMinutes", s.BoostDurationMinutes);
                s.BoostIntervalMinutes = ReadInt(json, "boostIntervalMinutes", s.BoostIntervalMinutes);
                s.FinalRefreshWindowMinutes = ReadInt(json, "finalRefreshWindowMinutes", s.FinalRefreshWindowMinutes);
                s.FinalRefreshIntervalMinutes = ReadInt(json, "finalRefreshIntervalMinutes", s.FinalRefreshIntervalMinutes);
                s.AlwaysOnTop = ReadBool(json, "alwaysOnTop", s.AlwaysOnTop);
                s.CodexShowUsed = ReadBool(json, "codexShowUsed", s.CodexShowUsed);
                s.ClaudeShowUsed = ReadBool(json, "claudeShowUsed", s.ClaudeShowUsed);
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
            NormalIntervalMinutes = other.NormalIntervalMinutes;
            BoostDurationMinutes = other.BoostDurationMinutes;
            BoostIntervalMinutes = other.BoostIntervalMinutes;
            FinalRefreshWindowMinutes = other.FinalRefreshWindowMinutes;
            FinalRefreshIntervalMinutes = other.FinalRefreshIntervalMinutes;
            AlwaysOnTop = other.AlwaysOnTop;
            CodexShowUsed = other.CodexShowUsed;
            ClaudeShowUsed = other.ClaudeShowUsed;
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
                    "  \"normalIntervalMinutes\": " + NormalIntervalMinutes + ",\r\n" +
                    "  \"boostDurationMinutes\": " + BoostDurationMinutes + ",\r\n" +
                    "  \"boostIntervalMinutes\": " + BoostIntervalMinutes + ",\r\n" +
                    "  \"finalRefreshWindowMinutes\": " + FinalRefreshWindowMinutes + ",\r\n" +
                    "  \"finalRefreshIntervalMinutes\": " + FinalRefreshIntervalMinutes + ",\r\n" +
                    "  \"alwaysOnTop\": " + (AlwaysOnTop ? "true" : "false") + ",\r\n" +
                    "  \"codexShowUsed\": " + (CodexShowUsed ? "true" : "false") + ",\r\n" +
                    "  \"claudeShowUsed\": " + (ClaudeShowUsed ? "true" : "false") + ",\r\n" +
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
