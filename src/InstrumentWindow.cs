using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RttClientDN
{
    /// <summary>
    /// One topmost, borderless window showing a single RTT image, stretched uniformly on black.
    /// Position/size are managed in virtual-desktop pixels via Win32, not WPF's DIP-based Left/Top.
    /// </summary>
    internal sealed class InstrumentWindow : Window
    {
        private const int ResizeBorderPx = 8;
        private static readonly Brush Orange = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00));

        private readonly AppController _controller;
        private readonly Image _image;
        private readonly TextBlock _waitingText;
        private readonly Border _dragOverlay;
        private WriteableBitmap _bitmap;
        private IntPtr _hwnd;
        private RECT _rect;
        private bool _inSizeMove;
        private bool _draggable;

        public int Index { get; }
        public string InstrumentName { get; }

        /// <summary>Width/height of the current image content, or 0 if unknown.</summary>
        public double ContentAspect { get; private set; }

        public InstrumentWindow(int index, string name, RECT rect, AppController controller)
        {
            Index = index;
            InstrumentName = name;
            _rect = rect;
            _controller = controller;

            Title = "RttClientDN - " + name;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize; // gives WS_THICKFRAME for native sizing; the frame itself is removed in WM_NCCALCSIZE
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Background = Brushes.Black;
            Left = -32000; Top = -32000; Width = 100; Height = 100; // real placement happens in OnSourceInitialized
            UseLayoutRounding = true;

            _image = new Image { Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);

            _waitingText = new TextBlock
            {
                Text = "Waiting...",
                Foreground = Orange,
                FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            _dragOverlay = new Border
            {
                BorderBrush = Orange,
                BorderThickness = new Thickness(2),
                Visibility = Visibility.Collapsed,
                Child = new TextBlock
                {
                    Text = name,
                    Foreground = Orange,
                    Background = new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0)),
                    FontSize = 12,
                    Padding = new Thickness(4, 1, 4, 1),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                },
            };

            var grid = new Grid { Background = Brushes.Black };
            grid.Children.Add(_image);
            grid.Children.Add(_waitingText);
            grid.Children.Add(_dragOverlay);
            Content = grid;

            MouseRightButtonUp += (s, e) => { _controller.ShowContextMenu(this); e.Handled = true; };
        }

        /// <summary>Current window rect in virtual-desktop pixels.</summary>
        public RECT Rect
        {
            get
            {
                if (_hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(_hwnd, out var r)) _rect = r;
                return _rect;
            }
        }

        public void SetRect(RECT r)
        {
            _rect = r;
            if (_hwnd != IntPtr.Zero)
                NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }

        public bool Draggable
        {
            get => _draggable;
            set
            {
                _draggable = value;
                _dragOverlay.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public void ShowWaiting()
        {
            _image.Source = null;
            _bitmap = null;
            ContentAspect = 0;
            _waitingText.Visibility = Visibility.Visible;
        }

        /// <summary>Copies this window's sub-rect of the RTT texture into its bitmap. UI thread only.</summary>
        public void UpdateImage(RttFrame frame, RECT src)
        {
            if (src.IsEmpty)
            {
                ShowWaiting();
                return;
            }

            int w = src.Width, h = src.Height;
            ContentAspect = (double)w / h;
            if (_bitmap == null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
            {
                _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
                _image.Source = _bitmap;
            }
            _bitmap.WritePixels(new Int32Rect(src.Left, src.Top, w, h), frame.Pixels, frame.Stride, 0, 0);
            _waitingText.Visibility = Visibility.Collapsed;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;

            // No maximize box (prevents Aero-snap maximize while dragging); tool window (no Alt+Tab entry).
            int style = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_STYLE);
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_STYLE, style & ~(NativeMethods.WS_MAXIMIZEBOX | NativeMethods.WS_MINIMIZEBOX));
            int ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, (ex | NativeMethods.WS_EX_TOOLWINDOW) & ~NativeMethods.WS_EX_APPWINDOW);

            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

            NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, _rect.Left, _rect.Top, _rect.Width, _rect.Height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case NativeMethods.WM_NCCALCSIZE:
                    if (wParam != IntPtr.Zero)
                    {
                        // Client area == window area: no visible frame, even though WS_THICKFRAME is set.
                        handled = true;
                        return IntPtr.Zero;
                    }
                    break;

                case NativeMethods.WM_NCHITTEST:
                    handled = true;
                    return (IntPtr)HitTest(NativeMethods.LoWordSigned(lParam), NativeMethods.HiWordSigned(lParam));

                case NativeMethods.WM_NCLBUTTONDBLCLK:
                    handled = true; // don't maximize on caption double-click
                    return IntPtr.Zero;

                case NativeMethods.WM_NCRBUTTONDOWN:
                    handled = true;
                    return IntPtr.Zero;

                case NativeMethods.WM_NCRBUTTONUP:
                    handled = true; // our menu instead of the system menu
                    _controller.ShowContextMenu(this);
                    return IntPtr.Zero;

                case NativeMethods.WM_GETMINMAXINFO:
                {
                    var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                    mmi.ptMinTrackSize = new POINT(Snapper.MinSize, Snapper.MinSize);
                    Marshal.StructureToPtr(mmi, lParam, false);
                    handled = true;
                    return IntPtr.Zero;
                }

                case NativeMethods.WM_ENTERSIZEMOVE:
                    _inSizeMove = true;
                    break;

                case NativeMethods.WM_EXITSIZEMOVE:
                    _inSizeMove = false;
                    _controller.OnWindowStateChanged(this);
                    break;

                case NativeMethods.WM_MOVING:
                {
                    var r = Marshal.PtrToStructure<RECT>(lParam);
                    r = Snapper.SnapMove(r, _controller.OtherWindowRects(this), NativeMethods.GetMonitorRects());
                    Marshal.StructureToPtr(r, lParam, false);
                    handled = true;
                    return (IntPtr)1;
                }

                case NativeMethods.WM_SIZING:
                {
                    var r = Marshal.PtrToStructure<RECT>(lParam);
                    r = Snapper.SnapSize(r, wParam.ToInt32(), _controller.OtherWindowRects(this), NativeMethods.GetMonitorRects(), ContentAspect);
                    Marshal.StructureToPtr(r, lParam, false);
                    handled = true;
                    return (IntPtr)1;
                }

                case NativeMethods.WM_DPICHANGED:
                {
                    // Keep our pixel size when crossing to a monitor with a different DPI (WPF would otherwise
                    // rescale the window). While dragging keep the suggested position; otherwise stay put.
                    // WPF's own DPI handling still runs after this, using the adjusted rect.
                    var suggested = Marshal.PtrToStructure<RECT>(lParam);
                    if (NativeMethods.GetWindowRect(hwnd, out var cur))
                    {
                        var r = _inSizeMove ? RECT.FromXYWH(suggested.Left, suggested.Top, cur.Width, cur.Height) : cur;
                        Marshal.StructureToPtr(r, lParam, false);
                    }
                    break;
                }
            }
            return IntPtr.Zero;
        }

        private int HitTest(int x, int y)
        {
            if (!_draggable || !NativeMethods.GetWindowRect(_hwnd, out var r))
                return NativeMethods.HTCLIENT;

            int b = Math.Max(2, Math.Min(ResizeBorderPx, Math.Min(r.Width, r.Height) / 4));
            bool l = x < r.Left + b, rt = x >= r.Right - b, t = y < r.Top + b, bt = y >= r.Bottom - b;
            if (t && l) return NativeMethods.HTTOPLEFT;
            if (t && rt) return NativeMethods.HTTOPRIGHT;
            if (bt && l) return NativeMethods.HTBOTTOMLEFT;
            if (bt && rt) return NativeMethods.HTBOTTOMRIGHT;
            if (l) return NativeMethods.HTLEFT;
            if (rt) return NativeMethods.HTRIGHT;
            if (t) return NativeMethods.HTTOP;
            if (bt) return NativeMethods.HTBOTTOM;
            return NativeMethods.HTCAPTION;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // Swallow Alt+F4 style closes; exiting is done via the menu.
            if (e.Key == Key.System && e.SystemKey == Key.F4) e.Handled = true;
            base.OnKeyDown(e);
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_controller.IsExiting) e.Cancel = true;
            base.OnClosing(e);
        }
    }
}
