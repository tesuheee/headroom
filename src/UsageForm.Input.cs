using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Headroom
{
    partial class UsageForm
    {
        void OnMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            string key = HitKey(e.Location);
            if (key.Length > 0)
            {
                BeginInvoke(new Action(async () => await HandleClickAsync(key)));
                return;
            }

            string sk = SilentHitKey(e.Location);
            if (sk.Length > 0)
            {
                pendingSilentKey = sk;
                pendingSilentScreenStart = PointToScreen(e.Location);
                pendingSilentWindowStart = Location;
                silentDragging = false;
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
                await ShowSettingsDialog();
                return;
            }
            if (key == "close")
            {
                Close();
                return;
            }

            if (key.EndsWith("-fiveMode") || key.EndsWith("-weekMode"))
            {
                if (key.StartsWith("codex")) settings.CodexShowUsed = !settings.CodexShowUsed;
                else settings.ClaudeShowUsed = !settings.ClaudeShowUsed;
                settings.Save();
                Invalidate();
                return;
            }
            if (key.EndsWith("-fiveResetLabel"))
            {
                settings.FiveHourResetMode = string.Equals(settings.FiveHourResetMode, "relative", StringComparison.OrdinalIgnoreCase) ? "time" : "relative";
                settings.Save();
                Invalidate();
                return;
            }
            if (key.EndsWith("-weekResetLabel"))
            {
                settings.WeeklyResetMode = string.Equals(settings.WeeklyResetMode, "relative", StringComparison.OrdinalIgnoreCase) ? "time" : "relative";
                settings.Save();
                Invalidate();
                return;
            }

            ServiceState service = key.StartsWith("codex") ? codex : claude;
            if (key.EndsWith("refresh"))
                await RefreshServiceAsync(service, true);
            else if (key.EndsWith("boost"))
            {
                bool activated = ToggleBoost(service);
                if (activated)
                    await RefreshServiceAsync(service, true);
            }
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

        bool ToggleBoost(ServiceState service)
        {
            if (service.BoostUntil.HasValue && service.BoostUntil.Value > DateTime.Now)
            {
                service.BoostUntil = null;
                Invalidate();
                return false;
            }
            service.BoostUntil = DateTime.Now.AddMinutes(Math.Max(1, settings.BoostDurationMinutes));
            Invalidate();
            return true;
        }

        string SilentHitKey(Point p)
        {
            foreach (var kv in silentHits)
                if (kv.Value.Contains(p)) return kv.Key;
            return "";
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

        void UpdateSideRailVisibilityFromCursor()
        {
            Point p = PointToClient(Cursor.Position);
            bool visible = ClientRectangle.Contains(p);
            if (!visible)
            {
                if (!sideRailVisible && hoverKey.Length == 0) return;
                sideRailVisible = false;
                hoverKey = "";
                Cursor = Cursors.Default;
                toolTip.Hide(this);
                tooltipTimer.Stop();
                Invalidate();
                return;
            }

            if (!sideRailVisible)
                sideRailVisible = true;

            RegisterSideRailHits();
            string key = HitKey(p);
            string silentKey = SilentHitKey(p);
            Cursor = (key.Length > 0 || silentKey.Length > 0) ? Cursors.Hand : Cursors.Default;
            if (hoverKey == key) return;
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
                    pendingTooltipLocation = new Point(p.X + 12, p.Y + 18);
                pendingTooltipText = tip;
                tooltipTimer.Start();
            }
            Invalidate();
        }

        void UpdateSideRailOpacity()
        {
            double target = sideRailVisible ? 1.0 : 0.0;
            double diff = target - sideRailOpacity;
            if (Math.Abs(diff) < 0.03)
            {
                sideRailOpacity = target;
                return;
            }
            sideRailOpacity += diff * 0.28;
        }

        void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (pendingSilentKey.Length > 0)
            {
                Point screenNow = PointToScreen(e.Location);
                int dx = screenNow.X - pendingSilentScreenStart.X;
                int dy = screenNow.Y - pendingSilentScreenStart.Y;
                if (!silentDragging && Math.Abs(dx) + Math.Abs(dy) > 4)
                    silentDragging = true;
                if (silentDragging)
                    Location = new Point(pendingSilentWindowStart.X + dx, pendingSilentWindowStart.Y + dy);
                return;
            }

            UpdateSideRailVisibilityFromCursor();
            RegisterSideRailHits();

            string key = HitKey(e.Location);
            string sk = SilentHitKey(e.Location);
            Cursor = (key.Length > 0 || sk.Length > 0) ? Cursors.Hand : Cursors.Default;
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

        async Task ShowSettingsDialog()
        {
            string prevLayout     = settings.LayoutMode;
            bool   prevShowCodex  = settings.ShowCodex;
            bool   prevShowClaude = settings.ShowClaude;

            using (var dlg = new SettingsForm(
                settings,
                () => { ApplyLayoutMinimumSize(); ApplyIdealSize(); TopMost = settings.AlwaysOnTop; Invalidate(); },
                () => claude.Data.Status != "login_required",
                () => codex.Data.Status != "login_required",
                () => LogoutServiceAsync(claude),
                () => LogoutServiceAsync(codex),
                HeadroomOptions.FixtureMode
            ))
            {
                dlg.ShowDialog(this);
                if (dlg.DialogResult == DialogResult.OK)
                {
                    if (dlg.ResetRequested)
                        settings.ResetToDefaults();

                    bool layoutChanged = settings.LayoutMode    != prevLayout     ||
                                        settings.ShowCodex      != prevShowCodex  ||
                                        settings.ShowClaude     != prevShowClaude;
                    ApplyLayoutMinimumSize();
                    if (layoutChanged || dlg.ResetRequested) ApplyIdealSize();
                    TopMost = settings.AlwaysOnTop;
                    settings.Save();
                }
                if (dlg.LoginClaude) await OpenLoginAsync(claude);
                if (dlg.LoginCodex)  await OpenLoginAsync(codex);
            }
            hoverKey = "";
            Invalidate();
        }
    }
}
