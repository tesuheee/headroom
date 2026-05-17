using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

class GenIcon
{
    static void Main()
    {
        int[] sizes = { 16, 32, 48, 256 };
        byte[][] pngs = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++)
        {
            using (var bmp = RenderIcon(sizes[i]))
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                pngs[i] = ms.ToArray();
            }
        }
        WriteIco(pngs, sizes, "app.ico");
        Console.WriteLine("app.ico created");
    }

    static Bitmap RenderIcon(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            int pad = Math.Max(1, size / 16);
            int r = size / 5;
            var bgRect = new Rectangle(pad, pad, size - pad * 2, size - pad * 2);

            using (var path = RoundRect(bgRect.X, bgRect.Y, bgRect.Width, bgRect.Height, r))
            {
                using (var grad = new LinearGradientBrush(bgRect, Color.FromArgb(40, 40, 48), Color.FromArgb(20, 20, 26), 90f))
                    g.FillPath(grad, path);
                using (var pen = new Pen(Color.FromArgb(65, 65, 75), Math.Max(1f, size / 50f)))
                    g.DrawPath(pen, path);
            }

            int barPad = size * 22 / 100;
            int barGap = size * 12 / 100;
            int barW = (size - barPad * 2 - barGap) / 2;
            int barH = size * 50 / 100;
            int barY = size * 24 / 100;
            int barR = Math.Max(2, barW / 3);

            DrawBar(g, barPad, barY, barW, barH, barR, 0.70,
                Color.FromArgb(50, 200, 120), Color.FromArgb(26, 160, 80));
            DrawBar(g, barPad + barW + barGap, barY, barW, barH, barR, 0.45,
                Color.FromArgb(80, 170, 255), Color.FromArgb(40, 120, 220));

            int dotSize = Math.Max(2, size * 7 / 100);
            int dotY = barY + barH + size * 8 / 100;
            g.FillEllipse(new SolidBrush(Color.FromArgb(26, 177, 92)),
                barPad + barW / 2 - dotSize / 2, dotY, dotSize, dotSize);
            g.FillEllipse(new SolidBrush(Color.FromArgb(45, 132, 235)),
                barPad + barW + barGap + barW / 2 - dotSize / 2, dotY, dotSize, dotSize);
        }
        return bmp;
    }

    static void DrawBar(Graphics g, int x, int y, int w, int h, int r, double fill,
        Color light, Color dark)
    {
        using (var track = RoundRect(x, y, w, h, r))
        using (var tb = new SolidBrush(Color.FromArgb(16, 16, 20)))
            g.FillPath(tb, track);

        int fh = Math.Max(r * 2, (int)(h * fill));
        int fy = y + h - fh;
        using (var fillPath = RoundRect(x, fy, w, fh, r))
        using (var grad = new LinearGradientBrush(new Rectangle(x, fy, w, Math.Max(2, fh)), light, dark, 90f))
            g.FillPath(grad, fillPath);

        int hlH = Math.Max(1, fh / 3);
        if (hlH > 2 && w > 4)
        {
            using (var hlPath = RoundRect(x + 1, fy + 1, Math.Max(2, w / 2 - 1), hlH, Math.Max(1, r / 2)))
            using (var hl = new SolidBrush(Color.FromArgb(50, 255, 255, 255)))
                g.FillPath(hl, hlPath);
        }
    }

    static GraphicsPath RoundRect(int x, int y, int w, int h, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Max(1, radius * 2);
        if (d > w) d = w;
        if (d > h) d = h;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    static void WriteIco(byte[][] pngs, int[] sizes, string path)
    {
        using (var ms = new MemoryStream())
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write((ushort)0);
            bw.Write((ushort)1);
            bw.Write((ushort)pngs.Length);

            int offset = 6 + 16 * pngs.Length;
            for (int i = 0; i < pngs.Length; i++)
            {
                bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write((ushort)1);
                bw.Write((ushort)32);
                bw.Write((uint)pngs[i].Length);
                bw.Write((uint)offset);
                offset += pngs[i].Length;
            }
            for (int i = 0; i < pngs.Length; i++)
                bw.Write(pngs[i]);

            File.WriteAllBytes(path, ms.ToArray());
        }
    }
}
