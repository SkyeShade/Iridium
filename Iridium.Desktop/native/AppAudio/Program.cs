using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Iridium.AppAudio;

// Captures one program's audio (or everything except one program) with Windows process loopback and writes it
// to stdout as interleaved 32-bit float stereo at 48 kHz. Status goes to stderr as JSON lines. It stops when
// stdin closes, so it never outlives the Iridium desktop app that started it.
//
//   iridium-app-audio --hwnd <window handle>     audio of the window's process and its children
//   iridium-app-audio --pid <process id>         audio of that process and its children
//   iridium-app-audio --pid <id> --exclude       everything except that process tree (full-screen shares)

const int SampleRate = 48000;
const int Channels = 2;
var iidAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
var iidAudioCaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
var iidCompletionHandler = new Guid("41D949AB-9862-444A-80F6-C261334DA5EB");

uint processId = 0;
var exclude = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--pid" when i + 1 < args.Length && uint.TryParse(args[++i], out var pid):
            processId = pid;
            break;
        case "--hwnd" when i + 1 < args.Length && long.TryParse(args[++i], out var window):
            Native.GetWindowThreadProcessId((nint)window, out processId);
            break;
        case "--exclude":
            exclude = true;
            break;
    }
}
if (processId == 0) return Fail("no target process (pass --pid or a valid --hwnd)", 2);

Native.CoInitializeEx(0, 0); // COINIT_MULTITHREADED
var wrappers = new StrategyBasedComWrappers();

// Activation is asynchronous: hand Windows a completion handler and wait for it to fire.
var handler = new ActivationHandler(wrappers);
var activation = new AudioClientActivationParams { ActivationType = 1, TargetProcessId = processId, ProcessLoopbackMode = exclude ? 1 : 0 };
var activationPointer = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
var variantPointer = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
Marshal.StructureToPtr(activation, activationPointer, false);
Marshal.StructureToPtr(new PropVariantBlob
{
    VarType = 65, BlobSize = (uint)Marshal.SizeOf<AudioClientActivationParams>(), BlobData = activationPointer
}, variantPointer, false);
var handlerUnknown = wrappers.GetOrCreateComInterfaceForObject(handler, CreateComInterfaceFlags.None);
Marshal.QueryInterface(handlerUnknown, in iidCompletionHandler, out var handlerPointer);
var hr = Native.ActivateAudioInterfaceAsync(Native.ProcessLoopbackDevice, in iidAudioClient, variantPointer, handlerPointer, out _);
if (hr < 0) return Fail($"ActivateAudioInterfaceAsync failed 0x{hr:X8}", 3);
if (!handler.Completed.Wait(TimeSpan.FromSeconds(5))) return Fail("audio activation timed out", 3);
if (handler.Result < 0 || handler.Client is null) return Fail($"process loopback activation failed 0x{handler.Result:X8}", 3);
var client = handler.Client;

// Ask for float directly; fall back to 16-bit PCM (converted below) if the engine refuses.
var isFloat = true;
var format = WaveFormatEx.Create(Native.WaveFormatIeeeFloat, Channels, SampleRate, 32);
const uint flags = Native.AudclntStreamflagsLoopback | Native.AudclntStreamflagsEventcallback |
                   Native.AudclntStreamflagsAutoconvertpcm | Native.AudclntStreamflagsSrcDefaultQuality;
hr = Initialize(client, format);
if (hr < 0)
{
    isFloat = false;
    format = WaveFormatEx.Create(Native.WaveFormatPcm, Channels, SampleRate, 16);
    hr = Initialize(client, format);
}
if (hr < 0) return Fail($"IAudioClient.Initialize failed 0x{hr:X8}", 4);

var ready = Native.CreateEvent(0, false, false, 0);
client.SetEventHandle(ready);
hr = client.GetService(in iidAudioCaptureClient, out var capturePointer);
if (hr < 0) return Fail($"GetService(IAudioCaptureClient) failed 0x{hr:X8}", 4);
var capture = (IAudioCaptureClient)wrappers.GetOrCreateObjectForComInstance(capturePointer, CreateObjectFlags.None);
hr = client.Start();
if (hr < 0) return Fail($"IAudioClient.Start failed 0x{hr:X8}", 4);

