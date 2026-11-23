using System;
using System.Globalization;
using Microsoft.Win32;

namespace RttClientDN
{
    internal struct WindowState
    {
        public RECT Rect;      // virtual-desktop pixels
        public bool Visible;
    }

    /// <summary>
    /// Persists each window's rect and visibility under HKCU\SOFTWARE\Benchmark Sims\RttClientDN,
    /// one REG_SZ value per window: "left,top,width,height,visible".
    /// </summary>
    internal static class Settings
    {
        private const string KeyPath = @"SOFTWARE\Benchmark Sims\RttClientDN";

        public static WindowState? Load(string name)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (!(key?.GetValue(name) is string s)) return null;
                    var p = s.Split(',');
                    if (p.Length != 5) return null;
                    var n = new int[5];
                    for (int i = 0; i < 5; i++)
                        if (!int.TryParse(p[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n[i])) return null;
                    if (n[2] < Snapper.MinSize || n[3] < Snapper.MinSize) return null;
                    return new WindowState { Rect = RECT.FromXYWH(n[0], n[1], n[2], n[3]), Visible = n[4] != 0 };
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static void Save(string name, WindowState state)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    var r = state.Rect;
                    key?.SetValue(name, string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4}",
                        r.Left, r.Top, r.Width, r.Height, state.Visible ? 1 : 0));
                }
            }
            catch (Exception)
            {
                // Settings are best-effort; never crash the overlay over them.
            }
        }
    }
}
