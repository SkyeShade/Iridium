using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Iridium.AppAudio;

// The handful of WASAPI pieces process loopback needs. Source-generated COM keeps it Native AOT friendly.

[GeneratedComInterface]
[Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
internal partial interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, nint format, nint audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint bufferFrames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint paddingFrames);
    [PreserveSig] int IsFormatSupported(int shareMode, nint format, out nint closestMatch);
    [PreserveSig] int GetMixFormat(out nint deviceFormat);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(nint eventHandle);
    [PreserveSig] int GetService(in Guid riid, out nint service);
}

[GeneratedComInterface]
[Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
internal partial interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out nint data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}

[GeneratedComInterface]
[Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
internal partial interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig] int GetActivateResult(out int activateResult, out nint activatedInterface);
}

[GeneratedComInterface]
[Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
internal partial interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig] int ActivateCompleted(nint operation);
}

// Marker interface: ActivateAudioInterfaceAsync requires an agile completion handler.
[GeneratedComInterface]
[Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90")]
internal partial interface IAgileObject;

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormatEx
{
    public ushort FormatTag;
    public ushort Channels;
    public uint SamplesPerSec;
    public uint AvgBytesPerSec;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort Size;

    public static WaveFormatEx Create(ushort formatTag, ushort channels, uint sampleRate, ushort bitsPerSample)
    {
        var blockAlign = (ushort)(channels * bitsPerSample / 8);
        return new WaveFormatEx
        {
            FormatTag = formatTag, Channels = channels, SamplesPerSec = sampleRate, BitsPerSample = bitsPerSample,
            BlockAlign = blockAlign, AvgBytesPerSec = sampleRate * blockAlign, Size = 0
        };
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientActivationParams
{
    public int ActivationType;       // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1
    public uint TargetProcessId;
    public int ProcessLoopbackMode;  // 0 = include target process tree, 1 = exclude it
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariantBlob
{
    [FieldOffset(0)] public ushort VarType; // VT_BLOB = 65
    [FieldOffset(8)] public uint BlobSize;
    [FieldOffset(16)] public nint BlobData;
}

internal static partial class Native
{
    public const int AudclntSharemodeShared = 0;
    public const uint AudclntStreamflagsLoopback = 0x00020000;
    public const uint AudclntStreamflagsEventcallback = 0x00040000;
    public const uint AudclntStreamflagsAutoconvertpcm = 0x80000000;
    public const uint AudclntStreamflagsSrcDefaultQuality = 0x08000000;
    public const uint AudclntBufferflagsSilent = 0x2;
    public const ushort WaveFormatPcm = 1;
    public const ushort WaveFormatIeeeFloat = 3;
    public const string ProcessLoopbackDevice = @"VAD\Process_Loopback";

    [LibraryImport("Mmdevapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int ActivateAudioInterfaceAsync(string deviceInterfacePath, in Guid riid, nint activationParams,
        nint completionHandler, out nint activationOperation);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW")]
    public static partial nint CreateEvent(nint attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset,
        [MarshalAs(UnmanagedType.Bool)] bool initialState, nint name);

    [LibraryImport("kernel32.dll")]
    public static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(nint reserved, uint coInit);
}