var running = true;
var stdin = new Thread(() =>
{
    try { using var input = Console.OpenStandardInput(); var buffer = new byte[64]; while (input.Read(buffer, 0, buffer.Length) > 0) { } }
    catch { /* a broken pipe means the parent is gone, same as EOF */ }
    running = false;
}) { IsBackground = true };
stdin.Start();

Console.Error.WriteLine($"{{\"event\":\"ready\",\"pid\":{processId},\"exclude\":{(exclude ? "true" : "false")},\"sampleRate\":{SampleRate},\"channels\":{Channels},\"source\":\"{(isFloat ? "f32" : "s16")}\"}}");
using var output = Console.OpenStandardOutput();
var floats = new float[SampleRate * Channels];
var bytes = new byte[floats.Length * sizeof(float)];
try
{
    while (running)
    {
        Native.WaitForSingleObject(ready, 100);
        while (running && capture.GetNextPacketSize(out var packetFrames) >= 0 && packetFrames > 0)
        {
            if (capture.GetBuffer(out var data, out var frames, out var bufferFlags, out _, out _) < 0) break;
            var samples = (int)frames * Channels;
            if (samples > floats.Length) { floats = new float[samples]; bytes = new byte[samples * sizeof(float)]; }
            unsafe
            {
                if ((bufferFlags & Native.AudclntBufferflagsSilent) != 0) Array.Clear(floats, 0, samples);
                else if (isFloat) new ReadOnlySpan<float>((void*)data, samples).CopyTo(floats);
                else
                {
                    var pcm = new ReadOnlySpan<short>((void*)data, samples);
                    for (var s = 0; s < samples; s++) floats[s] = pcm[s] / 32768f;
                }
            }
            capture.ReleaseBuffer(frames);
            Buffer.BlockCopy(floats, 0, bytes, 0, samples * sizeof(float));
            output.Write(bytes, 0, samples * sizeof(float));
        }
        output.Flush();
    }
}
catch (IOException)
{
    // stdout closed: the app stopped reading, so stop capturing.
}
client.Stop();
Native.CloseHandle(ready);
return 0;

int Initialize(IAudioClient audioClient, WaveFormatEx waveFormat)
{
    var formatPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
    try
    {
        Marshal.StructureToPtr(waveFormat, formatPointer, false);
        // 20 ms buffer; process loopback ignores periodicity in shared mode.
        return audioClient.Initialize(Native.AudclntSharemodeShared, flags, 200_000, 0, formatPointer, 0);
    }
    finally { Marshal.FreeHGlobal(formatPointer); }
}

static int Fail(string message, int code)
{
    Console.Error.WriteLine($"{{\"event\":\"error\",\"message\":\"{message.Replace("\"", "'")}\"}}");
    return code;
}

[GeneratedComClass]
internal sealed partial class ActivationHandler(StrategyBasedComWrappers wrappers) : IActivateAudioInterfaceCompletionHandler, IAgileObject
{
    public ManualResetEventSlim Completed { get; } = new();
    public int Result { get; private set; } = -1;
    public IAudioClient? Client { get; private set; }

    public int ActivateCompleted(nint operation)
    {
        try
        {
            var asyncOperation = (IActivateAudioInterfaceAsyncOperation)wrappers.GetOrCreateObjectForComInstance(operation, CreateObjectFlags.None);
            var hr = asyncOperation.GetActivateResult(out var activateResult, out var activated);
            Result = hr < 0 ? hr : activateResult;
            if (Result >= 0 && activated != 0)
                Client = (IAudioClient)wrappers.GetOrCreateObjectForComInstance(activated, CreateObjectFlags.None);
        }
        catch (Exception exception) { Result = exception.HResult; }
        finally { Completed.Set(); }
        return 0;
    }
}
