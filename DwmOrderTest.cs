using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace Spread;

/// <summary>
/// Experiment: in which order does DWM stack several thumbnails inside one destination window?
/// Two plain coloured source windows (red A, blue B) are shown through thumbnails that overlap each other;
/// one pixel in the overlap is read back from the screen after each step. Only pixels inside the test windows are read.
/// </summary>
internal static class DwmOrderTest
{
    static Form Make(string title, Color c, int x, int y, int w, int h, bool topmost)
    {
        var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
            Location = new Point(x, y), Size = new Size(w, h), BackColor = c, ShowInTaskbar = false, TopMost = topmost
        };
        return f;
    }

    static void Pump(int ms)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(10); }
    }

    static void Place(IntPtr th, int l, int t, int r, int b)
    {
        var p = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_VISIBLE | Native.DWM_TNP_OPACITY,
            rcDestination = new RECT { Left = l, Top = t, Right = r, Bottom = b }, opacity = 255, fVisible = true
        };
        Native.DwmUpdateThumbnailProperties(th, ref p);
    }

    static string Sample(Form d, int cx, int cy)
    {
        using var bmp = new Bitmap(1, 1);
        using (var g = Graphics.FromImage(bmp))
        {
            var pt = d.PointToScreen(new Point(cx, cy));
            g.CopyFromScreen(pt.X, pt.Y, 0, 0, new Size(1, 1));
        }
        var c = bmp.GetPixel(0, 0);
        if (c.R > 180 && c.B < 90) return "A (red)";
        if (c.B > 180 && c.R < 90) return "B (blue)";
        return $"other ({c.R},{c.G},{c.B})";
    }

    static IntPtr Reg(Form dest, Form src, int l, int t, int r, int b)
    {
        Native.DwmRegisterThumbnail(dest.Handle, src.Handle, out var th);
        Place(th, l, t, r, b);
        return th;
    }

    public static int Run()
    {
        foreach (bool topmostDest in new[] { false, true })
        {
            Console.WriteLine($"--- destination window topmost = {topmostDest}");
            var a = Make("A", Color.Red, 40, 60, 300, 200, false);
            var b = Make("B", Color.Blue, 400, 60, 300, 200, false);
            var d = Make("D", Color.Black, 40, 330, 760, 300, topmostDest);
            a.Show(); b.Show(); d.Show();
            Pump(500);

            // dest rects: A at x 50-450, B at x 250-650, overlap 250-450; sample inside the overlap
            IntPtr ta = Reg(d, a, 50, 50, 450, 250);
            IntPtr tb = Reg(d, b, 250, 50, 650, 250);
            Pump(500);
            Console.WriteLine("1. registered A, then B                       -> top: " + Sample(d, 350, 150));

            Native.SetWindowPos(a.Handle, IntPtr.Zero, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            Pump(500);
            Console.WriteLine("2. real window A raised above B (z-order)     -> top: " + Sample(d, 350, 150));

            Native.DwmUnregisterThumbnail(ta); ta = Reg(d, a, 50, 50, 450, 250);
            Pump(500);
            Console.WriteLine("3. A unregistered + registered again (last)   -> top: " + Sample(d, 350, 150));

            Native.DwmUnregisterThumbnail(tb); tb = Reg(d, b, 250, 50, 650, 250);
            Pump(500);
            Console.WriteLine("4. B unregistered + registered again (last)   -> top: " + Sample(d, 350, 150));

            // re-applying properties of A: does that change the order?
            Place(ta, 50, 50, 450, 250);
            Pump(300);
            Console.WriteLine("5. only A's properties updated again          -> top: " + Sample(d, 350, 150));

            Native.DwmUnregisterThumbnail(ta); Native.DwmUnregisterThumbnail(tb);
            a.Close(); b.Close(); d.Close();
            Pump(200);
        }
        return 0;
    }
}
