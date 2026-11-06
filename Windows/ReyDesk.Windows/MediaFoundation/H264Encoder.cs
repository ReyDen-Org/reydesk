using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ReyDesk.Windows.MediaFoundation;

/// <summary>
/// Drives a single Media Foundation H.264 encoder MFT: NV12 frames in, Annex-B access
/// units out. Prefers a hardware encoder MFT (GPU-accelerated) and falls back to
/// Windows' built-in software H.264 encoder MFT (always present) if none is found or
/// setup fails - see docs/architecture.md for why this exists
/// instead of the originally-planned Vortice.MediaFoundation path (that package
/// doesn't actually expose IMFTransform/IMFSample at all as of the pinned version).
///
/// Deliberately drives the MFT with plain CPU-memory samples (MFCreateMemoryBuffer),
/// not GPU-resident DXGI surface buffers: the zero-copy GPU path needs
/// MF_SA_D3D11_AWARE/an IMFDXGIDeviceManager binding, which is a real extra layer of
/// COM plumbing for a *performance* optimization, not a correctness requirement - the
/// encode itself still happens on the hardware MFT either way. Revisit only if CPU
/// readback (ScreenCapture's staging-texture copy) proves to be the actual bottleneck.
///
/// </summary>
internal sealed class H264Encoder : IDisposable
{
    private readonly IMFActivate _activate;
    private readonly IMFTransform _transform;
    private readonly int _outputBufferSize;
    private readonly bool _outputProvidesSamples;
    internal sealed record EncodedFrame(byte[] Data, long SampleTimeHns);

    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }

    private H264Encoder(IMFActivate activate, IMFTransform transform, int width, int height, int fps, int outputBufferSize, bool outputProvidesSamples)
    {
        _activate = activate;
        _transform = transform;
        Width = width;
        Height = height;
        Fps = fps;
        _outputBufferSize = outputBufferSize;
        _outputProvidesSamples = outputProvidesSamples;
    }

    public static H264Encoder Create(int width, int height, int fps, int targetBitrateKbps)
    {
        // Software MS H.264 Encoder MFT (synchronous, always present on Windows
        // 10/11) first, hardware encoders as a fallback - confirmed on real hardware
        // that this needs to be this way around, not just a theoretical concern: the
        // hardware (GPU) encoder MFT this enumerated on a real test machine formally
        // accepted the plain-memory-buffer NV12 input type at configure time, then
        // rejected every actual frame at ProcessInput with MF_E_UNSUPPORTED_D3D_TYPE
        // (0xC00D6D76) - it requires D3D11-aware/GPU-resident samples, which this
        // class deliberately doesn't produce (see the class doc comment on why). The
        // built-in software encoder has no such requirement. If a hardware MFT ever
        // needs to be preferred again, it needs the D3D11-aware sample path built
        // first, not just a reordering back.
        var candidates = new List<IMFActivate>();
        candidates.AddRange(EnumerateEncoders(MfConstants.MFT_ENUM_FLAG_SYNCMFT | MfConstants.MFT_ENUM_FLAG_SORTANDFILTER));
        candidates.AddRange(EnumerateEncoders(MfConstants.MFT_ENUM_FLAG_HARDWARE | MfConstants.MFT_ENUM_FLAG_SORTANDFILTER));

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException("No H.264 encoder MFT is registered on this system.");
        }

        Exception? lastFailure = null;
        for (var i = 0; i < candidates.Count; i++)
        {
            var activate = candidates[i];
            try
            {
                var encoder = TryActivate(activate, width, height, fps, targetBitrateKbps);
                // Unused candidates are never activated, so they only ever hold their
                // enumeration reference - release those now that one has won.
                for (var j = i + 1; j < candidates.Count; j++)
                {
                    candidates[j].ShutdownObject();
                    Marshal.ReleaseComObject(candidates[j]);
                }
                return encoder;
            }
            catch (Exception ex)
            {
                lastFailure = ex;
                activate.ShutdownObject();
                Marshal.ReleaseComObject(activate);
            }
        }

        throw new InvalidOperationException("Every enumerated H.264 encoder MFT failed to configure.", lastFailure);
    }

    private static H264Encoder TryActivate(IMFActivate activate, int width, int height, int fps, int targetBitrateKbps)
    {
        MfConstants.ThrowIfFailed(activate.ActivateObject(typeof(IMFTransform).GUID, out var transformPtr));
        var transform = ComInterop.WrapAndRelease<IMFTransform>(transformPtr);
        try
        {
            ConfigureAsyncUnlockIfNeeded(transform);
            RequestLowLatency(transform);
            ConfigureTypes(transform, width, height, fps, targetBitrateKbps);

            transform.GetOutputStreamInfo(0, out var outInfo);
            var providesSamples = (outInfo.Flags & MfConstants.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;

            transform.ProcessMessage(MfConstants.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
            transform.ProcessMessage(MfConstants.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero);

            return new H264Encoder(activate, transform, width, height, fps, outInfo.Size, providesSamples);
        }
        catch
        {
            Marshal.ReleaseComObject(transform);
            throw;
        }
    }

    private static List<IMFActivate> EnumerateEncoders(int flags)
    {
        var outputInfo = new MftRegisterTypeInfo { MajorType = MfConstants.MFMediaType_Video, SubType = MfConstants.MFVideoFormat_H264 };
        MfConstants.ThrowIfFailed(MfConstants.MFTEnumEx(MfConstants.MFT_CATEGORY_VIDEO_ENCODER, flags, null, outputInfo, out var arrayPtr, out var count));

        var result = new List<IMFActivate>(Math.Max(count, 0));
        if (arrayPtr == IntPtr.Zero || count == 0)
        {
            return result;
        }

        for (var i = 0; i < count; i++)
        {
            var ptr = Marshal.ReadIntPtr(arrayPtr, i * IntPtr.Size);
            result.Add(ComInterop.WrapAndRelease<IMFActivate>(ptr));
        }
        Marshal.FreeCoTaskMem(arrayPtr);
        return result;
    }

    /// <summary>Modern hardware H.264 encoder MFTs are almost always asynchronous
    /// (MF_TRANSFORM_ASYNC=1) and refuse ProcessInput/ProcessOutput until unlocked via
    /// MF_TRANSFORM_ASYNC_UNLOCK. This drives even an unlocked async MFT by polling
    /// ProcessOutput rather than the fully event-driven IMFMediaEventGenerator model
    /// the docs describe as canonical - simpler, and tolerated by most real encoder
    /// MFTs in practice, but flagged here as the one place this deviates from the
    /// textbook-correct async MFT contract. If a specific hardware encoder rejects this
    /// (unlikely, but possible), the event-driven model is the fallback to implement.</summary>
    private static void ConfigureAsyncUnlockIfNeeded(IMFTransform transform)
    {
        MfConstants.ThrowIfFailed(transform.GetAttributes(out var attributesPtr));
        var attributes = ComInterop.WrapAndRelease<IMFAttributes>(attributesPtr);
        try
        {
            if (attributes.TryGetUInt32(MfConstants.MF_TRANSFORM_ASYNC, out var isAsync) && isAsync != 0)
            {
                attributes.SetUInt32(MfConstants.MF_TRANSFORM_ASYNC_UNLOCK, 1);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(attributes);
        }
    }

    private static void RequestLowLatency(IMFTransform transform)
    {
        MfConstants.ThrowIfFailed(transform.GetAttributes(out var attributesPtr));
        var attributes = ComInterop.WrapAndRelease<IMFAttributes>(attributesPtr);
        try
        {
            // Microsoft documents MF_LOW_LATENCY on IMFTransform::GetAttributes.
            // This requests low delay; stage timings, not attribute acceptance,
            // determine whether a particular encoder actually meets the budget.
            var hr = attributes.SetUINT32(MfConstants.MF_LOW_LATENCY, 1);
            Console.WriteLine($"[H264Encoder] MF_LOW_LATENCY request HRESULT=0x{hr:X8}");
        }
        finally
        {
            Marshal.ReleaseComObject(attributes);
        }
    }

    /// <summary>Output type must be set before input type - until the output type is
    /// set, SetInputType returns MF_E_TRANSFORM_TYPE_NOT_SET (Microsoft's own encoder
    /// documentation).</summary>
    private static void ConfigureTypes(IMFTransform transform, int width, int height, int fps, int targetBitrateKbps)
    {
        MfConstants.MFCreateMediaType(out var outputTypePtr);
        var outputType = ComInterop.Wrap<IMFMediaType>(outputTypePtr);
        outputType.SetGuid(MfConstants.MF_MT_MAJOR_TYPE, MfConstants.MFMediaType_Video);
        outputType.SetGuid(MfConstants.MF_MT_SUBTYPE, MfConstants.MFVideoFormat_H264);
        outputType.SetUInt32(MfConstants.MF_MT_AVG_BITRATE, targetBitrateKbps * 1000);
        outputType.SetUInt64(MfConstants.MF_MT_FRAME_SIZE, ComInterop.PackHiLo((uint)width, (uint)height));
        outputType.SetUInt64(MfConstants.MF_MT_FRAME_RATE, ComInterop.PackHiLo((uint)fps, 1));
        outputType.SetUInt32(MfConstants.MF_MT_INTERLACE_MODE, MfConstants.MFVideoInterlace_Progressive);
        outputType.SetUInt32(MfConstants.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
        MfConstants.ThrowIfFailed(transform.SetOutputType(0, outputTypePtr, 0));
        Marshal.ReleaseComObject(outputType);
        Marshal.Release(outputTypePtr);

        MfConstants.MFCreateMediaType(out var inputTypePtr);
        var inputType = ComInterop.Wrap<IMFMediaType>(inputTypePtr);
        inputType.SetGuid(MfConstants.MF_MT_MAJOR_TYPE, MfConstants.MFMediaType_Video);
        inputType.SetGuid(MfConstants.MF_MT_SUBTYPE, MfConstants.MFVideoFormat_NV12);
        inputType.SetUInt64(MfConstants.MF_MT_FRAME_SIZE, ComInterop.PackHiLo((uint)width, (uint)height));
        inputType.SetUInt64(MfConstants.MF_MT_FRAME_RATE, ComInterop.PackHiLo((uint)fps, 1));
        inputType.SetUInt32(MfConstants.MF_MT_INTERLACE_MODE, MfConstants.MFVideoInterlace_Progressive);
        inputType.SetUInt32(MfConstants.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
        MfConstants.ThrowIfFailed(transform.SetInputType(0, inputTypePtr, 0));
        Marshal.ReleaseComObject(inputType);
        Marshal.Release(inputTypePtr);
    }

    /// <summary>
    /// Encodes one NV12 frame (length must be exactly width*height*3/2 bytes) and
    /// returns zero or more complete H.264 Annex-B access units - almost always exactly
    /// one, but the MFT is free to batch, so callers must send everything returned.
    /// </summary>
    public List<EncodedFrame> EncodeFrame(ReadOnlySpan<byte> nv12Frame, long sampleTimeHns)
    {
        var duration = 10_000_000L / Fps; // 100-ns units
        if (nv12Frame.Length != checked(Width * Height * 3 / 2))
            throw new ArgumentException("NV12 frame dimensions do not match the encoder.", nameof(nv12Frame));
        var samplePtr = CreateInputSample(nv12Frame, sampleTimeHns, duration);
        try
        {
            var hr = _transform.ProcessInput(0, samplePtr, 0);
            // MF_E_NOTACCEPTING would mean the MFT wants ProcessOutput drained first;
            // not expected at 1 in / ~1 out steady state but surfaced rather than
            // silently dropping the frame.
            MfConstants.ThrowIfFailed(hr);
        }
        finally
        {
            Marshal.Release(samplePtr);
        }

        return DrainOutput();
    }

    /// <summary>Returns a sample pointer holding exactly one owned reference - the
    /// caller (EncodeFrame) must release it after passing it to ProcessInput.</summary>
    private static IntPtr CreateInputSample(ReadOnlySpan<byte> nv12Frame, long sampleTime, long duration)
    {
        MfConstants.MFCreateMemoryBuffer(nv12Frame.Length, out var bufferPtr);
        var buffer = ComInterop.Wrap<IMFMediaBuffer>(bufferPtr);
        MfConstants.ThrowIfFailed(buffer.Lock(out var dest, out _, out _));
        unsafe
        {
            nv12Frame.CopyTo(new Span<byte>((void*)dest, nv12Frame.Length));
        }
        MfConstants.ThrowIfFailed(buffer.Unlock());
        MfConstants.ThrowIfFailed(buffer.SetCurrentLength(nv12Frame.Length));

        MfConstants.MFCreateSample(out var samplePtr);
        var sample = ComInterop.Wrap<IMFSample>(samplePtr);
        MfConstants.ThrowIfFailed(sample.AddBuffer(bufferPtr)); // sample AddRefs the buffer itself
        MfConstants.ThrowIfFailed(sample.SetSampleTime(sampleTime));
        MfConstants.ThrowIfFailed(sample.SetSampleDuration(duration));

        Marshal.ReleaseComObject(buffer);
        Marshal.Release(bufferPtr);
        Marshal.ReleaseComObject(sample);
        // samplePtr's original factory reference is deliberately NOT released here -
        // it's the one reference EncodeFrame owns and releases after ProcessInput.
        return samplePtr;
    }

    private List<EncodedFrame> DrainOutput()
    {
        var results = new List<EncodedFrame>();
        while (true)
        {
            var providedSamplePtr = IntPtr.Zero;
            if (!_outputProvidesSamples)
            {
                MfConstants.MFCreateMemoryBuffer(_outputBufferSize, out var providedBufferPtr);
                MfConstants.MFCreateSample(out providedSamplePtr);
                var setupSample = ComInterop.Wrap<IMFSample>(providedSamplePtr);
                MfConstants.ThrowIfFailed(setupSample.AddBuffer(providedBufferPtr));
                Marshal.ReleaseComObject(setupSample);
                Marshal.Release(providedBufferPtr);
            }

            var dataBuffer = new MftOutputDataBuffer { StreamId = 0, Sample = providedSamplePtr, Status = 0, Events = IntPtr.Zero };
            var dataBufferPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftOutputDataBuffer>());
            try
            {
                Marshal.StructureToPtr(dataBuffer, dataBufferPtr, false);
                var hr = _transform.ProcessOutput(0, 1, dataBufferPtr, out _);

                if (hr == MfConstants.MF_E_TRANSFORM_NEED_MORE_INPUT)
                {
                    if (providedSamplePtr != IntPtr.Zero)
                    {
                        Marshal.Release(providedSamplePtr);
                    }
                    break;
                }
                MfConstants.ThrowIfFailed(hr);

                var resultPtr = _outputProvidesSamples
                    ? Marshal.PtrToStructure<MftOutputDataBuffer>(dataBufferPtr).Sample
                    : providedSamplePtr;

                if (resultPtr != IntPtr.Zero)
                {
                    var resultSample = ComInterop.Wrap<IMFSample>(resultPtr);
                    MfConstants.ThrowIfFailed(resultSample.GetSampleTime(out var sampleTimeHns));
                    results.Add(new EncodedFrame(ReadAccessUnit(resultSample), sampleTimeHns));
                    Marshal.ReleaseComObject(resultSample);
                    Marshal.Release(resultPtr);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(dataBufferPtr);
            }
        }
        return results;
    }

    private static byte[] ReadAccessUnit(IMFSample sample)
    {
        MfConstants.ThrowIfFailed(sample.ConvertToContiguousBuffer(out var bufferPtr));
        var buffer = ComInterop.WrapAndRelease<IMFMediaBuffer>(bufferPtr);
        try
        {
            MfConstants.ThrowIfFailed(buffer.Lock(out var src, out _, out var currentLength));
            try
            {
                var bytes = new byte[currentLength];
                Marshal.Copy(src, bytes, 0, currentLength);
                return bytes;
            }
            finally
            {
                buffer.Unlock();
            }
        }
        finally
        {
            Marshal.ReleaseComObject(buffer);
        }
    }

    public void Dispose()
    {
        try
        {
            _transform.ProcessMessage(MfConstants.MFT_MESSAGE_NOTIFY_END_STREAMING, IntPtr.Zero);
        }
        catch
        {
            // Best-effort - the transform may already be in a bad state if we're
            // disposing due to an earlier failure.
        }
        Marshal.ReleaseComObject(_transform);
        _activate.ShutdownObject();
        Marshal.ReleaseComObject(_activate);
    }
}
