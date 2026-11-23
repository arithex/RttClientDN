using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace RttClientDN
{
    /// <summary>
    /// The most recent RTT texture, converted to 32bpp BGRX, plus the 7 image rects within it.
    /// Written by the reader thread and read by the UI thread, both under <see cref="Lock"/>.
    /// </summary>
    internal sealed class RttFrame
    {
        public readonly object Lock = new object();
        public byte[] Pixels = new byte[0];
        public int Width, Height, Stride;
        public readonly RECT[] Rects = new RECT[RttReader.ImageCount];
    }

    /// <summary>
    /// Background thread that waits on the BMS_RTTExport_Done event and copies the RTT texture out of
    /// FalconTexturesSharedMemoryArea, using the image rects from FalconSharedMemoryArea2.
    /// </summary>
    internal sealed class RttReader
    {
        public const int ImageCount = 7;

        private const string TexturesSectionName = "FalconTexturesSharedMemoryArea";
        private const string FlightData2SectionName = "FalconSharedMemoryArea2";
        private const string EventName = "BMS_RTTExport_Done";

        private const int IdleTimeoutMs = 3000;
        private const int MinWaitIntervalMs = 5;

        // FalconSharedMemoryArea2 layout.
        private const int Fd2RttWidthOffset = 1040;   // ushort
        private const int Fd2RttHeightOffset = 1042;  // ushort
        private const int Fd2RttAreasOffset = 1044;   // ImageCount x {short left, top, right, bottom}
        private const int Fd2MinSize = Fd2RttAreasOffset + ImageCount * 8;

        // DDS layout (offsets from start of file, i.e. including the 4-byte magic).
        private const uint DdsMagic = 0x20534444; // "DDS "
        private const int DdsHeaderEnd = 4 + 124;
        private const int DdsFlagsOffset = 8;
        private const int DdsHeightOffset = 12;
        private const int DdsWidthOffset = 16;
        private const int DdsPitchOffset = 20;
        private const int DdsPfFlagsOffset = 80;
        private const int DdsPfFourCCOffset = 84;
        private const int DdsPfBitCountOffset = 88;
        private const int DdsPfRMaskOffset = 92;
        private const int DdsPfGMaskOffset = 96;
        private const int DdsPfBMaskOffset = 100;
        private const uint DDSD_PITCH = 0x8;
        private const uint DDPF_FOURCC = 0x4;
        private const uint DDPF_RGB = 0x40;
        private const uint D3DFMT_A8R8G8B8 = 21;
        private const uint D3DFMT_X8R8G8B8 = 22;

        private readonly RttFrame _frame;
        private readonly Action _onFrame;
        private readonly Action _onWaiting;
        private volatile bool _shutdown;
        private Thread _thread;

        private MemoryMappedFile _texFile, _fd2File;
        private MemoryMappedViewAccessor _texView, _fd2View;

        /// <param name="onFrame">Called on the reader thread after <paramref name="frame"/> was updated.</param>
        /// <param name="onWaiting">Called on the reader thread after the sim went idle and the sections were closed.</param>
        public RttReader(RttFrame frame, Action onFrame, Action onWaiting)
        {
            _frame = frame;
            _onFrame = onFrame;
            _onWaiting = onWaiting;
        }

        public void Start()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "RTT reader" };
            _thread.Start();
        }

        /// <summary>Asks the thread to exit; it notices within the idle timeout (or on the next signal).</summary>
        public void RequestShutdown() => _shutdown = true;

        private void Run()
        {
            EventWaitHandle evt;
            try
            {
                // Creating (rather than opening) the event means we don't have to poll for the sim to start:
                // when the sim later calls CreateEvent with the same name, it gets this same event object.
                evt = new EventWaitHandle(false, EventResetMode.ManualReset, EventName);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("RttClientDN: cannot create/open event " + EventName + ": " + ex.Message);
                return;
            }

            var clock = Stopwatch.StartNew();
            long lastWaitMs = -MinWaitIntervalMs;
            bool connected = false;

            using (evt)
            {
                while (!_shutdown)
                {
                    // Spin prevention: the sender uses PulseEvent, so never wait more than once per 5ms.
                    long sinceLast = clock.ElapsedMilliseconds - lastWaitMs;
                    if (sinceLast < MinWaitIntervalMs)
                        Thread.Sleep((int)(MinWaitIntervalMs - sinceLast));
                    lastWaitMs = clock.ElapsedMilliseconds;

                    bool signaled = evt.WaitOne(IdleTimeoutMs);
                    if (_shutdown) break;

                    if (!signaled)
                    {
                        if (connected)
                        {
                            CloseSections();
                            connected = false;
                            _onWaiting();
                        }
                        continue;
                    }

                    if (!connected)
                        connected = TryOpenSections();
                    if (connected)
                    {
                        try
                        {
                            if (ReadFrame())
                                _onFrame();
                        }
                        catch (Exception ex)
                        {
                            Trace.WriteLine("RttClientDN: read failed: " + ex.Message);
                            CloseSections();
                            connected = false;
                        }
                    }
                }
            }
            CloseSections();
        }

        private bool TryOpenSections()
        {
            try
            {
                _texFile = MemoryMappedFile.OpenExisting(TexturesSectionName, MemoryMappedFileRights.Read);
                _texView = _texFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                _fd2File = MemoryMappedFile.OpenExisting(FlightData2SectionName, MemoryMappedFileRights.Read);
                _fd2View = _fd2File.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                return true;
            }
            catch (Exception)
            {
                CloseSections();
                return false;
            }
        }

        private void CloseSections()
        {
            _texView?.Dispose(); _texView = null;
            _texFile?.Dispose(); _texFile = null;
            _fd2View?.Dispose(); _fd2View = null;
            _fd2File?.Dispose(); _fd2File = null;
        }

        private unsafe bool ReadFrame()
        {
            byte* tex = null, fd2 = null;
            var texHandle = _texView.SafeMemoryMappedViewHandle;
            var fd2Handle = _fd2View.SafeMemoryMappedViewHandle;
            try
            {
                texHandle.AcquirePointer(ref tex);
                fd2Handle.AcquirePointer(ref fd2);
                tex += _texView.PointerOffset;
                fd2 += _fd2View.PointerOffset;
                return CopyFrame(tex, _texView.Capacity, fd2, _fd2View.Capacity);
            }
            finally
            {
                if (tex != null) texHandle.ReleasePointer();
                if (fd2 != null) fd2Handle.ReleasePointer();
            }
        }

        private unsafe bool CopyFrame(byte* tex, long texSize, byte* fd2, long fd2Size)
        {
            if (texSize < DdsHeaderEnd || fd2Size < Fd2MinSize) return false;
            if (*(uint*)tex != DdsMagic) return false;

            int height = *(int*)(tex + DdsHeightOffset);
            int width = *(int*)(tex + DdsWidthOffset);
            uint flags = *(uint*)(tex + DdsFlagsOffset);
            uint pfFlags = *(uint*)(tex + DdsPfFlagsOffset);
            uint fourCC = *(uint*)(tex + DdsPfFourCCOffset);
            int bpp = *(int*)(tex + DdsPfBitCountOffset);
            uint rMask = *(uint*)(tex + DdsPfRMaskOffset);
            uint gMask = *(uint*)(tex + DdsPfGMaskOffset);
            uint bMask = *(uint*)(tex + DdsPfBMaskOffset);

            if ((pfFlags & DDPF_RGB) == 0)
            {
                // Tolerate legacy D3DFMT codes stored as a FourCC.
                if ((pfFlags & DDPF_FOURCC) != 0 && (fourCC == D3DFMT_A8R8G8B8 || fourCC == D3DFMT_X8R8G8B8))
                {
                    bpp = 32; rMask = 0x00FF0000; gMask = 0x0000FF00; bMask = 0x000000FF;
                }
                else return false;
            }
            if (width <= 0 || height <= 0 || width > 16384 || height > 16384) return false;
            if (bpp != 16 && bpp != 24 && bpp != 32) return false;

            int srcPitch = *(int*)(tex + DdsPitchOffset);
            int minPitch = (width * bpp + 7) / 8;
            if ((flags & DDSD_PITCH) == 0 || srcPitch < minPitch) srcPitch = minPitch;
            if (DdsHeaderEnd + (long)srcPitch * height > texSize) return false; // section smaller than header claims

            int dstStride = width * 4;
            int bytesPerPixel = bpp / 8;
            bool isBgrx = bpp == 32 && rMask == 0x00FF0000 && gMask == 0x0000FF00 && bMask == 0x000000FF;

            byte* src = tex + DdsHeaderEnd;
            lock (_frame.Lock)
            {
                if (_frame.Pixels.Length != dstStride * height)
                    _frame.Pixels = new byte[dstStride * height];
                _frame.Width = width;
                _frame.Height = height;
                _frame.Stride = dstStride;

                fixed (byte* dst0 = _frame.Pixels)
                {
                    if (isBgrx)
                    {
                        for (int y = 0; y < height; y++)
                            Buffer.MemoryCopy(src + (long)y * srcPitch, dst0 + (long)y * dstStride, dstStride, dstStride);
                    }
                    else
                    {
                        var r = new Channel(rMask); var g = new Channel(gMask); var b = new Channel(bMask);
                        for (int y = 0; y < height; y++)
                        {
                            byte* s = src + (long)y * srcPitch;
                            byte* d = dst0 + (long)y * dstStride;
                            for (int x = 0; x < width; x++, s += bytesPerPixel, d += 4)
                            {
                                uint v = bytesPerPixel == 4 ? *(uint*)s
                                       : bytesPerPixel == 3 ? (uint)(s[0] | s[1] << 8 | s[2] << 16)
                                       : *(ushort*)s;
                                d[0] = b.Extract(v); d[1] = g.Extract(v); d[2] = r.Extract(v); d[3] = 255;
                            }
                        }
                    }
                }

                // Image rects, clamped to the texture actually present in shared memory.
                short* areas = (short*)(fd2 + Fd2RttAreasOffset);
                for (int i = 0; i < ImageCount; i++)
                {
                    int l = Clamp(areas[i * 4 + 0], 0, width);
                    int t = Clamp(areas[i * 4 + 1], 0, height);
                    int rr = Clamp(areas[i * 4 + 2], l, width);
                    int bb = Clamp(areas[i * 4 + 3], t, height);
                    _frame.Rects[i] = new RECT(l, t, rr, bb);
                }
            }
            return true;
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

        /// <summary>Extracts an 8-bit channel value from a packed pixel using a DDS bit mask.</summary>
        private readonly struct Channel
        {
            private readonly uint _mask;
            private readonly int _shift;
            private readonly uint _max;

            public Channel(uint mask)
            {
                _mask = mask;
                _shift = 0;
                if (mask != 0) while (((mask >> _shift) & 1) == 0) _shift++;
                _max = mask >> _shift;
            }

            public byte Extract(uint v)
            {
                if (_max == 0) return 0;
                uint c = (v & _mask) >> _shift;
                return _max == 255 ? (byte)c : (byte)(c * 255 / _max);
            }
        }
    }
}
