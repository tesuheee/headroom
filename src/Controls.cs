using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Headroom
{
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
        public Color FillColor = Color.Empty;
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
            BackColor = Color.Transparent;
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

                Color fill = FillColor != Color.Empty ? FillColor : Color.FromArgb(32, 32, 38);
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
