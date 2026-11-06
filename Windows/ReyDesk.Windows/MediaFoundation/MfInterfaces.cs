using System;
using System.Runtime.InteropServices;

namespace ReyDesk.Windows.MediaFoundation;

// Raw Media Foundation COM interfaces, declared with classic [ComImport]/[PreserveSig]
// (same style already used in Capture/Direct3D11Interop.cs for WinRT interop) rather
// than .NET 8's newer [GeneratedComInterface] source-generator, to avoid pulling in
// extra ComWrappers plumbing for a handful of interfaces.
//
// Method order within each interface is the actual COM vtable order and must never be
// reordered/edited casually - a wrong order silently calls the wrong native method.
// Verified against NAudio.Wasapi's MediaFoundation interop layer (MIT licensed,
// https://github.com/naudio/NAudio/tree/main/src/NAudio.Wasapi/MediaFoundation/Interfaces),
// itself sourced from the Windows SDK's mfobjects.h/mftransform.h - not hand-derived here.
// IMFMediaType/IMFSample/IMFActivate all extend IMFAttributes; their vtables are flattened
// (IMFAttributes methods repeated verbatim) rather than expressed via C# interface
// inheritance, matching that same source.

[ComImport]
[Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    [PreserveSig] int GetItem(in Guid guidKey, IntPtr pValue);
    [PreserveSig] int GetItemType(in Guid guidKey, out int pType);
    [PreserveSig] int CompareItem(in Guid guidKey, IntPtr value, out int pbResult);
    [PreserveSig] int Compare(IntPtr pTheirs, int matchType, out int pbResult);
    [PreserveSig] int GetUINT32(in Guid guidKey, out int punValue);
    [PreserveSig] int GetUINT64(in Guid guidKey, out long punValue);
    [PreserveSig] int GetDouble(in Guid guidKey, out double pfValue);
    [PreserveSig] int GetGUID(in Guid guidKey, out Guid pguidValue);
    [PreserveSig] int GetStringLength(in Guid guidKey, out int pcchLength);
    [PreserveSig] int GetString(in Guid guidKey, IntPtr pwszValue, int cchBufSize, out int pcchLength);
    [PreserveSig] int GetAllocatedString(in Guid guidKey, out IntPtr ppwszValue, out int pcchLength);
    [PreserveSig] int GetBlobSize(in Guid guidKey, out int pcbBlobSize);
    [PreserveSig] int GetBlob(in Guid guidKey, IntPtr pBuf, int cbBufSize, out int pcbBlobSize);
    [PreserveSig] int GetAllocatedBlob(in Guid guidKey, out IntPtr ppBuf, out int pcbSize);
    [PreserveSig] int GetUnknown(in Guid guidKey, in Guid riid, out IntPtr ppv);
    [PreserveSig] int SetItem(in Guid guidKey, IntPtr value);
    [PreserveSig] int DeleteItem(in Guid guidKey);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(in Guid guidKey, int unValue);
    [PreserveSig] int SetUINT64(in Guid guidKey, long unValue);
    [PreserveSig] int SetDouble(in Guid guidKey, double fValue);
    [PreserveSig] int SetGUID(in Guid guidKey, in Guid guidValue);
    [PreserveSig] int SetString(in Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig] int SetBlob(in Guid guidKey, IntPtr pBuf, int cbBufSize);
    [PreserveSig] int SetUnknown(in Guid guidKey, IntPtr pUnknown);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out int pcItems);
    [PreserveSig] int GetItemByIndex(int unIndex, out Guid pGuidKey, IntPtr pValue);
    [PreserveSig] int CopyAllItems(IntPtr pDest);
}

