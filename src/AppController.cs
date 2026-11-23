using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace RttClientDN
{
    /// <summary>Owns the 7 instrument windows, the tray icon, the menus and the RTT reader thread.</summary>
    internal sealed class AppController
    {
        // Order matches the RTT rect array in FalconSharedMemoryArea2.
        public static readonly string[] InstrumentNames = { "HUD", "PFL", "DED", "RWR", "MFD_L", "MFD_R", "HMS" };

        private readonly Application _app;
        private readonly InstrumentWindow[] _windows = new InstrumentWindow[RttReader.ImageCount];
        private readonly bool[] _visible = new bool[RttReader.ImageCount];
        private readonly RttFrame _frame = new RttFrame();
        private RttReader _reader;
        private TrayIcon _tray;
        private bool _draggable;
        private int _framePosted; // 1 while a frame update is queued on the dispatcher

        public bool IsExiting { get; private set; }

        public AppController(Application app)
        {
            _app = app;
        }

        public void Start()
        {
            var defaults = DefaultLayout();
            for (int i = 0; i < _windows.Length; i++)
            {
                var saved = Settings.Load(InstrumentNames[i]);
                var rect = defaults[i];
                _visible[i] = true;
                if (saved.HasValue)
                {
                    var r = saved.Value.Rect;
                    // Ignore saved positions that are no longer on any display (e.g. a monitor was removed).
                    if (NativeMethods.MonitorFromRect(ref r, NativeMethods.MONITOR_DEFAULTTONULL) != IntPtr.Zero)
                        rect = r;
                    _visible[i] = saved.Value.Visible;
                }
                _windows[i] = new InstrumentWindow(i, InstrumentNames[i], rect, this);
                _windows[i].ShowWaiting();
                if (_visible[i]) _windows[i].Show();
            }

            _tray = new TrayIcon(this);

            _reader = new RttReader(_frame, OnFrameFromReader, OnWaitingFromReader);
            _reader.Start();
        }

        // ---- reader thread callbacks -------------------------------------------------------------

        private void OnFrameFromReader()
        {
            // Coalesce: if the UI hasn't consumed the previous frame yet, it will pick up this one.
            if (Interlocked.Exchange(ref _framePosted, 1) == 0)
                _app.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(ApplyFrame));
        }

        private void OnWaitingFromReader()
        {
            _app.Dispatcher.BeginInvoke(new Action(() =>
            {
                foreach (var w in _windows) w.ShowWaiting();
            }));
        }

        private void ApplyFrame()
        {
            Interlocked.Exchange(ref _framePosted, 0);
            lock (_frame.Lock)
            {
                for (int i = 0; i < _windows.Length; i++)
                    if (_visible[i]) _windows[i].UpdateImage(_frame, _frame.Rects[i]);
            }
        }

        // ---- window state ------------------------------------------------------------------------

        public IList<RECT> OtherWindowRects(InstrumentWindow self) =>
            _windows.Where(w => w != self && _visible[w.Index]).Select(w => w.Rect).ToList();

        public void OnWindowStateChanged(InstrumentWindow w) =>
            Settings.Save(w.InstrumentName, new WindowState { Rect = w.Rect, Visible = _visible[w.Index] });

        public bool IsVisible(int index) => _visible[index];

        public void SetVisible(int index, bool visible)
        {
            _visible[index] = visible;
            var w = _windows[index];
            if (visible)
            {
                w.ShowWaiting(); // repainted with the next frame
                w.Show();
            }
            else w.Hide();
            OnWindowStateChanged(w);
        }

        public void ShowAll()
        {
            for (int i = 0; i < _windows.Length; i++)
                if (!_visible[i]) SetVisible(i, true);
        }

        public bool Draggable => _draggable;

        public void ToggleDraggable()
        {
            _draggable = !_draggable;
            foreach (var w in _windows) w.Draggable = _draggable;
        }

        /// <summary>Expand the window to fill the display that contains its center point.</summary>
        public void FillCurrentMonitor(InstrumentWindow w)
        {
            var r = w.Rect;
            var center = new POINT((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
            var mi = NativeMethods.GetMonitorInfoFor(NativeMethods.MonitorFromPoint(center, NativeMethods.MONITOR_DEFAULTTONEAREST));
            if (mi == null) return;
            w.SetRect(mi.Value.rcMonitor);
            OnWindowStateChanged(w);
        }

        public void Exit()
        {
            if (IsExiting) return;
            IsExiting = true;
            _reader?.RequestShutdown(); // background thread; notices within the 3s idle timeout
            foreach (var w in _windows) OnWindowStateChanged(w);
            _tray?.Dispose();
            _app.Shutdown();
        }

        // ---- menus -------------------------------------------------------------------------------

        public void ShowContextMenu(InstrumentWindow w)
        {
            var menu = new ContextMenu();

            var drag = new MenuItem { Header = "Toggle draggable border", IsCheckable = true, IsChecked = _draggable };
            drag.Click += (s, e) => ToggleDraggable();
            menu.Items.Add(drag);

            var fill = new MenuItem { Header = "Fill current monitor" };
            fill.Click += (s, e) => FillCurrentMonitor(w);
            menu.Items.Add(fill);

            var windows = new MenuItem { Header = "Hide or Show windows" };
            for (int i = 0; i < _windows.Length; i++)
            {
                int idx = i;
                var item = new MenuItem { Header = InstrumentNames[i], IsCheckable = true, IsChecked = _visible[i], StaysOpenOnClick = true };
                item.Click += (s, e) => SetVisible(idx, item.IsChecked);
                windows.Items.Add(item);
            }
            menu.Items.Add(windows);

            menu.Items.Add(new Separator());

            var exit = new MenuItem { Header = "Exit RttClientDN.exe" };
            exit.Click += (s, e) => Exit();
            menu.Items.Add(exit);

            // Place at the cursor explicitly. PlacementMode.MousePoint uses WPF's last-seen mouse position, which is
            // stale when the click arrived as a non-client message (draggable mode) or the window was moved natively.
            menu.PlacementTarget = w;
            menu.Placement = PlacementMode.Relative;
            if (NativeMethods.GetCursorPos(out var cursor))
            {
                var r = w.Rect;
                var dpi = VisualTreeHelper.GetDpi(w);
                menu.HorizontalOffset = (cursor.X - r.Left) / dpi.DpiScaleX;
                menu.VerticalOffset = (cursor.Y - r.Top) / dpi.DpiScaleY;
            }
            menu.IsOpen = true;
        }

        // ---- default layout ----------------------------------------------------------------------

        private static RECT[] DefaultLayout()
        {
            var mi = NativeMethods.GetMonitorInfoFor(NativeMethods.MonitorFromPoint(new POINT(0, 0), NativeMethods.MONITOR_DEFAULTTOPRIMARY));
            var work = mi?.rcWork ?? new RECT(0, 0, 1920, 1080);

            const int margin = 20, gap = 10;
            int perRow = 4;
            int size = Math.Max(SnapSession.MinSize, Math.Min(400, (work.Width - 2 * margin - (perRow - 1) * gap) / perRow));
            var rects = new RECT[RttReader.ImageCount];
            for (int i = 0; i < rects.Length; i++)
            {
                int col = i % perRow, row = i / perRow;
                rects[i] = RECT.FromXYWH(work.Left + margin + col * (size + gap), work.Top + margin + row * (size + gap), size, size);
            }
            return rects;
        }
    }
}
