using System;
using System.Diagnostics;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace ReyDesk.Windows.Media;

/// <summary>
/// Captures the primary display via Windows.Graphics.Capture and raises
/// <see cref="FrameCaptured"/> for each frame as a tightly-packed NV12 buffer (the
/// input format ReyDesk.Windows.MediaFoundation.H264Encoder expects). Multi-monitor
/// selection is out of scope for this milestone - see
/// Direct3D11Interop.GetPrimaryMonitorHandle.
///
/// NOT hardware-validated (no GPU/display available in the environment this was
/// written in).
/// </summary>
internal sealed class ScreenCapture : IDisposable
{
    // Preserve native1080p text while keeping4K work bounded. Frame pacing drops
    // obsolete input rather than queueing it if CPU conversion/encoding runs late.

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly GraphicsCaptureItem _item;
    private Direct3D11CaptureFramePool _framePool = null!;
    private GraphicsCaptureSession _session = null!;
    private ID3D11Texture2D? _stagingTexture;
    private SizeInt32 _size;
    private SizeInt32 _poolSize;
    private byte[]? _nv12Buffer;
    private long _frameCount;
    private long _droppedFrames;
    private long _busyFrames, _poolDiscardedFrames, _noSubscriberFrames;
    private long _futureFrames, _staleFrames, _cadenceFrames;
    private double _lastDropLogSeconds = -5;
    private double _minimumSourceAgeMs = double.MaxValue, _maximumSourceAgeMs = double.MinValue;
    private readonly object _frameLock = new();
    private readonly VideoFrameTiming _timing;
    private readonly int _maxHeight;
    private bool _disposed;
    private double _lastLogSeconds = -5;
    private readonly System.Diagnostics.Stopwatch _captureClock = System.Diagnostics.Stopwatch.StartNew();

    public event Action<byte[], int, int, long>? FrameCaptured;

    /// <summary>Native captured resolution - NOT what's actually encoded/sent, see
    /// <see cref="EncodeWidth"/>/<see cref="EncodeHeight"/>.</summary>
    public int Width => _size.Width;
    public int Height => _size.Height;

    public int EncodeWidth => ComputeEncodeSize().Width;
    public int EncodeHeight => ComputeEncodeSize().Height;

    private (int Width, int Height) ComputeEncodeSize()
    {
        return VideoCaptureQuality.GetEncodeSize(_size.Width, _size.Height, _maxHeight);
    }

    private ScreenCapture(ID3D11Device device, ID3D11DeviceContext context, GraphicsCaptureItem item, int framesPerSecond, int maxHeight)
    {
        _device = device;
        _context = context;
        _item = item;
        _timing = new VideoFrameTiming(framesPerSecond);
        _maxHeight = maxHeight;
    }

    public static ScreenCapture StartForPrimaryMonitor(int framesPerSecond = 15, int maxHeight = 1080)
    {
        var featureLevels = new[]
        {
            FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0,
        };
        // BgraSupport is required for Windows.Graphics.Capture's D3D device bridge.
        D3D11.D3D11CreateDevice(
            null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, featureLevels,
            out var device, out _, out var context);

        var hmonitor = Direct3D11Interop.GetPrimaryMonitorHandle();
        var item = Direct3D11Interop.CreateItemForMonitor(hmonitor);

        var capture = new ScreenCapture(device!, context!, item, framesPerSecond, maxHeight);
        capture.Start();
        return capture;
    }

