using System;
using System.Collections.Generic;

namespace RttClientDN
{
    /// <summary>
    /// Snapping for one interactive move/size operation (WM_ENTERSIZEMOVE .. WM_EXITSIZEMOVE).
    /// All coordinates are virtual-desktop pixels.
    ///
    /// Rules: an edge/center/display/aspect snap engages when within <see cref="Threshold"/> of its target.
    /// It releases once the mouse has travelled more than <see cref="ReleaseDistance"/> since it engaged
    /// (or leaves the snap zone). A target released by travel is then ignored until the mouse leaves its zone,
    /// so it doesn't immediately re-grab.
    ///
    /// The rects Windows passes in WM_MOVING / WM_SIZING are always derived from the cursor (not from
    /// our previous adjustment), so they are the "raw" mouse-driven positions used for travel.
    /// </summary>
    internal sealed class SnapSession
    {
        public const int Threshold = 10;
        public const int ReleaseDistance = 10;
        public const int MinSize = 32;

        private readonly Axis _moveX = new Axis(), _moveY = new Axis();
        private readonly Axis _left = new Axis(), _right = new Axis(), _top = new Axis(), _bottom = new Axis();
        private bool _aspectEngaged, _aspectSuppressed;
        private int _aspectEntryW, _aspectEntryH;

        /// <summary>Snap a moving window: edges/centers to other windows, edges flush to display edges.</summary>
        public RECT SnapMove(RECT r, IList<RECT> others, IList<RECT> monitors)
        {
            int w = r.Width, h = r.Height;
            var xs = new List<int>(); var ys = new List<int>();
            foreach (var o in others)
            {
                xs.Add(o.Left); xs.Add(o.Right); xs.Add(o.Left - w); xs.Add(o.Right - w);
                xs.Add((o.Left + o.Right - w) / 2);
                ys.Add(o.Top); ys.Add(o.Bottom); ys.Add(o.Top - h); ys.Add(o.Bottom - h);
                ys.Add((o.Top + o.Bottom - h) / 2);
            }
            foreach (var m in monitors)
            {
                xs.Add(m.Left); xs.Add(m.Right - w);
                ys.Add(m.Top); ys.Add(m.Bottom - h);
            }
            int x = _moveX.Apply(r.Left, xs);
            int y = _moveY.Apply(r.Top, ys);
            return RECT.FromXYWH(x, y, w, h);
        }

        /// <summary>
        /// Snap a resizing window. Only the edge(s) being dragged move. Afterwards, if the size is within
        /// the threshold of the image aspect ratio, snap to that exact ratio.
        /// </summary>
        public RECT SnapSize(RECT raw, int wmszEdge, IList<RECT> others, IList<RECT> monitors, double aspect)
        {
            bool left = wmszEdge == NativeMethods.WMSZ_LEFT || wmszEdge == NativeMethods.WMSZ_TOPLEFT || wmszEdge == NativeMethods.WMSZ_BOTTOMLEFT;
            bool right = wmszEdge == NativeMethods.WMSZ_RIGHT || wmszEdge == NativeMethods.WMSZ_TOPRIGHT || wmszEdge == NativeMethods.WMSZ_BOTTOMRIGHT;
            bool top = wmszEdge == NativeMethods.WMSZ_TOP || wmszEdge == NativeMethods.WMSZ_TOPLEFT || wmszEdge == NativeMethods.WMSZ_TOPRIGHT;
            bool bottom = wmszEdge == NativeMethods.WMSZ_BOTTOM || wmszEdge == NativeMethods.WMSZ_BOTTOMLEFT || wmszEdge == NativeMethods.WMSZ_BOTTOMRIGHT;

            var r = raw;
            if (left)
            {
                var c = new List<int>();
                foreach (var o in others) { c.Add(o.Left); c.Add(o.Right); c.Add(o.Left + o.Right - raw.Right); }
                foreach (var m in monitors) c.Add(m.Left);
                r.Left = _left.Apply(raw.Left, c);
            }
            if (right)
            {
                var c = new List<int>();
                foreach (var o in others) { c.Add(o.Left); c.Add(o.Right); c.Add(o.Left + o.Right - raw.Left); }
                foreach (var m in monitors) c.Add(m.Right);
                r.Right = _right.Apply(raw.Right, c);
            }
            if (top)
            {
                var c = new List<int>();
                foreach (var o in others) { c.Add(o.Top); c.Add(o.Bottom); c.Add(o.Top + o.Bottom - raw.Bottom); }
                foreach (var m in monitors) c.Add(m.Top);
                r.Top = _top.Apply(raw.Top, c);
            }
            if (bottom)
            {
                var c = new List<int>();
                foreach (var o in others) { c.Add(o.Top); c.Add(o.Bottom); c.Add(o.Top + o.Bottom - raw.Top); }
                foreach (var m in monitors) c.Add(m.Bottom);
                r.Bottom = _bottom.Apply(raw.Bottom, c);
            }

            if (aspect > 0)
                r = SnapAspect(r, raw, aspect, left, right, top, bottom);

            if (r.Width < MinSize) { if (left) r.Left = r.Right - MinSize; else r.Right = r.Left + MinSize; }
            if (r.Height < MinSize) { if (top) r.Top = r.Bottom - MinSize; else r.Bottom = r.Top + MinSize; }
            return r;
        }