[ComImport]
[Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType
{
    [PreserveSig] int GetItem(in Guid guidKey, IntPtr pValue);
    [PreserveSig] int GetItemType(in Guid guidKey, out int pType);
    [PreserveSig] int CompareItem(in Guid guidKey, IntPtr value, out int pbResult);
    [PreserveSig] int Compare(IntPtr pTheirs, int matchType, out int pbResult);
    [PreserveSig] int GetUINT32(in Guid guidKey, out int punValue);
    [PreserveSig] int GetUINT64(in Guid guidKey, out long punValue);
    [PreserveSig] int GetDouble(in Guid guidKey, out double pfValue);
    [PreserveSig] int GetGUID(in Guid guidKey, out Guid pguidValue);
    [PreserveSig] int GetStringLength(in Guid guidKey, out int pcchLength);
    [PreserveSig] int GetString(in Guid guidKey, IntPtr pwszValue, int cchBufSize, out int pcchLength);
    [PreserveSig] int GetAllocatedString(in Guid guidKey, out IntPtr ppwszValue, out int pcchLength);
    [PreserveSig] int GetBlobSize(in Guid guidKey, out int pcbBlobSize);
    [PreserveSig] int GetBlob(in Guid guidKey, IntPtr pBuf, int cbBufSize, out int pcbBlobSize);
    [PreserveSig] int GetAllocatedBlob(in Guid guidKey, out IntPtr ppBuf, out int pcbSize);
    [PreserveSig] int GetUnknown(in Guid guidKey, in Guid riid, out IntPtr ppv);
    [PreserveSig] int SetItem(in Guid guidKey, IntPtr value);
    [PreserveSig] int DeleteItem(in Guid guidKey);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(in Guid guidKey, int unValue);
    [PreserveSig] int SetUINT64(in Guid guidKey, long unValue);
    [PreserveSig] int SetDouble(in Guid guidKey, double fValue);
    [PreserveSig] int SetGUID(in Guid guidKey, in Guid guidValue);
    [PreserveSig] int SetString(in Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig] int SetBlob(in Guid guidKey, IntPtr pBuf, int cbBufSize);
    [PreserveSig] int SetUnknown(in Guid guidKey, IntPtr pUnknown);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out int pcItems);
    [PreserveSig] int GetItemByIndex(int unIndex, out Guid pGuidKey, IntPtr pValue);
    [PreserveSig] int CopyAllItems(IntPtr pDest);
    // IMFMediaType's own methods
    [PreserveSig] int GetMajorType(out Guid pguidMajorType);
    [PreserveSig] int IsCompressedFormat(out int pfCompressed);
    [PreserveSig] int IsEqual(IntPtr pIMediaType, out int pdwFlags);
    [PreserveSig] int GetRepresentation(in Guid guidRepresentation, out IntPtr ppvRepresentation);
    [PreserveSig] int FreeRepresentation(in Guid guidRepresentation, IntPtr pvRepresentation);
}