    private void Start()
    {
        _size = _item.Size;
        _poolSize = _size;
        var winrtDevice = Direct3D11Interop.CreateDirect3DDevice(_device);
        // CreateFreeThreaded, not Create: this whole call chain runs on a background
        // ThreadPool thread (pairing -> SessionCoordinator -> WebRtcSession.StartAsync,
        // none of it on the WinUI dispatcher thread), and plain Create() delivers
        // FrameArrived via the calling thread's DispatcherQueue - which doesn't exist
        // here, so the event silently never fires at all (confirmed on real hardware:
        // capture "started" with no error, but zero frames ever arrived). Free-threaded
        // delivers the event on an arbitrary MTA thread instead, no dispatcher needed.
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, numberOfBuffers: 2, size: _size);
        _framePool.FrameArrived += OnFrameArrived;
        _session = _framePool.CreateCaptureSession(_item);
        try
        {
            // Real hardware test produced near-empty (~100-300 byte) P-frames for
            // almost the entire session - consistent with the cursor genuinely not
            // being part of the captured content at all (a real, if surprising,
            // possibility - IsCursorCaptureEnabled exists specifically to control
            // this and isn't guaranteed on by default on every OS build). Guarded
            // because the property itself was only added in a later Windows 10
            // feature update than this app's TargetPlatformMinVersion (10.0.17763.0).
            _session.IsCursorCaptureEnabled = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ScreenCapture] IsCursorCaptureEnabled not supported on this OS build, cursor will not appear in the stream: {ex.Message}");
        }
        _session.StartCapture();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        // Never queue callbacks behind an encode/send stall or share the immediate
        // D3D context / reusable NV12 buffer between concurrent callback threads.
        if (!Monitor.TryEnter(_frameLock))
        {
            Interlocked.Increment(ref _droppedFrames);
            Interlocked.Increment(ref _busyFrames);
            return;
        }
        // This whole body runs on a WinRT-invoked callback thread with no managed
        // exception handler above it - an uncaught exception here doesn't just fail
        // this frame, it takes down the entire process. Originally only the
        // conversion step below had a try/catch; that was a real gap, not a
        // theoretical one.
        try
        {
            if (!_disposed) OnFrameArrivedCore(sender);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ScreenCapture] OnFrameArrived failed, dropping this frame: {ex}");
        }
        finally
        {
            Monitor.Exit(_frameLock);
        }
    }

    private void OnFrameArrivedCore(Direct3D11CaptureFramePool sender)
    {
        var newest = sender.TryGetNextFrame();
        // The frame pool has two buffers. Prefer the newest pending frame rather
        // than encoding an obsolete desktop after the previous frame was slow.
        var pending = sender.TryGetNextFrame();
        if (pending is not null)
        {
            newest?.Dispose();
            newest = pending;
            Interlocked.Increment(ref _droppedFrames);
            _poolDiscardedFrames++;
        }
        using var frame = newest;
        if (frame is null)
        {
            return;
        }

        var sourceTimeHns = frame.SystemRelativeTime.Ticks;
        var nowHns = (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));
        var signedAgeMs = (nowHns - sourceTimeHns) / 10_000.0;
        _minimumSourceAgeMs = Math.Min(_minimumSourceAgeMs, signedAgeMs);
        _maximumSourceAgeMs = Math.Max(_maximumSourceAgeMs, signedAgeMs);
        long sampleTimeHns = 0;
        var hasSubscriber = FrameCaptured is not null;
        var accepted = hasSubscriber && _timing.TryAccept(sourceTimeHns, nowHns, out sampleTimeHns);
        if (!accepted)
        {
            Interlocked.Increment(ref _droppedFrames);
            if (!hasSubscriber) _noSubscriberFrames++;
            else switch (_timing.LastDropReason)
            {
                case VideoFrameDropReason.FutureTimestamp: _futureFrames++; break;
                case VideoFrameDropReason.Stale: _staleFrames++; break;
                case VideoFrameDropReason.Cadence: _cadenceFrames++; break;
            }
        }
        if (_captureClock.Elapsed.TotalSeconds - _lastDropLogSeconds >= 5)
        {
            _lastDropLogSeconds = _captureClock.Elapsed.TotalSeconds;
            Console.WriteLine($"[ScreenCapture] drops: future={_futureFrames} stale={_staleFrames} cadence={_cadenceFrames} noSubscriber={_noSubscriberFrames} busy={Interlocked.Read(ref _busyFrames)} poolDiscard={_poolDiscardedFrames}; sourceAgeSigned={signedAgeMs:F3}ms min={_minimumSourceAgeMs:F3}ms max={_maximumSourceAgeMs:F3}ms accepted={_frameCount}");
        }
        if (!accepted)
        {
            return;
        }
        var frameAgeMs = Math.Max(0, nowHns - sourceTimeHns) / 10_000.0;

        if (frame.ContentSize.Width != _poolSize.Width || frame.ContentSize.Height != _poolSize.Height)
        {
            // Deliberately does NOT skip/return here - this frame is still
            // processable immediately (see below for why _size itself is no longer
            // trusted for anything but deciding whether to Recreate the pool).
            // Returning unconditionally here was a real bug found on the first
            // hardware test: WGC's very first delivered frame(s) can report a
            // different ContentSize than GraphicsCaptureItem.Size did back in
            // Start(), and it kept mismatching on every single frame - which meant
            // every frame was silently dropped forever (no video, no error, nothing
            // to log) rather than just the first one or two.
            Console.WriteLine($"[ScreenCapture] size changed {_poolSize.Width}x{_poolSize.Height} -> {frame.ContentSize.Width}x{frame.ContentSize.Height}");
            _poolSize = frame.ContentSize;
            _size = frame.ContentSize;
            _stagingTexture?.Dispose();
            _stagingTexture = null;
            _nv12Buffer = null;
            sender.Recreate(Direct3D11Interop.CreateDirect3DDevice(_device), DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        }

        using var texture = Direct3D11Interop.GetTexture(frame.Surface);
        EnsureStagingTexture(texture);

        // Authoritative size comes from the real D3D11 texture, not from _size
        // (GraphicsCaptureItem.Size / frame.ContentSize) - a real bug found on
        // hardware: these two disagreed (by roughly a factor matching the reported
        // "only ~1/3 of the screen, and it looks like a tall narrow strip" symptom),
        // and Width/Height (read by both ComputeEncodeSize and the NV12 conversion's
        // *source* sampling below) was using the wrong, smaller one - so the
        // conversion only ever sampled a portion of each row of otherwise perfectly
        // valid captured pixel data, silently cropping every frame to that same
        // portion instead of touching invalid memory or throwing. EnsureStagingTexture
        // already independently got this right by reading straight off the texture.
        var textureDescription = texture.Description;
        if (textureDescription.Width != _size.Width || textureDescription.Height != _size.Height)
        {
            Console.WriteLine($"[ScreenCapture] real texture size {textureDescription.Width}x{textureDescription.Height} != reported {_size.Width}x{_size.Height} - trusting the texture");
            _size = new SizeInt32((int)textureDescription.Width, (int)textureDescription.Height);
        }

        var (encodeWidth, encodeHeight) = ComputeEncodeSize();
        var requiredLength = encodeWidth * encodeHeight * 3 / 2;
        if (_nv12Buffer is null || _nv12Buffer.Length != requiredLength)
        {
            _nv12Buffer = new byte[requiredLength];
        }

        var mapStart = Stopwatch.GetTimestamp();
        _context.CopyResource(_stagingTexture!, texture);
        var mapped = _context.Map(_stagingTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        var conversionStart = Stopwatch.GetTimestamp();
        try
        {
            unsafe
            {
                Nv12Conversion.ConvertBgraToNv12(
                    (byte*)mapped.DataPointer, (int)mapped.RowPitch, Width, Height, encodeWidth, encodeHeight, _nv12Buffer);
            }
        }
        finally
        {
            _context.Unmap(_stagingTexture!, 0);
        }

        _frameCount++;
        var conversionEnd = Stopwatch.GetTimestamp();
        if (_captureClock.Elapsed.TotalSeconds - _lastLogSeconds >= 5)
        {
            // The frame-index-based logging this replaced couldn't distinguish
            // "genuinely running at ~15fps with a growing backlog" from "not
            // actually achieving anywhere near 15fps at all" - two very different
            // bugs with the same user-visible symptom (laggy/slow-motion video).
            // Real elapsed time settles that question directly.
            var elapsed = _captureClock.Elapsed.TotalSeconds;
            var avgFps = _frameCount / elapsed;
            _lastLogSeconds = elapsed;
            Console.WriteLine($"[ScreenCapture] frame #{_frameCount} {Width}x{Height}->{encodeWidth}x{encodeHeight}; sourceAge={frameAgeMs:F1}ms copyMap={Stopwatch.GetElapsedTime(mapStart, conversionStart).TotalMilliseconds:F1}ms convert={Stopwatch.GetElapsedTime(conversionStart, conversionEnd).TotalMilliseconds:F1}ms accepted={avgFps:F1}fps dropped={Interlocked.Read(ref _droppedFrames)}");
        }
        FrameCaptured?.Invoke(_nv12Buffer, encodeWidth, encodeHeight, sampleTimeHns);
    }

    private void EnsureStagingTexture(ID3D11Texture2D sourceTexture)
    {
        var desc = sourceTexture.Description;
        if (_stagingTexture is not null)
        {
            var staging = _stagingTexture.Description;
            if (staging.Width == desc.Width && staging.Height == desc.Height && staging.Format == desc.Format) return;
            _stagingTexture.Dispose();
        }
        _stagingTexture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = desc.Width,
            Height = desc.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = desc.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });
    }

    public void Dispose()
    {
        lock (_frameLock)
        {
            if (_disposed) return;
            _disposed = true;
            _session?.Dispose();
            if (_framePool is not null)
            {
                _framePool.FrameArrived -= OnFrameArrived;
                _framePool.Dispose();
            }
            _stagingTexture?.Dispose();
            _context.Dispose();
            _device.Dispose();
            Console.WriteLine("[ScreenCapture] capture disposed");
        }
    }
}