        private RECT SnapAspect(RECT r, RECT raw, double aspect, bool left, bool right, bool top, bool bottom)
        {
            int w = r.Width, h = r.Height;
            int idealH = (int)Math.Round(w / aspect);
            int idealW = (int)Math.Round(h * aspect);
            int errH = Math.Abs(idealH - h), errW = Math.Abs(idealW - w);
            bool horizontalEdge = left || right, verticalEdge = top || bottom;

            // Adjust the dimension the user is NOT dragging when only one is being dragged;
            // for corner drags adjust whichever needs the smaller correction.
            bool fixHeight = horizontalEdge && (!verticalEdge || errH <= errW);
            int err = fixHeight ? errH : errW;

            if (_aspectEngaged)
            {
                int travel = Math.Max(Math.Abs(raw.Width - _aspectEntryW), Math.Abs(raw.Height - _aspectEntryH));
                if (travel > ReleaseDistance) { _aspectEngaged = false; _aspectSuppressed = true; }
                else if (err > Threshold) _aspectEngaged = false;
            }
            if (_aspectSuppressed && err > Threshold) _aspectSuppressed = false;
            if (!_aspectEngaged && !_aspectSuppressed && err <= Threshold)
            {
                _aspectEngaged = true;
                _aspectEntryW = raw.Width;
                _aspectEntryH = raw.Height;
            }
            if (!_aspectEngaged) return r;

            if (fixHeight) { if (top) r.Top = r.Bottom - idealH; else r.Bottom = r.Top + idealH; }
            else { if (left) r.Left = r.Right - idealW; else r.Right = r.Left + idealW; }
            return r;
        }

        /// <summary>Snap state for one mouse-driven coordinate (a window position or a single edge).</summary>
        private sealed class Axis
        {
            private bool _engaged;
            private int _target, _entry;
            private readonly List<int> _suppressed = new List<int>();

            /// <param name="raw">The cursor-driven coordinate.</param>
            /// <param name="candidates">Absolute coordinates this value may snap to.</param>
            public int Apply(int raw, IEnumerable<int> candidates)
            {
                if (_engaged)
                {
                    if (Math.Abs(raw - _entry) > ReleaseDistance)
                    {
                        _engaged = false;
                        _suppressed.Add(_target); // don't re-grab until the mouse leaves this target's zone
                    }
                    else if (Math.Abs(raw - _target) > Threshold) _engaged = false;
                    else return _target;
                }

                _suppressed.RemoveAll(s => Math.Abs(raw - s) > Threshold);

                int best = 0, bestDist = int.MaxValue;
                foreach (int c in candidates)
                {
                    int d = Math.Abs(c - raw);
                    if (d > Threshold || d >= bestDist || IsSuppressed(c)) continue;
                    best = c; bestDist = d;
                }
                if (bestDist == int.MaxValue) return raw;

                _engaged = true;
                _target = best;
                _entry = raw;
                return best;
            }

            private bool IsSuppressed(int c)
            {
                foreach (int s in _suppressed)
                    if (Math.Abs(s - c) <= 1) return true;
                return false;
            }
        }
    }
}
