# RttClientDN

A small WPF (.NET Framework 4.8) viewer for Falcon BMS RTT instrument images
(HUD, PFL, DED, RWR, MFD_L, MFD_R, HMS). Each image gets its own topmost,
borderless window. See `CLAUDE.md` for the full spec.

## Build

```
dotnet build -c Release
```

Output: `bin/Release/net48/RttClientDN.exe` (AnyCPU, runs as a 64-bit process).
Visual Studio 2022 works too. The project needs no Windows-only SDK pieces, so
it also builds on Linux/macOS with the .NET SDK.

## Usage

* Right-click any window (or the tray icon) for the menu: toggle the draggable
  border, fill the current monitor, hide/show windows, exit.
* In draggable mode, drag anywhere inside a window to move it, or drag its edges
  to resize. Windows snap to each other's edges and centers, to display edges,
  and to the image's aspect ratio (10px threshold).
* Window positions/sizes/visibility are saved in
  `HKCU\SOFTWARE\Benchmark Sims\RttClientDN`, in virtual-desktop pixels.

## Layout

| File | Purpose |
|------|---------|
| `src/Program.cs` | Entry point, single-instance guard |
| `src/AppController.cs` | Windows, menus, default layout, frame dispatch |
| `src/InstrumentWindow.cs` | Borderless window, Win32 hit-testing, move/size hooks |
| `src/Snapper.cs` | Edge/center/display/aspect snapping |
| `src/RttReader.cs` | Event wait loop, shared-memory + DDS reading |
| `src/Settings.cs` | Registry persistence |
| `src/TrayIcon.cs` | Notification-area icon and menu |
