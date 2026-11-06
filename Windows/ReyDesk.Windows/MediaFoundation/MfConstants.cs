using System;
using System.Runtime.InteropServices;

namespace ReyDesk.Windows.MediaFoundation;

// Media Foundation P/Invoke entry points + GUID/constant catalogue. GUIDs are copied
// from two verified sources rather than typed from memory (a single wrong hex digit
// silently makes an attribute a no-op, which is very hard to debug after the fact):
//   - Attribute keys (MF_MT_*, MF_TRANSFORM_ASYNC*): naudio/NAudio's MediaFoundation
//     interop (MIT) and, cross-checked, aldentea/MediaFoundation.Net (the open-source
//     "MFNet" project, LGPL, used here only as a read-only cross-check of values, no
//     code copied from it).
//   - Video subtype GUIDs (MFVideoFormat_*): Microsoft's documented
//     "{FOURCC}-0000-0010-8000-00AA00389B71" (DEFINE_MEDIATYPE_GUID) pattern for
//     FourCC-based subtypes, and the documented D3DFMT-based value for ARGB32.
internal static class MfConstants
{
    // --- mfplat.dll ---
    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern void MFStartup(int version, int dwFlags);

    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern void MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern void MFCreateMediaType(out IntPtr ppMFType);

    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern void MFCreateSample(out IntPtr ppIMFSample);

    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern void MFCreateMemoryBuffer(int cbMaxLength, out IntPtr ppBuffer);

    // MftRegisterTypeInfo params may be null (any type) - PreserveSig=false auto-throw
    // doesn't play well with an intentional "no results" success case here, so this one
    // keeps a raw HRESULT return instead.
    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = true)]
    public static extern int MFTEnumEx(
        Guid guidCategory, int flags,
        MftRegisterTypeInfo? pInputType, MftRegisterTypeInfo? pOutputType,
        out IntPtr pppMFTActivate, out int pcMFTActivate);

    public const int MF_VERSION = (0x0002 << 16) | 0x0070;

    // --- MFT_ENUM_FLAG (mftransform.h) ---
    public const int MFT_ENUM_FLAG_SYNCMFT = 0x00000001;
    public const int MFT_ENUM_FLAG_ASYNCMFT = 0x00000002;
    public const int MFT_ENUM_FLAG_HARDWARE = 0x00000004;
    public const int MFT_ENUM_FLAG_SORTANDFILTER = 0x00000040;

    // --- MFT_MESSAGE_TYPE (mftransform.h) ---
    public const int MFT_MESSAGE_COMMAND_FLUSH = 0x00000000;
    public const int MFT_MESSAGE_COMMAND_DRAIN = 0x00000001;
    public const int MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000;
    public const int MFT_MESSAGE_NOTIFY_END_STREAMING = 0x10000001;
    public const int MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;

    // --- MFT_OUTPUT_STREAM_INFO flags (mftransform.h) ---
    public const int MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x00000100;

    // --- HRESULTs actually branched on (mferror.h) ---
    public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    public const int MF_E_NO_MORE_TYPES = unchecked((int)0xC00D36B9);
    public const int MF_E_ATTRIBUTENOTFOUND = unchecked((int)0xC00D36E6);

    // --- MFT categories (mfidl.h) ---
    public static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new("f79eac7d-e545-4387-bdee-d647d7bde42a");

    // --- Major/subtype (mfapi.h / mfobjects.h) ---
    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71");

    // --- Attribute keys (mfapi.h) ---
    // Microsoft Windows SDK mfapi.h; same GUID as CODECAPI_AVLowLatencyMode.
    public static readonly Guid MF_LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid MF_MT_MAJOR_TYPE = new(0x48eba18e, 0xf8c9, 0x4687, 0xbf, 0x11, 0x0a, 0x74, 0xc9, 0xf9, 0x6a, 0x8f);
    public static readonly Guid MF_MT_SUBTYPE = new(0xf7e34c9a, 0x42e8, 0x4714, 0xb7, 0x4b, 0xcb, 0x29, 0xd7, 0x2c, 0x35, 0xe5);
    public static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new(0xc9173739, 0x5e56, 0x461c, 0xb7, 0x13, 0x46, 0xfb, 0x99, 0x5c, 0xb9, 0x5f);
    public static readonly Guid MF_MT_FRAME_SIZE = new(0x1652c33d, 0xd6b2, 0x4012, 0xb8, 0x34, 0x72, 0x03, 0x08, 0x49, 0xa3, 0x7d);
    public static readonly Guid MF_MT_FRAME_RATE = new(0xc459a2e8, 0x3d2c, 0x4e44, 0xb1, 0x32, 0xfe, 0xe5, 0x15, 0x6c, 0x7b, 0xb0);
    public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new(0xc6376a1e, 0x8d0a, 0x4027, 0xbe, 0x45, 0x6d, 0x9a, 0x0a, 0xd3, 0x9b, 0xb6);
    public static readonly Guid MF_MT_INTERLACE_MODE = new(0xe2724bb8, 0xe676, 0x4806, 0xb4, 0xb2, 0xa8, 0xd6, 0xef, 0xb4, 0x4c, 0xcd);
    public static readonly Guid MF_MT_AVG_BITRATE = new(0x20332624, 0xfb0d, 0x4d9e, 0xbd, 0x0d, 0xcb, 0xf6, 0x78, 0x6c, 0x10, 0x2e);
    public static readonly Guid MF_TRANSFORM_ASYNC = new(0xf81a699a, 0x649a, 0x497d, 0x8c, 0x73, 0x29, 0xf8, 0xfe, 0xd6, 0xad, 0x7a);
    public static readonly Guid MF_TRANSFORM_ASYNC_UNLOCK = new(0xe5666d6b, 0x3422, 0x4eb6, 0xa4, 0x21, 0xda, 0x7d, 0xb1, 0xf8, 0xe2, 0x07);

    // MFVideoInterlaceMode: 2 == Progressive.
    public const int MFVideoInterlace_Progressive = 2;

    public static void ThrowIfFailed(int hr)
    {
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }
}
