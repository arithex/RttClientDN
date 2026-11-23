
Let's build a simple WPF app to display extracted instrument-images from a flight simulator.  The images are written to a named shared-mem section, and a win32 event is signaled when they are updated.  Each image should be presented in its own top-level, topmost borderless window.

* Let's name the app RttClientDN.exe

* Use .NET Framework 4.8, compiled to AnyCPU but prefer 64-bit (x64 and arm64).

* Set dpi-awareness, so that coordinates saved will always be in the "virtual desktop" coordinate space.

The vision is for a clean, minimalist UX -- one topmost, borderless window for each of the 7 instrument images.  Ideally this app should live in the taskbar "tray" area (aka shell notify-icon area) -- if it's possible to do that in a portable way (this app will run in Linux/Wine environment, in addition to Windows).

The 7 instrument windows should be borderless, initially/by-default.  Right-clicking any of the windows, should offer a popup menu --
- Toggle draggable border
- Fill current monitor
- Hide or Show windows >>
	- HUD
	- PFL
	- DED
	- RWR
	- MFD_L
	- MFD_R
	- HMS
- Exit RttClientDN.exe

### Dragging and Resizing

When in draggable-mode, the individual windows can be repositioned or resized.  When resizing, the image content should be stretched, preserving aspect ratio -- with black background above/below or left/right.  If there is image content, and the resize rect is close to the aspect ration of the image (say, +/- 10px) it should snap to match the aspect ratio of the image content.

When positioning or sizing, if the center is near (say, within 10px) the center of another window, it should snap to align to that other window.  Same, if the top/bottom/left/right edges are close to aligning with another window -- snap to alignment.

The windows can be dragged to other monitors.  The "Fill current monitor" menu command should expand the window to fill the display which currently contains the majority of it (its centerpoint).

If dragging or resizing, if an edge is close (10px) to the edge of a display, it should snap flush to that edge.

### Auto-saving

Any adjustments to the 7 window states (positions, sizes, and visibility) should be saved in a registry key: 
- `HKCU\SOFTWARE\Benchmark Sims\RttClientDN`

### Startup

This app will typically be started before the flightsim -- if the sharedmem section doesn't exist yet, wait for it -- polling should not be necessary, if we have a thread waiting on the win32 event signal -- we can wait to open the sharedmem once we receive the first signal.  Render empty (black background) placeholder windows with orange "Waiting..." text.

### Idle detection

If the win32 event isn't signaled for 3 seconds, go ahead and close the sharedmem handles -- revert to the startup "Waiting..." state.  (Note: it's possible, when the sharedmem section is re-created by the flightsim software, it may be at a larger or smaller size.)

### Spin prevention

The sender uses PulseEvent() it's possible for the reader to loop twice (or more) in response to a single notification -- we should prevent that with a cap.. and not enter WaitForSingleObject more than once per 5ms.

### Shutdown detection

To cleanly shutdown the thread waiting for event signals, we can simply set a global flag -- the 3 sec idle-timeout should be sufficient to see that, and exit the wait-loop.

### The Binary Interface Contract

The name of the memory-mapped section containing the images is `FalconTexturesSharedMemoryArea`, and the name of the win32 event is `BMS_RTTExport_Done`.  (The event is a manual-reset event, but signaled via PulseEvent so we shouldn't need to call ResetEvent.)

This is not 100% threadsafe, but it's good enough for our purposes.

The binary content of FalconTexturesSharedMemoryArea is just a single DDS texture (basically a DDS file in memory) containing the 7 logical images -- ie. an "atlas" image.  We call this the "RTT texture" -- it contains 7 "RTT images".

First 4 bytes: DDS_MAGIC (0x20534444)
Next 124 bytes: DDS_HEADER (which provides width, height, row-pitch and pixel format)

The DDS texture is in DDS_RGB pixel format, so it should be straightforward to convert to a set of Bitmap objects (if WPF can't natively use a DDS texture as a ImageSource?  I'm not sure).

The sizes and offsets of the images within the atlas, must be obtained from a different sharedmem section: `FalconSharedMemoryArea2`

The overall size of the RTT texture is at byte-offset 1040 (width, ushort) and 1042 (height, ushort).  Followed at offset 1044 by an array of {left,top,right,bottom} rectangle structs (each number is a 2-byte short).  These numbers will be zero, if the FalconTexturesSharedMemoryArea hasn't been created and populated yet.
