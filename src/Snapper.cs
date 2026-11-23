using System;
using System.Collections.Generic;

namespace RttClientDN
{
    /// <summary>
    /// Snapping rules applied while the user moves (WM_MOVING) or resizes (WM_SIZING) a window.
    /// All coordinates are virtual-desktop pixels.
    /// </summary>
    internal static class Snapper
    {
        public const int Threshold = 10;
        public const int MinSize = 32;

        /// <summary>Snap a moving window: edges/centers to other windows, edges flush to display edges.</summary>
        public static RECT SnapMove(RECT r, IList<RECT> others, IList<RECT> monitors)
        {
            var dx = new Best(); var dy = new Best();
            foreach (var o in others)
            {
                dx.Offer(o.Left - r.Left); dx.Offer(o.Right - r.Left);
                dx.Offer(o.Left - r.Right); dx.Offer(o.Right - r.Right);
                dx.Offer((o.Left + o.Right - r.Left - r.Right) / 2);   // centers
                dy.Offer(o.Top - r.Top); dy.Offer(o.Bottom - r.Top);
                dy.Offer(o.Top - r.Bottom); dy.Offer(o.Bottom - r.Bottom);
                dy.Offer((o.Top + o.Bottom - r.Top - r.Bottom) / 2);
            }
            foreach (var m in monitors)
            {
                dx.Offer(m.Left - r.Left); dx.Offer(m.Right - r.Right);
                dy.Offer(m.Top - r.Top); dy.Offer(m.Bottom - r.Bottom);
            }
            return new RECT(r.Left + dx.Delta, r.Top + dy.Delta, r.Right + dx.Delta, r.Bottom + dy.Delta);
        }

        /// <summary>
        /// Snap a resizing window. Only the edge(s) being dragged move. Afterwards, if the size is within
        /// the threshold of the image aspect ratio, snap to that exact ratio.
        /// </summary>
        public static RECT SnapSize(RECT r, int wmszEdge, IList<RECT> others, IList<RECT> monitors, double aspect)
        {
            bool left = wmszEdge == NativeMethods.WMSZ_LEFT || wmszEdge == NativeMethods.WMSZ_TOPLEFT || wmszEdge == NativeMethods.WMSZ_BOTTOMLEFT;
            bool right = wmszEdge == NativeMethods.WMSZ_RIGHT || wmszEdge == NativeMethods.WMSZ_TOPRIGHT || wmszEdge == NativeMethods.WMSZ_BOTTOMRIGHT;
            bool top = wmszEdge == NativeMethods.WMSZ_TOP || wmszEdge == NativeMethods.WMSZ_TOPLEFT || wmszEdge == NativeMethods.WMSZ_TOPRIGHT;
            bool bottom = wmszEdge == NativeMethods.WMSZ_BOTTOM || wmszEdge == NativeMethods.WMSZ_BOTTOMLEFT || wmszEdge == NativeMethods.WMSZ_BOTTOMRIGHT;

            var dl = new Best(); var dr = new Best(); var dt = new Best(); var db = new Best();
            foreach (var o in others)
            {
                if (left) { dl.Offer(o.Left - r.Left); dl.Offer(o.Right - r.Left); dl.Offer(o.Left + o.Right - r.Right - r.Left); }
                if (right) { dr.Offer(o.Left - r.Right); dr.Offer(o.Right - r.Right); dr.Offer(o.Left + o.Right - r.Left - r.Right); }
                if (top) { dt.Offer(o.Top - r.Top); dt.Offer(o.Bottom - r.Top); dt.Offer(o.Top + o.Bottom - r.Bottom - r.Top); }
                if (bottom) { db.Offer(o.Top - r.Bottom); db.Offer(o.Bottom - r.Bottom); db.Offer(o.Top + o.Bottom - r.Top - r.Bottom); }
            }
            foreach (var m in monitors)
            {
                if (left) dl.Offer(m.Left - r.Left);
                if (right) dr.Offer(m.Right - r.Right);
                if (top) dt.Offer(m.Top - r.Top);
                if (bottom) db.Offer(m.Bottom - r.Bottom);
            }
            r.Left += dl.Delta; r.Right += dr.Delta; r.Top += dt.Delta; r.Bottom += db.Delta;

            if (aspect > 0)
            {
                int w = r.Width, h = r.Height;
                int idealH = (int)Math.Round(w / aspect);
                int idealW = (int)Math.Round(h * aspect);
                int errH = Math.Abs(idealH - h), errW = Math.Abs(idealW - w);
                bool horizontalEdge = left || right, verticalEdge = top || bottom;

                // Adjust the dimension the user is NOT dragging when only one is being dragged;
                // for corner drags adjust whichever needs the smaller correction.
                bool fixHeight = horizontalEdge && (!verticalEdge || errH <= errW);
                if (fixHeight && errH <= Threshold)
                {
                    if (top) r.Top = r.Bottom - idealH; else r.Bottom = r.Top + idealH;
                }
                else if (!fixHeight && verticalEdge && errW <= Threshold)
                {
                    if (left) r.Left = r.Right - idealW; else r.Right = r.Left + idealW;
                }
            }

            if (r.Width < MinSize) { if (left) r.Left = r.Right - MinSize; else r.Right = r.Left + MinSize; }
            if (r.Height < MinSize) { if (top) r.Top = r.Bottom - MinSize; else r.Bottom = r.Top + MinSize; }
            return r;
        }

        /// <summary>Tracks the smallest-magnitude offset within the snap threshold.</summary>
        private struct Best
        {
            private bool _any;
            public int Delta;

            public void Offer(int d)
            {
                if (Math.Abs(d) > Threshold) return;
                if (!_any || Math.Abs(d) < Math.Abs(Delta)) { Delta = d; _any = true; }
            }
        }
    }
}
