using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace autocad_final.Agent
{
    /// <summary>
    /// Captures the AutoCAD main window and returns a base64-encoded PNG,
    /// optionally with red-circle overlays at the supplied screen-space points.
    /// Used by the vision-feedback loop after each successful write tool.
    /// </summary>
    internal static class DrawingScreenCapture
    {
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        public static string CaptureBase64Png(int maxWidthPx = 1280)
        {
            return CaptureWithOverlay(null, maxWidthPx);
        }

        /// <summary>
        /// Captures the AutoCAD main window. If <paramref name="screenMarkers"/> contains
        /// screen-space pixel coordinates, red markers are drawn at each one so the
        /// vision model can see exactly where validators flagged issues.
        /// </summary>
        public static string CaptureWithOverlay(IList<PointF> screenMarkers, int maxWidthPx = 1280)
        {
            try
            {
                IntPtr hwnd;
                try { hwnd = Autodesk.AutoCAD.ApplicationServices.Application.MainWindow.Handle; }
                catch { return null; }
                if (hwnd == IntPtr.Zero) return null;

                if (!GetWindowRect(hwnd, out RECT rc)) return null;
                int srcW = rc.Right - rc.Left;
                int srcH = rc.Bottom - rc.Top;
                if (srcW <= 0 || srcH <= 0) return null;

                double scale = srcW > maxWidthPx ? (double)maxWidthPx / srcW : 1.0;
                int dstW = Math.Max(1, (int)(srcW * scale));
                int dstH = Math.Max(1, (int)(srcH * scale));

                using (var src = new Bitmap(srcW, srcH, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(src))
                    {
                        g.CopyFromScreen(rc.Left, rc.Top, 0, 0, new Size(srcW, srcH), CopyPixelOperation.SourceCopy);

                        if (screenMarkers != null && screenMarkers.Count > 0)
                        {
                            using (var pen = new Pen(Color.FromArgb(240, 255, 40, 40), 3f))
                            {
                                foreach (var pt in screenMarkers)
                                {
                                    float px = pt.X - rc.Left;
                                    float py = pt.Y - rc.Top;
                                    if (px < 0 || py < 0 || px > srcW || py > srcH) continue;
                                    g.DrawEllipse(pen, px - 14, py - 14, 28, 28);
                                    g.DrawLine(pen, px - 10, py, px + 10, py);
                                    g.DrawLine(pen, px, py - 10, px, py + 10);
                                }
                            }
                        }
                    }

                    using (var dst = new Bitmap(dstW, dstH, PixelFormat.Format32bppArgb))
                    {
                        using (var g2 = Graphics.FromImage(dst))
                        {
                            g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g2.DrawImage(src, 0, 0, dstW, dstH);
                        }

                        using (var ms = new MemoryStream())
                        {
                            dst.Save(ms, ImageFormat.Png);
                            return Convert.ToBase64String(ms.ToArray());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AgentLog.Write("DrawingScreenCapture", "capture failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Captures the full virtual screen (all monitors) as PNG, downscaled so the longest edge is at most <paramref name="maxEdgePx"/>.
        /// Used when the user explicitly sends a screen capture to the vision model.
        /// </summary>
        public static string CaptureVirtualScreenBase64Png(int maxEdgePx = 1920)
        {
            try
            {
                var vs = SystemInformation.VirtualScreen;
                int srcW = vs.Width;
                int srcH = vs.Height;
                if (srcW <= 0 || srcH <= 0)
                    return null;

                double scale = 1.0;
                int maxSrc = Math.Max(srcW, srcH);
                if (maxSrc > maxEdgePx && maxEdgePx > 0)
                    scale = (double)maxEdgePx / maxSrc;
                int dstW = Math.Max(1, (int)Math.Round(srcW * scale));
                int dstH = Math.Max(1, (int)Math.Round(srcH * scale));

                using (var src = new Bitmap(srcW, srcH, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(src))
                    {
                        g.CopyFromScreen(vs.Left, vs.Top, 0, 0, new Size(srcW, srcH), CopyPixelOperation.SourceCopy);
                    }

                    using (var dst = new Bitmap(dstW, dstH, PixelFormat.Format32bppArgb))
                    {
                        using (var g2 = Graphics.FromImage(dst))
                        {
                            g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g2.DrawImage(src, 0, 0, dstW, dstH);
                        }

                        using (var ms = new MemoryStream())
                        {
                            dst.Save(ms, ImageFormat.Png);
                            return Convert.ToBase64String(ms.ToArray());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AgentLog.Write("DrawingScreenCapture", "virtual screen capture failed: " + ex.Message);
                return null;
            }
        }
    }
}
