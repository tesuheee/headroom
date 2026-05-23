using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Headroom
{
    sealed class SettingsForm : Form
    {
        readonly WidgetSettings settings;
        readonly WidgetSettings original;
        readonly Action preview;
        readonly ToolTip tooltips = new ToolTip();
        readonly bool fixtureMode;
        bool _updatingLanguage;
        DarkScrollContainer scrollContainer;
        Label versionLocLabel, settingsLocLabel, authLocLabel, logsLocLabel;
        bool _resizing;
        Point _resizeStart;
        Size  _resizeStartSize;

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int sz);

        readonly DarkComboBox language = new DarkComboBox();
        readonly DarkComboBox layoutMode = new DarkComboBox();
        readonly DarkComboBox serviceOrder = new DarkComboBox();
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
        readonly DarkComboBox claudeLoginMethod = new DarkComboBox();
        readonly DarkComboBox codexLoginMethod = new DarkComboBox();
        readonly DarkTextBox warningPercent = new DarkTextBox();
        readonly DarkTextBox criticalPercent = new DarkTextBox();

        Func<bool> claudeLoggedIn;
        Func<bool> codexLoggedIn;
        Func<Task> logoutClaude;
        Func<Task> logoutCodex;
        public bool LoginClaude { get; private set; }
        public bool LoginCodex  { get; private set; }
        public bool ResetRequested { get; private set; }

        public SettingsForm(WidgetSettings settings, Action preview,
            Func<bool> claudeLoggedIn, Func<bool> codexLoggedIn,
            Func<Task> logoutClaude, Func<Task> logoutCodex,
            bool fixtureMode)
        {
            this.settings = settings;
            this.original = settings.Clone();
            this.preview = preview;
            this.claudeLoggedIn = claudeLoggedIn;
            this.codexLoggedIn  = codexLoggedIn;
            this.logoutClaude   = logoutClaude;
            this.logoutCodex    = logoutCodex;
            this.fixtureMode = fixtureMode;
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

            if (fixtureMode)
            {
                var fixtureBadge = new Label
                {
                    Text = "FIXTURE",
                    Location = new Point(112, 20),
                    Width = 74,
                    Height = 18,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Font = new Font("Yu Gothic UI", 8f, FontStyle.Bold),
                    ForeColor = Color.FromArgb(170, 210, 255),
                    BackColor = Color.FromArgb(28, 50, 76)
                };
                Controls.Add(fixtureBadge);
            }

            var cancel = new RoundButton {
                Text = T("キャンセル", "Cancel"), Tag = "キャンセル|Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(Width - 218, 12), Width = 96, Height = 34,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                FillColor = Color.FromArgb(32, 32, 38),
                ForeColor = Color.FromArgb(200, 206, 218),
                Font = new Font("Yu Gothic UI", 10.5f),
                CornerRadius = 0,
                HoverBackColor   = Color.FromArgb(48, 48, 56),
                PressedBackColor = Color.FromArgb(28, 28, 34),
                BorderColorNormal = Color.FromArgb(60, 60, 68)
            };
            var ok = new RoundButton {
                Text = T("保存", "Save"), Tag = "保存|Save",
                DialogResult = DialogResult.OK,
                Location = new Point(Width - 114, 12), Width = 90, Height = 34,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                FillColor = Color.FromArgb(45, 132, 235),
                ForeColor = Color.White,
                Font = new Font("Yu Gothic UI", 10.5f, FontStyle.Bold),
                CornerRadius = 0,
                HoverBackColor   = Color.FromArgb(72, 152, 250),
                PressedBackColor = Color.FromArgb(35, 112, 210),
                BorderColorNormal = Color.FromArgb(45, 132, 235)
            };
            var reset = new RoundButton {
                Text = T("初期化", "Reset"), Tag = "初期化|Reset",
                Location = new Point(Width - 316, 12), Width = 90, Height = 34,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                FillColor = Color.FromArgb(32, 32, 38),
                ForeColor = Color.FromArgb(200, 206, 218),
                Font = new Font("Yu Gothic UI", 10.5f),
                CornerRadius = 0,
                HoverBackColor   = Color.FromArgb(44, 44, 52),
                PressedBackColor = Color.FromArgb(28, 28, 34),
                BorderColorNormal = Color.FromArgb(60, 60, 68)
            };
            reset.Click += (s, e) =>
            {
                if (MessageBox.Show(
                    T("設定をすべて初期値に戻しますか？", "Reset all settings to defaults?"),
                    T("設定を初期化", "Reset settings"),
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    ResetRequested = true;
                    DialogResult = DialogResult.OK;
                    Close();
                }
            };
            ok.Click += (s, e) => ApplyToSettings();
            Controls.Add(reset);
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
            AddSection(leftCard, "アカウント", "Account", ref leftY);
            AddRow(leftCard, "Claude ログイン方法", "Claude login method", "ブラウザOAuth / CLI", "Browser OAuth / CLI", claudeLoginMethod, ref leftY);
            AddRow(leftCard, "Codex ログイン方法", "Codex login method", "ブラウザOAuth / CLI", "Browser OAuth / CLI", codexLoginMethod, ref leftY);
            AddAccountRow(leftCard, "Claude", claudeLoggedIn(), logoutClaude, ref leftY, true);
            AddAccountRow(leftCard, "Codex",  codexLoggedIn(),  logoutCodex,  ref leftY, false);
            AddSection(leftCard, "レイアウト", "Layout", ref leftY);
            AddRow(leftCard, "配置", "Arrangement", "", "", layoutMode, ref leftY);
            AddRow(leftCard, "表示順", "Service order", "先頭のカード", "First card", serviceOrder, ref leftY);
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
            SetupCombo(serviceOrder, settings.ServiceOrder, new[] { "Claude / Codex", "Codex / Claude" });
            SetupCombo(codexMode, settings.CodexShowUsed ? "used" : "remaining", new[] { T("残量", "Remaining"), T("使用量", "Used") });
            SetupCombo(claudeMode, settings.ClaudeShowUsed ? "used" : "remaining", new[] { T("残量", "Remaining"), T("使用量", "Used") });
            SetupCombo(fiveResetMode, settings.FiveHourResetMode, new[] { T("リセット時刻", "Clock time"), T("残り時間", "Time left") });
            SetupCombo(weeklyResetMode, settings.WeeklyResetMode, new[] { T("リセット時刻", "Clock time"), T("残り時間", "Time left") });
            SetupCombo(claudeLoginMethod, settings.ClaudeLoginMethod, new[] { T("ブラウザOAuth", "Browser OAuth"), "CLI", T("自動", "Auto") });
            SetupCombo(codexLoginMethod, settings.CodexLoginMethod, new[] { T("ブラウザOAuth", "Browser OAuth"), "CLI", T("自動", "Auto") });
            SetupCombo(language, settings.Language, new[] { "日本語", "English" });
            SetupNumbers();

            int contentH = Math.Max(leftY + 16, rightY + 16);
            leftCard.Height = contentH;
            rightCard.Height = contentH;
            vDivider.Height = contentH;

            var locFont   = new Font("Yu Gothic UI", 9f);
            var locNormal = Color.FromArgb(100, 106, 120);
            var locHover  = Color.FromArgb(170, 178, 200);

            Func<int, Label> makeLocLabel = offsetY =>
            {
                var lbl = new Label
                {
                    Location  = new Point(24, contentH + 4 + offsetY),
                    Width     = body.Width - 48,
                    Height    = 17,
                    Padding   = new Padding(2, 0, 2, 0),
                    Font      = locFont,
                    ForeColor = locNormal,
                    BackColor = Color.Transparent,
                    Cursor    = Cursors.Hand
                };
                lbl.MouseEnter += (s, e) => lbl.ForeColor = locHover;
                lbl.MouseLeave += (s, e) => lbl.ForeColor = locNormal;
                return lbl;
            };

            versionLocLabel = makeLocLabel(0);
            body.Controls.Add(versionLocLabel);

            settingsLocLabel = makeLocLabel(19);
            settingsLocLabel.Click += (s, e) => OpenLocation(WidgetSettings.SettingsPath, selectFile: true);
            body.Controls.Add(settingsLocLabel);

            authLocLabel = makeLocLabel(38);
            authLocLabel.Click += (s, e) => OpenLocation(UsageForm.ClaudeCredentialPath, selectFile: true);
            body.Controls.Add(authLocLabel);

            logsLocLabel = makeLocLabel(57);
            logsLocLabel.Click += (s, e) => OpenLocation(UsageForm.DebugDirectory, selectFile: false);
            body.Controls.Add(logsLocLabel);

            UpdateLocationsLabel();

            int totalContentH = logsLocLabel.Bottom + 4;
            scrollContainer.SetContentHeight(totalContentH);
            scrollContainer.AttachWheelToChildren();

            int idealFormH = Math.Min(Screen.PrimaryScreen.WorkingArea.Height - 80, totalContentH + 57);
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
                if (pt.Y >= 0 && pt.Y < 56 && pt.X < Width - 325)
                    m.Result = (IntPtr)HTCAPTION;
            }
        }

        void AddAccountRow(Panel parent, string serviceName, bool loggedIn, Func<Task> logout, ref int y, bool isClaude)
        {
            int rowH = 50;
            bool awaitingConfirm = false;
            System.Windows.Forms.Timer confirmTimer = null;

            Color signedInColor  = Color.FromArgb(140, 210, 160);
            Color signedOutColor = Color.FromArgb(160, 140, 120);
            string statusText = T(serviceName + "：" + (loggedIn ? "ログイン中" : "未ログイン"),
                                  serviceName + ": " + (loggedIn ? "Signed in" : "Not signed in"));
            if (!loggedIn)
                statusText += isClaude
                    ? T("\n/login でサインイン", "\nType /login to sign in")
                    : T("\nブラウザで自動サインイン", "\nBrowser sign-in starts");
            var statusLabel = new Label
            {
                Text = statusText,
                Location = new Point(24, loggedIn ? y + 15 : y + 7),
                Width = 260, Height = loggedIn ? 20 : 38,
                Font = new Font("Yu Gothic UI", 9.2f),
                ForeColor = loggedIn ? signedInColor : signedOutColor
            };
            parent.Controls.Add(statusLabel);

            var btn = new RoundButton
            {
                Text = loggedIn ? T("ログアウト", "Logout") : T("ログイン", "Login"),
                CornerRadius = 0, Width = 110, Height = 30,
                FillColor        = loggedIn ? Color.FromArgb(44, 44, 52) : Color.FromArgb(45, 132, 235),
                ForeColor        = loggedIn ? Color.FromArgb(200, 206, 218) : Color.White,
                Font             = new Font("Yu Gothic UI", 9.5f),
                HoverBackColor   = loggedIn ? Color.FromArgb(56, 56, 66) : Color.FromArgb(62, 148, 250),
                PressedBackColor = loggedIn ? Color.FromArgb(30, 30, 38) : Color.FromArgb(32, 110, 210)
            };
            btn.Location = new Point(parent.Width - 134, y + 10);
            parent.Controls.Add(btn);

            var line = new Panel { Location = new Point(24, y + rowH - 1), Width = parent.Width - 48, Height = 1, BackColor = Color.FromArgb(42, 42, 52) };
            parent.Controls.Add(line);
            y += rowH;

            btn.Click += async (s, e) =>
            {
                if (!loggedIn)
                {
                    if (isClaude) LoginClaude = true; else LoginCodex = true;
                    Close();
                    return;
                }
                if (!awaitingConfirm)
                {
                    awaitingConfirm = true;
                    btn.Text = T("もう一度押して", "Confirm logout");
                    btn.FillColor = Color.FromArgb(140, 45, 45);
                    btn.HoverBackColor = Color.FromArgb(165, 60, 60);
                    btn.Invalidate();
                    if (confirmTimer != null) confirmTimer.Stop();
                    confirmTimer = new System.Windows.Forms.Timer { Interval = 5000 };
                    confirmTimer.Tick += (t, te) =>
                    {
                        if (confirmTimer != null) confirmTimer.Stop();
                        if (!awaitingConfirm) return;
                        awaitingConfirm = false;
                        btn.Text = T("ログアウト", "Logout");
                        btn.FillColor      = Color.FromArgb(44, 44, 52);
                        btn.HoverBackColor = Color.FromArgb(56, 56, 66);
                        btn.Invalidate();
                    };
                    confirmTimer.Start();
                }
                else
                {
                    if (confirmTimer != null) confirmTimer.Stop();
                    awaitingConfirm = false;
                    loggedIn = false;
                    statusLabel.Text = T(serviceName + "：未ログイン", serviceName + ": Not signed in");
                    statusLabel.ForeColor = signedOutColor;
                    btn.Text = T("ログイン", "Login");
                    btn.FillColor        = Color.FromArgb(45, 132, 235);
                    btn.HoverBackColor   = Color.FromArgb(62, 148, 250);
                    btn.ForeColor        = Color.White;
                    btn.Invalidate();
                    await logout();
                }
            };
        }

        void AddSection(Panel parent, string ja, string en, ref int y)
        {
            y += 24;
            var bar = new Panel { Location = new Point(20, y + 3), Width = 3, Height = 16, BackColor = Color.FromArgb(45, 132, 235) };
            parent.Controls.Add(bar);
            var label = new Label
            {
                Text = T(ja, en),
                Tag = ja + "|" + en,
                Location = new Point(30, y),
                Width = 260,
                Height = 22,
                Font = new Font("Yu Gothic UI", 11f, FontStyle.Bold),
                ForeColor = Color.FromArgb(185, 190, 212)
            };
            parent.Controls.Add(label);
            y += 30;
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
            else if (box == serviceOrder)                       box.SelectedIndex = string.Equals(value, "codex-claude", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            else if (box == codexMode || box == claudeMode)    box.SelectedIndex = string.Equals(value, "used",      StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            else if (box == fiveResetMode || box == weeklyResetMode) box.SelectedIndex = string.Equals(value, "relative", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            else if (box == claudeLoginMethod || box == codexLoginMethod) box.SelectedIndex = LoginMethodIndex(value);
            else if (box == topMost)                           box.SelectedIndex = string.Equals(value, "enabled",   StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            else if (box == showCodex || box == showClaude)   box.SelectedIndex = string.Equals(value, "enabled",   StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            else if (box == language)                          box.SelectedIndex = string.Equals(value, "en",        StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }

        static int LoginMethodIndex(string value)
        {
            if (string.Equals(value, "cli", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase)) return 2;
            return 0;
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

        void UpdateLocationsLabel()
        {
            if (versionLocLabel == null) return;
            versionLocLabel.Text  = T("バージョン: ", "Version: ") + AppInfo.DisplayVersion;
            settingsLocLabel.Text = T("設定ファイル: ", "Settings: ") + WidgetSettings.SettingsPath;
            authLocLabel.Text     = T("認証ファイル: ", "Auth: ") + UsageForm.ClaudeCredentialPath + " / " + UsageForm.CodexCredentialPath;
            logsLocLabel.Text     = T("ログ: ", "Logs: ") + UsageForm.DebugDirectory;
            tooltips.SetToolTip(settingsLocLabel, T("クリックで設定ファイルを選択表示", "Click to reveal the settings file"));
            tooltips.SetToolTip(authLocLabel,     T("クリックで認証ファイルを選択表示", "Click to reveal the auth file"));
            tooltips.SetToolTip(logsLocLabel,     T("クリックでログフォルダを開く",     "Click to open the logs folder"));
        }

        void OpenLocation(string path, bool selectFile)
        {
            try
            {
                if (selectFile)
                {
                    string dir = Path.GetDirectoryName(path);
                    Directory.CreateDirectory(dir);
                    if (path == WidgetSettings.SettingsPath && !File.Exists(path)) settings.Save();
                    Process.Start("explorer.exe", "/select,\"" + path + "\"");
                }
                else
                {
                    Directory.CreateDirectory(path);
                    Process.Start("explorer.exe", "\"" + path + "\"");
                }
            }
            catch { }
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
                    UpdateLocationsLabel();
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
            claudeLoginMethod.SelectedIndexChanged += apply;
            codexLoginMethod.SelectedIndexChanged += apply;
            layoutMode.SelectedIndexChanged += apply;
            serviceOrder.SelectedIndexChanged += apply;
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
            int orderSel    = serviceOrder.SelectedIndex;
            int codexSel    = codexMode.SelectedIndex;
            int claudeSel   = claudeMode.SelectedIndex;
            int fiveSel     = fiveResetMode.SelectedIndex;
            int weeklySel   = weeklyResetMode.SelectedIndex;
            int topMostSel  = topMost.SelectedIndex;
            int showCxSel   = showCodex.SelectedIndex;
            int showClSel   = showClaude.SelectedIndex;
            int claudeLoginSel = claudeLoginMethod.SelectedIndex;
            int codexLoginSel  = codexLoginMethod.SelectedIndex;

            layoutMode.Items.Clear();
            layoutMode.Items.AddRange(new[] { T("横", "Wide"), T("縦", "Tall") });
            layoutMode.SelectedIndex = Math.Max(0, Math.Min(1, layoutSel));

            serviceOrder.Items.Clear();
            serviceOrder.Items.AddRange(new[] { "Claude / Codex", "Codex / Claude" });
            serviceOrder.SelectedIndex = Math.Max(0, Math.Min(1, orderSel));

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

            claudeLoginMethod.Items.Clear();
            claudeLoginMethod.Items.AddRange(new[] { T("ブラウザOAuth", "Browser OAuth"), "CLI", T("自動", "Auto") });
            claudeLoginMethod.SelectedIndex = Math.Max(0, Math.Min(2, claudeLoginSel));

            codexLoginMethod.Items.Clear();
            codexLoginMethod.Items.AddRange(new[] { T("ブラウザOAuth", "Browser OAuth"), "CLI", T("自動", "Auto") });
            codexLoginMethod.SelectedIndex = Math.Max(0, Math.Min(2, codexLoginSel));
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
            settings.ClaudeLoginMethod = LoginMethodValue(claudeLoginMethod.SelectedIndex);
            settings.CodexLoginMethod = LoginMethodValue(codexLoginMethod.SelectedIndex);
            settings.LayoutMode = layoutMode.SelectedIndex == 1 ? "vertical" : "horizontal";
            settings.ServiceOrder = serviceOrder.SelectedIndex == 1 ? "codex-claude" : "claude-codex";
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

        static string LoginMethodValue(int selectedIndex)
        {
            if (selectedIndex == 1) return "cli";
            if (selectedIndex == 2) return "auto";
            return "browser";
        }

        string T(string ja, string en)
        {
            return string.Equals(settings.Language, "en", StringComparison.OrdinalIgnoreCase) ? en : ja;
        }
    }
}
