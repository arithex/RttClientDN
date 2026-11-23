using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RttClientDN
{
    /// <summary>
    /// Shell notification-area icon (WinForms NotifyIcon, which Wine also supports).
    /// If no tray is available the icon simply doesn't appear; the per-window menus still work.
    /// </summary>
    internal sealed class TrayIcon : IDisposable
    {
        private readonly AppController _controller;
        private readonly NotifyIcon _icon;
        private readonly ContextMenuStrip _menu;

        public TrayIcon(AppController controller)
        {
            _controller = controller;
            _menu = new ContextMenuStrip();
            _menu.Opening += (s, e) => { BuildMenu(); e.Cancel = false; };
            BuildMenu();

            _icon = new NotifyIcon
            {
                Text = "RttClientDN",
                Icon = CreateIcon(),
                ContextMenuStrip = _menu,
                Visible = true,
            };
            _icon.DoubleClick += (s, e) => _controller.ShowAll();
        }

        private void BuildMenu()
        {
            _menu.Items.Clear();

            var drag = new ToolStripMenuItem("Toggle draggable border") { Checked = _controller.Draggable };
            drag.Click += (s, e) => _controller.ToggleDraggable();
            _menu.Items.Add(drag);

            var windows = new ToolStripMenuItem("Hide or Show windows");
            for (int i = 0; i < AppController.InstrumentNames.Length; i++)
            {
                int idx = i;
                var item = new ToolStripMenuItem(AppController.InstrumentNames[i]) { Checked = _controller.IsVisible(i) };
                item.Click += (s, e) => _controller.SetVisible(idx, !_controller.IsVisible(idx));
                windows.DropDownItems.Add(item);
            }
            windows.DropDownItems.Add(new ToolStripSeparator());
            var all = new ToolStripMenuItem("Show all");
            all.Click += (s, e) => _controller.ShowAll();
            windows.DropDownItems.Add(all);
            _menu.Items.Add(windows);

            _menu.Items.Add(new ToolStripSeparator());
            var exit = new ToolStripMenuItem("Exit RttClientDN.exe");
            exit.Click += (s, e) => _controller.Exit();
            _menu.Items.Add(exit);
        }

        /// <summary>Draws a simple black-with-orange-frame icon so no .ico resource is needed.</summary>
        private static Icon CreateIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                using (var pen = new Pen(Color.FromArgb(255, 140, 0), 3))
                using (var brush = new SolidBrush(Color.FromArgb(255, 140, 0)))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    g.FillRectangle(Brushes.Black, 2, 2, 28, 28);
                    g.DrawRectangle(pen, 3, 3, 26, 26);
                    g.FillRectangle(brush, 9, 9, 6, 6);
                    g.FillRectangle(brush, 17, 9, 6, 6);
                    g.FillRectangle(brush, 9, 17, 14, 6);
                }
                IntPtr h = bmp.GetHicon();
                // Clone so the Icon owns its own handle data (the HICON from GetHicon is never freed otherwise; fine for one icon).
                return (Icon)Icon.FromHandle(h).Clone();
            }
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
            _menu.Dispose();
        }
    }
}