[ComImport]
[Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample
{
    [PreserveSig] int GetItem(in Guid guidKey, IntPtr pValue);
    [PreserveSig] int GetItemType(in Guid guidKey, out int pType);
    [PreserveSig] int CompareItem(in Guid guidKey, IntPtr value, out int pbResult);
    [PreserveSig] int Compare(IntPtr pTheirs, int matchType, out int pbResult);
    [PreserveSig] int GetUINT32(in Guid guidKey, out int punValue);
    [PreserveSig] int GetUINT64(in Guid guidKey, out long punValue);
    [PreserveSig] int GetDouble(in Guid guidKey, out double pfValue);
    [PreserveSig] int GetGUID(in Guid guidKey, out Guid pguidValue);
    [PreserveSig] int GetStringLength(in Guid guidKey, out int pcchLength);
    [PreserveSig] int GetString(in Guid guidKey, IntPtr pwszValue, int cchBufSize, out int pcchLength);
    [PreserveSig] int GetAllocatedString(in Guid guidKey, out IntPtr ppwszValue, out int pcchLength);
    [PreserveSig] int GetBlobSize(in Guid guidKey, out int pcbBlobSize);
    [PreserveSig] int GetBlob(in Guid guidKey, IntPtr pBuf, int cbBufSize, out int pcbBlobSize);
    [PreserveSig] int GetAllocatedBlob(in Guid guidKey, out IntPtr ppBuf, out int pcbSize);
    [PreserveSig] int GetUnknown(in Guid guidKey, in Guid riid, out IntPtr ppv);
    [PreserveSig] int SetItem(in Guid guidKey, IntPtr value);
    [PreserveSig] int DeleteItem(in Guid guidKey);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(in Guid guidKey, int unValue);
    [PreserveSig] int SetUINT64(in Guid guidKey, long unValue);
    [PreserveSig] int SetDouble(in Guid guidKey, double fValue);
    [PreserveSig] int SetGUID(in Guid guidKey, in Guid guidValue);
    [PreserveSig] int SetString(in Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig] int SetBlob(in Guid guidKey, IntPtr pBuf, int cbBufSize);
    [PreserveSig] int SetUnknown(in Guid guidKey, IntPtr pUnknown);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out int pcItems);
    [PreserveSig] int GetItemByIndex(int unIndex, out Guid pGuidKey, IntPtr pValue);
    [PreserveSig] int CopyAllItems(IntPtr pDest);
    // IMFSample's own methods
    [PreserveSig] int GetSampleFlags(out int pdwSampleFlags);
    [PreserveSig] int SetSampleFlags(int dwSampleFlags);
    [PreserveSig] int GetSampleTime(out long phnsSampleTime);
    [PreserveSig] int SetSampleTime(long hnsSampleTime);
    [PreserveSig] int GetSampleDuration(out long phnsSampleDuration);
    [PreserveSig] int SetSampleDuration(long hnsSampleDuration);
    [PreserveSig] int GetBufferCount(out int pdwBufferCount);
    [PreserveSig] int GetBufferByIndex(int dwIndex, out IntPtr ppBuffer);
    [PreserveSig] int ConvertToContiguousBuffer(out IntPtr ppBuffer);
    [PreserveSig] int AddBuffer(IntPtr pBuffer);
    [PreserveSig] int RemoveBufferByIndex(int dwIndex);
    [PreserveSig] int RemoveAllBuffers();
    [PreserveSig] int GetTotalLength(out int pcbTotalLength);
    [PreserveSig] int CopyToBuffer(IntPtr pBuffer);
}

[ComImport]
[Guid("045FA593-8799-42b8-BC8D-8968C6453507")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    [PreserveSig] int Lock(out IntPtr ppbBuffer, out int pcbMaxLength, out int pcbCurrentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out int pcbCurrentLength);
    [PreserveSig] int SetCurrentLength(int cbCurrentLength);
    [PreserveSig] int GetMaxLength(out int pcbMaxLength);
}

[ComImport]
[Guid("7FEE9E9A-4A89-47a6-899C-B6A53A70FB67")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFActivate
{
    [PreserveSig] int GetItem(in Guid guidKey, IntPtr pValue);
    [PreserveSig] int GetItemType(in Guid guidKey, out int pType);
    [PreserveSig] int CompareItem(in Guid guidKey, IntPtr value, out int pbResult);
    [PreserveSig] int Compare(IntPtr pTheirs, int matchType, out int pbResult);
    [PreserveSig] int GetUINT32(in Guid guidKey, out int punValue);
    [PreserveSig] int GetUINT64(in Guid guidKey, out long punValue);
    [PreserveSig] int GetDouble(in Guid guidKey, out double pfValue);
    [PreserveSig] int GetGUID(in Guid guidKey, out Guid pguidValue);
    [PreserveSig] int GetStringLength(in Guid guidKey, out int pcchLength);
    [PreserveSig] int GetString(in Guid guidKey, IntPtr pwszValue, int cchBufSize, out int pcchLength);
    [PreserveSig] int GetAllocatedString(in Guid guidKey, out IntPtr ppwszValue, out int pcchLength);
    [PreserveSig] int GetBlobSize(in Guid guidKey, out int pcbBlobSize);
    [PreserveSig] int GetBlob(in Guid guidKey, IntPtr pBuf, int cbBufSize, out int pcbBlobSize);
    [PreserveSig] int GetAllocatedBlob(in Guid guidKey, out IntPtr ppBuf, out int pcbSize);
    [PreserveSig] int GetUnknown(in Guid guidKey, in Guid riid, out IntPtr ppv);
    [PreserveSig] int SetItem(in Guid guidKey, IntPtr value);
    [PreserveSig] int DeleteItem(in Guid guidKey);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(in Guid guidKey, int unValue);
    [PreserveSig] int SetUINT64(in Guid guidKey, long unValue);
    [PreserveSig] int SetDouble(in Guid guidKey, double fValue);
    [PreserveSig] int SetGUID(in Guid guidKey, in Guid guidValue);
    [PreserveSig] int SetString(in Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig] int SetBlob(in Guid guidKey, IntPtr pBuf, int cbBufSize);
    [PreserveSig] int SetUnknown(in Guid guidKey, IntPtr pUnknown);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out int pcItems);
    [PreserveSig] int GetItemByIndex(int unIndex, out Guid pGuidKey, IntPtr pValue);
    [PreserveSig] int CopyAllItems(IntPtr pDest);
    // IMFActivate's own methods
    [PreserveSig] int ActivateObject(in Guid riid, out IntPtr ppv);
    [PreserveSig] int ShutdownObject();
    [PreserveSig] int DetachObject();
}

