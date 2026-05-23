using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Headroom
{
    partial class UsageForm
    {
        delegate void IconPainter(Graphics g, Rectangle r, Color color);

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.Black);
            hits.Clear();
            silentHits.Clear();

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
            if (sideRailOpacity > 0.01)
                DrawSideRail(g);
        }

        List<Tuple<ServiceState, string>> VisibleServices()
        {
            var items = new List<Tuple<ServiceState, string>>();
            bool codexFirst = string.Equals(settings.ServiceOrder, "codex-claude", StringComparison.OrdinalIgnoreCase);
            if (codexFirst)
            {
                if (settings.ShowCodex) items.Add(Tuple.Create(codex, "codex"));
                if (settings.ShowClaude) items.Add(Tuple.Create(claude, "claude"));
            }
            else
            {
                if (settings.ShowClaude) items.Add(Tuple.Create(claude, "claude"));
                if (settings.ShowCodex) items.Add(Tuple.Create(codex, "codex"));
            }
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
            int fiveY     = 82;
            int weekY     = 108;
            int settingsY = 134;

            RegisterSideRailHits();

            DrawIconButton(g, "close",     x, closeY,    Color.FromArgb(160, 160, 165), DrawCloseIcon);
            DrawIconButton(g, "pin",       x, pinY,      settings.AlwaysOnTop ? Color.FromArgb(100, 180, 255) : Color.FromArgb(100, 100, 105), DrawPinIcon);
            DrawIconButton(g, "token",     x, tokenY,    Color.FromArgb(130, 145, 165), DrawTokenToggleIcon);
            DrawIconButton(g, "fiveReset", x, fiveY,     Color.FromArgb(110, 125, 145), DrawFiveResetIcon);
            DrawIconButton(g, "weekReset", x, weekY,     Color.FromArgb(110, 125, 145), DrawWeekResetIcon);
            DrawIconButton(g, "settings",  x, settingsY, Color.FromArgb(130, 130, 135), DrawGearIcon);
        }

        void RegisterSideRailHits()
        {
            int x         = ClientSize.Width - 24;
            int closeY    = 4;
            int pinY      = 30;
            int tokenY    = 56;
            int fiveY     = 82;
            int weekY     = 108;
            int settingsY = 134;

            hits["close"]     = new Rectangle(x - 6, closeY    - 6, 28, 28);
            hits["pin"]       = new Rectangle(x - 6, pinY      - 6, 28, 28);
            hits["token"]     = new Rectangle(x - 6, tokenY    - 6, 28, 28);
            hits["fiveReset"] = new Rectangle(x - 6, fiveY     - 6, 28, 28);
            hits["weekReset"] = new Rectangle(x - 6, weekY     - 6, 28, 28);
            hits["settings"]  = new Rectangle(x - 6, settingsY - 6, 28, 28);
        }

        void DrawIconButton(Graphics g, string key, int x, int y, Color color, IconPainter painter)
        {
            var r = new Rectangle(x - 1, y - 1, 20, 20);
            if (hoverKey == key)
            {
                Color rawHoverBg =
                    key == "close" ? Color.FromArgb(190, 58, 58) :
                    key == "settings" ? Color.FromArgb(45, 132, 235) :
                    Color.FromArgb(50, 50, 54);
                Color hoverBg = FadeSideRailColor(rawHoverBg);
                using (var bg = new SolidBrush(hoverBg))
                using (var path = RoundRect(r.X - 4, r.Y - 4, r.Width + 8, r.Height + 8, 12))
                    g.FillPath(bg, path);
                color = key == "close" || key == "settings" ? Color.White : Color.FromArgb(Math.Min(255, color.R + 40), Math.Min(255, color.G + 40), Math.Min(255, color.B + 40));
            }
            painter(g, r, FadeSideRailColor(color));
        }

        Color FadeSideRailColor(Color color)
        {
            int alpha = Math.Max(0, Math.Min(255, (int)Math.Round(color.A * sideRailOpacity)));
            return Color.FromArgb(alpha, color.R, color.G, color.B);
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

            using (var path = RoundRect(x, y, w, h, 12))
            {
                Color topColor = exhausted ? Color.FromArgb(42, 22, 22) : (stale ? Color.FromArgb(38, 34, 22) : Color.FromArgb(30, 30, 34));
                Color bottomColor = exhausted ? Color.FromArgb(32, 18, 18) : (stale ? Color.FromArgb(30, 28, 18) : Color.FromArgb(22, 22, 26));
                using (var grad = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(x, y, w, h), topColor, bottomColor, 90f))
                    g.FillPath(grad, path);
                using (var highlight = new Pen(Color.FromArgb(exhausted ? 18 : 28, 255, 255, 255)))
                    g.DrawPath(highlight, path);
            }
            using (var path = RoundRect(x, y, w, h, 12))
            using (var border = new Pen(exhausted ? Color.FromArgb(80, 40, 40) : (stale ? Color.FromArgb(80, 68, 30) : Color.FromArgb(48, 48, 54)), 0.8f))
                g.DrawPath(border, path);

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
                if (state.Status == "rate_limited")
                    DrawBadge(g, T("待機", "Wait"), x + 100, y + 14, Color.FromArgb(80, 64, 28), Color.FromArgb(220, 185, 80));
                DrawCardControls(g, state, x, y, w, keyPrefix);

                if (!state.Data.HasAnyValue())
                {
                    g.DrawString("--", num, white, x + 58, y + 54);
                    string effectiveStatus = state.Status ?? state.Data.Status ?? "no_data";
                    string status = state.IsRefreshing ? T("更新中", "Updating") : StatusText(effectiveStatus);
                    g.DrawString(status, label, muted, x + 20, y + 88);
                    if (NeedsLogin(effectiveStatus))
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
                bool fiveLockedByWeekly = weekRemain.HasValue && weekRemain.Value <= 0;
                Color fiveAccent = fiveLockedByWeekly ? Color.FromArgb(58, 60, 68) : state.Accent;
                DrawRow(g, T("5時間", "5h"), state.Data.FiveHourDisplayPercent(showUsed), state.DisplayedFivePct, showUsed, false, state.Data.FiveHourReset, state.Data.FiveHourNotStarted, settings.FiveHourResetMode, x, firstY, w, fiveAccent, label, reset, num, white, muted, dim, keyPrefix + "-fiveMode", keyPrefix + "-fiveResetLabel", fiveLockedByWeekly);
                DrawRow(g, T("週", "Week"), state.Data.WeeklyDisplayPercent(showUsed), state.DisplayedWeekPct, showUsed, true, state.Data.WeeklyReset, state.Data.WeeklyNotStarted, settings.WeeklyResetMode, x, secondY, w, state.Accent, label, reset, num, white, muted, dim, keyPrefix + "-weekMode", keyPrefix + "-weekResetLabel");
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

        string StatusText(string status)
        {
            switch (status)
            {
                case "updating": return T("更新中", "Updating");
                case "rate_limited": return T("429待機中", "Rate limited");
                case "fetch_error": return T("一時的にAPIに接続できません", "Temporarily unreachable");
                case "no_data": return T("データなし", "No data");
                case "login_required": return T("ログインしてください", "Please log in");
                case "login_pending": return T("ログイン中…ブラウザで認証してください", "Signing in… complete it in your browser");
                case "fixture_missing": return T("フィクスチャなし", "Fixture missing");
                case "no_usage_text": return T("使用量テキストなし", "No usage text");
                case "starting": return T("起動中", "Starting");
                default: return status ?? T("データなし", "No data");
            }
        }

        static bool NeedsLogin(string status)
        {
            return status == "login_required" || status == "login_pending";
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

        void DrawRow(Graphics g, string label, int? pct, double? barPct, bool showUsed, bool weekly, string resetText, bool notStarted, string resetMode, int x, int y, int w, Color accent, Font labelFont, Font resetFont, Font numFont, Brush white, Brush muted, Brush dim, string hitMode = null, string hitReset = null, bool disabled = false)
        {
            bool empty = !pct.HasValue;
            bool atLimit = pct.HasValue && (showUsed ? pct.Value >= 100 : pct.Value <= 0);
            Color rowColor = disabled ? Color.FromArgb(88, 92, 104) : RowColor(pct, showUsed, accent);
            Color numColor = disabled ? Color.FromArgb(130, 136, 150) : Color.FromArgb(240, 242, 248);
            Color labelColor = disabled ? Color.FromArgb(120, 126, 140) : Color.FromArgb(210, 215, 228);
            Color dimColor = disabled ? Color.FromArgb(105, 111, 124) : Color.FromArgb(185, 190, 205);
            using (var pctBrush = new SolidBrush(numColor))
            using (var labelBrush = new SolidBrush(labelColor))
            using (var dimBrush = new SolidBrush(dimColor))
            {
                int labelX = x + 12;
                int label5hW = (int)Math.Ceiling(g.MeasureString(T("5時間", "5h"), labelFont).Width);
                int labelWkW = (int)Math.Ceiling(g.MeasureString(T("週", "Week"), labelFont).Width);
                int maxLabelW = Math.Max(label5hW, labelWkW);
                string modeText = showUsed ? T("使用", "Used") : T("残り", "Rem");
                int modeColW = Math.Max(
                    (int)Math.Ceiling(g.MeasureString(T("使用", "Used"), labelFont).Width),
                    (int)Math.Ceiling(g.MeasureString(T("残り", "Rem"), labelFont).Width));
                int modeX = labelX + maxLabelW + 2;
                int percentX = modeX + modeColW + 2;
                int labelY = y + Math.Max(0, (int)Math.Round((numFont.Size - labelFont.Size) / 2.0));
                g.DrawString(label, labelFont, labelBrush, labelX, labelY);
                if (hitMode != null)
                    silentHits[hitMode] = new Rectangle(modeX - 2, labelY - 2, modeColW + 4, labelFont.Height + 4);
                g.DrawString(modeText, labelFont, dimBrush, modeX, labelY);
                string pctDigits = empty ? "--" : pct.Value.ToString(CultureInfo.InvariantCulture);
                int digitColW = Math.Max(34, (int)Math.Ceiling(g.MeasureString("100", numFont).Width));
                int pctSignW = Math.Max(10, (int)Math.Ceiling(g.MeasureString("%", numFont).Width));
                int pctColW = digitColW + pctSignW + 2;
                using (var pctFormat = new StringFormat())
                {
                    pctFormat.Alignment = StringAlignment.Far;
                    pctFormat.LineAlignment = StringAlignment.Near;
                    pctFormat.FormatFlags = StringFormatFlags.NoWrap;
                    g.DrawString(pctDigits, numFont, pctBrush, new RectangleF(percentX, y - 4, digitColW, numFont.Height + 4), pctFormat);
                    if (!empty)
                        g.DrawString("%", numFont, pctBrush, percentX + digitColW + 2, y - 4);
                }
                int barX = percentX + pctColW + 4;
                int barY = y + Math.Max(5, (int)Math.Round(PercentFontSize * 0.42));
                int barW = Math.Max(70, w - (barX - x) - 14);
                DrawBar(g, barX, barY, barW, 9, barPct, rowColor);

                string reset = notStarted ? T("未開始", "Not started") : ResetText(resetText, resetMode, English, weekly);
                if (!string.IsNullOrEmpty(reset))
                {
                    int resetY = y + Math.Max(18, (int)Math.Round(PercentFontSize * 1.0));
                    if (hitReset != null)
                    {
                        SizeF resetSz = g.MeasureString(reset, resetFont);
                        silentHits[hitReset] = new Rectangle(barX - 2, resetY - 2, (int)Math.Ceiling(resetSz.Width) + 4, (int)Math.Ceiling(resetSz.Height) + 4);
                    }
                    if (atLimit && !disabled)
                        using (var boldReset = new Font(resetFont, FontStyle.Bold))
                            g.DrawString(reset, boldReset, white, barX, resetY);
                    else
                        g.DrawString(reset, resetFont, dimBrush, barX, resetY);
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
            int days = totalMinutes / (60 * 24);
            int hours = (totalMinutes % (60 * 24)) / 60;
            int minutes = totalMinutes % 60;
            if (english)
            {
                if (days > 0) return days + "d " + hours + "h " + minutes + "m";
                if (hours > 0) return hours + "h " + minutes + "m";
                return minutes + "m";
            }
            if (days > 0) return days + "日 " + hours + "時間 " + minutes + "分";
            if (hours > 0) return hours + "時間 " + minutes + "分";
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
                TextRenderer.DrawText(g, T("ログイン", "Login"), f,
                    new Rectangle(x, y, buttonW, buttonH),
                    Color.FromArgb(235, 238, 242),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
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
                g.DrawLine(pen, r.X + 7, r.Y + 4, r.X + 14, r.Y + 4);
                g.DrawLine(pen, r.X + 14, r.Y + 4, r.X + 14, r.Y + 10);
                g.DrawLine(pen, r.X + 14, r.Y + 10, r.X + 7, r.Y + 10);
                g.DrawLine(pen, r.X + 7, r.Y + 10, r.X + 7, r.Y + 4);
                g.DrawLine(pen, r.X + 10, r.Y + 10, r.X + 10, r.Y + 16);
                g.FillEllipse(new SolidBrush(color), r.X + 9, r.Y + 15, 3, 3);
            }
        }

        void DrawGearIcon(Graphics g, Rectangle r, Color color)
        {
            int cx = r.X + r.Width / 2;
            int cy = r.Y + r.Height / 2;
            using (var pen = new Pen(color, 1.5f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
            {
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

            using (var glowPath = RoundRect(x, y + 1, fillW, barH, barH / 2))
            using (var glow = new SolidBrush(Color.FromArgb(30, accent.R, accent.G, accent.B)))
                g.FillPath(glow, glowPath);

            using (var fillPath = RoundRect(x, y, fillW, barH, barH / 2))
            {
                Color lighter = Color.FromArgb(Math.Min(255, accent.R + 40), Math.Min(255, accent.G + 40), Math.Min(255, accent.B + 40));
                using (var grad = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(x, y, fillW, barH), lighter, accent, 90f))
                    g.FillPath(grad, fillPath);

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
                string lbl = "5h";
                SizeF sz = g.MeasureString(lbl, f);
                g.DrawString(lbl, f, br, cx - sz.Width / 2f, r.Bottom - sz.Height);
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
                string lbl = T("週", "Wk");
                SizeF sz = g.MeasureString(lbl, f);
                g.DrawString(lbl, f, br, cx - sz.Width / 2f, r.Bottom - sz.Height);
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
    }
}
