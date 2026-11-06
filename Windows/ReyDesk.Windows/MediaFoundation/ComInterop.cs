using System;
using System.Runtime.InteropServices;

namespace ReyDesk.Windows.MediaFoundation;

/// <summary>
/// Small helpers for the classic (non-source-generated) COM interop style used by the
/// [ComImport] interfaces in MfInterfaces.cs.
/// </summary>
internal static class ComInterop
{
    /// <summary>
    /// Wraps a raw COM pointer (as returned by an "out IntPtr" factory function like
    /// MFCreateMediaType) as a managed RCW implementing <typeparamref name="T"/>,
    /// WITHOUT releasing the passed-in reference - GetObjectForIUnknown gets the RCW
    /// its own reference via an internal QueryInterface, so <paramref name="ptr"/>
    /// remains separately valid/owned by the caller. Use this when the raw pointer is
    /// still needed afterward (e.g. to pass to a native API expecting an IntPtr) -
    /// both the pointer and the returned RCW must eventually be released independently.
    /// </summary>
    public static T Wrap<T>(IntPtr ptr) where T : class => (T)Marshal.GetObjectForIUnknown(ptr);

    /// <summary>
    /// Same as <see cref="Wrap{T}"/> but also releases <paramref name="ptr"/>'s
    /// reference immediately - use only when the raw pointer itself is never needed
    /// again (only the RCW's methods will be called from here on).
    /// </summary>
    public static T WrapAndRelease<T>(IntPtr ptr) where T : class
    {
        try
        {
            return Wrap<T>(ptr);
        }
        finally
        {
            Marshal.Release(ptr);
        }
    }

    /// <summary>Packs two 32-bit values into the HI32/LO32 UINT64 layout Media
    /// Foundation uses for MF_MT_FRAME_SIZE/MF_MT_FRAME_RATE/MF_MT_PIXEL_ASPECT_RATIO.</summary>
    public static long PackHiLo(uint hi, uint lo) => unchecked((long)(((ulong)hi << 32) | lo));

    public static void SetGuid(this IMFAttributes attributes, in Guid key, in Guid value) =>
        MfConstants.ThrowIfFailed(attributes.SetGUID(key, value));

    public static void SetUInt32(this IMFAttributes attributes, in Guid key, int value) =>
        MfConstants.ThrowIfFailed(attributes.SetUINT32(key, value));

    public static void SetUInt64(this IMFAttributes attributes, in Guid key, long value) =>
        MfConstants.ThrowIfFailed(attributes.SetUINT64(key, value));

    public static int GetUInt32(this IMFAttributes attributes, in Guid key)
    {
        MfConstants.ThrowIfFailed(attributes.GetUINT32(key, out var value));
        return value;
    }

    public static bool TryGetUInt32(this IMFAttributes attributes, in Guid key, out int value)
    {
        var hr = attributes.GetUINT32(key, out value);
        if (hr == MfConstants.MF_E_ATTRIBUTENOTFOUND)
        {
            return false;
        }
        MfConstants.ThrowIfFailed(hr);
        return true;
    }

    public static void SetGuid(this IMFMediaType mediaType, in Guid key, in Guid value) =>
        MfConstants.ThrowIfFailed(mediaType.SetGUID(key, value));

    public static void SetUInt32(this IMFMediaType mediaType, in Guid key, int value) =>
        MfConstants.ThrowIfFailed(mediaType.SetUINT32(key, value));

    public static void SetUInt64(this IMFMediaType mediaType, in Guid key, long value) =>
        MfConstants.ThrowIfFailed(mediaType.SetUINT64(key, value));
}