[ComImport]
[Guid("bf94c121-5b05-4e6f-8000-ba598961414d")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFTransform
{
    [PreserveSig] int GetStreamLimits(out int pdwInputMinimum, out int pdwInputMaximum, out int pdwOutputMinimum, out int pdwOutputMaximum);
    [PreserveSig] int GetStreamCount(out int pcInputStreams, out int pcOutputStreams);
    [PreserveSig] int GetStreamIds(int dwInputIdArraySize, IntPtr pdwInputIDs, int dwOutputIdArraySize, IntPtr pdwOutputIDs);
    [PreserveSig] int GetInputStreamInfo(int dwInputStreamId, out MftInputStreamInfo pStreamInfo);
    [PreserveSig] int GetOutputStreamInfo(int dwOutputStreamId, out MftOutputStreamInfo pStreamInfo);
    [PreserveSig] int GetAttributes(out IntPtr pAttributes);
    [PreserveSig] int GetInputStreamAttributes(int dwInputStreamId, out IntPtr pAttributes);
    [PreserveSig] int GetOutputStreamAttributes(int dwOutputStreamId, out IntPtr pAttributes);
    [PreserveSig] int DeleteInputStream(int dwStreamId);
    [PreserveSig] int AddInputStreams(int cStreams, IntPtr adwStreamIDs);
    [PreserveSig] int GetInputAvailableType(int dwInputStreamId, int dwTypeIndex, out IntPtr ppType);
    [PreserveSig] int GetOutputAvailableType(int dwOutputStreamId, int dwTypeIndex, out IntPtr ppType);
    [PreserveSig] int SetInputType(int dwInputStreamId, IntPtr pType, int dwFlags);
    [PreserveSig] int SetOutputType(int dwOutputStreamId, IntPtr pType, int dwFlags);
    [PreserveSig] int GetInputCurrentType(int dwInputStreamId, out IntPtr ppType);
    [PreserveSig] int GetOutputCurrentType(int dwOutputStreamId, out IntPtr ppType);
    [PreserveSig] int GetInputStatus(int dwInputStreamId, out int pdwFlags);
    [PreserveSig] int GetOutputStatus(out int pdwFlags);
    [PreserveSig] int SetOutputBounds(long hnsLowerBound, long hnsUpperBound);
    [PreserveSig] int ProcessEvent(int dwInputStreamId, IntPtr pEvent);
    [PreserveSig] int ProcessMessage(int eMessage, IntPtr ulParam);
    [PreserveSig] int ProcessInput(int dwInputStreamId, IntPtr pSample, int dwFlags);
    [PreserveSig] int ProcessOutput(int dwFlags, int cOutputBufferCount, IntPtr pOutputSamples, out int pdwStatus);
}

[StructLayout(LayoutKind.Sequential)]
internal struct MftInputStreamInfo
{
    public long MaxLatency;
    public int Flags;
    public int Size;
    public int MaxLookahead;
    public int Alignment;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MftOutputStreamInfo
{
    public int Flags;
    public int Size;
    public int Alignment;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MftOutputDataBuffer
{
    public int StreamId;
    public IntPtr Sample;
    public int Status;
    public IntPtr Events;
}

[StructLayout(LayoutKind.Sequential)]
internal class MftRegisterTypeInfo
{
    public Guid MajorType;
    public Guid SubType;
}
