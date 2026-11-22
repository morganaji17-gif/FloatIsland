using System.Diagnostics;
using System.Runtime.InteropServices;
using FloatIsland.Models;

#pragma warning disable CA1416 // process loopback is runtime-guarded

namespace FloatIsland.Services;

/// <summary>
/// Captures ONLY the audio rendered by the Spotify process tree using the Windows
/// process-loopback WASAPI activation (Windows 10 build 20348+).
///
/// Audio is analysed in memory (RMS + FFT band energies + beat detection) and is
/// never recorded, buffered to disk, or shared. If process activation fails the
/// service raises <see cref="Failed"/> and stops — it never silently falls back
/// to system-wide ("what you hear") loopback.
/// </summary>
public sealed class ProcessLoopbackCapture : IDisposable
{
    private const int FftSize = 1024;
    private const int Channels = 2;
    private const int SampleRate = 48000;

    private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    private const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    private const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    private const string VirtualDevice = @"VAD\Process_Loopback";
    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    /// <summary>Raised (from the capture thread) when process capture cannot start.</summary>
    public event EventHandler<string>? Failed;

    public bool IsRunning { get; private set; }

    private readonly object _sync = new();
    private readonly AudioLevels _shared = new();

    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private EventWaitHandle? _frameEvent;
    private uint _targetPid;

    // --- analysis state (capture thread only) ---------------------------
    private readonly float[] _re = new float[FftSize];
    private readonly float[] _im = new float[FftSize];
    private readonly float[] _window = new float[FftSize];
    private readonly float[] _mono = new float[FftSize];
    private readonly float[] _bars = new float[AudioLevels.BarCount];
    private float[] _samples = new float[4096];
    private readonly int[] _bandLo = new int[AudioLevels.BarCount];
    private readonly int[] _bandHi = new int[AudioLevels.BarCount];
    private int _monoFill;
    private float _bass, _mid, _treble, _rms, _peak, _beat, _bassSlow;
    private long _lastBeatTicks;

    // -------------------------------------------------------------------
    public ProcessLoopbackCapture()
    {
        for (int i = 0; i < FftSize; i++)
            _window[i] = 0.5f - 0.5f * (float)Math.Cos(2 * Math.PI * i / (FftSize - 1));
        BuildBandRanges();
        _shared.Reset();
    }

    /// <summary>Finds the Spotify process id whose audio should be captured.</summary>
    public static uint? FindSpotifyProcessId()
    {
        try
        {
            var procs = Process.GetProcessesByName("Spotify");
            if (procs.Length == 0) return null;

            // Oldest process is the main one; INCLUDE_TARGET_PROCESS_TREE covers helpers.
            Process? best = null;
            var bestStart = DateTime.MaxValue;
            foreach (var p in procs)
            {
                try
                {
                    if (p.HasExited) continue;
                    var t = p.StartTime;
                    if (t < bestStart) { bestStart = t; best = p; }
                }
                catch { best ??= p; }
            }
            return best != null ? (uint)best.Id : (uint)procs[0].Id;
        }
        catch (Exception ex)
        {
            Log.Warn($"Spotify pid lookup: {ex.Message}");
            return null;
        }
    }

    public bool Start(uint pid)
    {
        if (IsRunning) return true;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
        {
            Fail("Process loopback requires Windows 10 build 20348 or later.");
            return false;
        }
        _targetPid = pid;
        _cts = new CancellationTokenSource();
        _frameEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
        IsRunning = true;
        _thread = new Thread(CaptureThread) { IsBackground = true, Name = "SonoCapture" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        return true;
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        try { _cts?.Cancel(); _frameEvent?.Set(); } catch { }
        try { _thread?.Join(1500); } catch { }
        _thread = null;
        _cts?.Dispose(); _cts = null;
        _frameEvent?.Dispose(); _frameEvent = null;

        lock (_sync)
        {
            _shared.Reset();
            _monoFill = 0;
            _bass = _mid = _treble = _rms = _beat = 0f;
        }
    }

    public void ReadSnapshot(AudioLevels dst)
    {
        lock (_sync) dst.CopyFrom(_shared);
    }

    private void Fail(string reason)
    {
        Log.Warn($"Audio capture: {reason}");
        Failed?.Invoke(this, reason);
    }

    // -------------------------------------------------------------------
    private void CaptureThread()
    {
        var token = _cts!.Token;
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        try
        {
            CoInitializeEx(IntPtr.Zero, 0x0);       // COINIT_MULTITHREADED
            RoInitialize(1);                        // RO_INIT_MULTITHREADED

            client = ActivateProcessLoopback(_targetPid);
            if (client == null) { Fail("Could not activate process loopback for Spotify."); return; }

            var fmt = new WaveFormatEx
            {
                wFormatTag = 3,                     // WAVE_FORMAT_IEEE_FLOAT
                nChannels = Channels,
                nSamplesPerSec = SampleRate,
                wBitsPerSample = 32
            };
            fmt.nBlockAlign = (ushort)(fmt.nChannels * fmt.wBitsPerSample / 8);
            fmt.nAvgBytesPerSec = fmt.nSamplesPerSec * fmt.nBlockAlign;

            try
            {
                client.Initialize(0, AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK
                    | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM, 0, 0, ref fmt, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Fail($"AudioClient.Initialize failed: {Hr(ex)}");
                return;
            }

            client.GetBufferSize(out _);
            client.SetEventHandle(_frameEvent!.SafeWaitHandle.DangerousGetHandle());

            object obj;
            var captureIid = IID_IAudioCaptureClient;
            client.GetService(ref captureIid, out obj);
            capture = (IAudioCaptureClient)obj;

            client.Start();
            Log.Info($"Audio capture: started (pid {_targetPid}, include process tree)");

            while (!token.IsCancellationRequested)
            {
                if (!_frameEvent.WaitOne(200)) { Decay(); continue; }

                while (!token.IsCancellationRequested)
                {
                    capture.GetNextPacketSize(out uint packet);
                    if (packet == 0) break;

                    capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _);
                    if (frames > 0)
                    {
                        if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0 || data == IntPtr.Zero)
                            ProcessSilence(frames);
                        else
                            ProcessSamples(data, frames);
                    }
                    capture.ReleaseBuffer(frames);
                }
            }

            try { client.Stop(); } catch { }
        }
        catch (Exception ex)
        {
            Fail(Hr(ex));
        }
        finally
        {
            WithCom(capture, c => Marshal.ReleaseComObject(c));
            WithCom(client, c => Marshal.ReleaseComObject(c));
            RoUninitialize();
            CoUninitialize();
            lock (_sync) { _shared.Reset(); }
            Log.Info("Audio capture: stopped");
        }
    }

    private static string Hr(Exception ex) => (ex as COMException) is { } c
        ? $"HRESULT 0x{c.HResult:X8}: {ex.Message}"
        : ex.Message;

    private static void WithCom<T>(T? o, Action<T> a) where T : class
    {
        if (o == null) return;
        try { a(o); } catch { }
    }

    // --- audio analysis -------------------------------------------------
    private void EnsureSamples(int count)
    {
        if (_samples.Length < count) _samples = new float[count];
    }

    private void ProcessSamples(IntPtr data, uint frames)
    {
        int n = (int)frames * Channels;
        EnsureSamples(n);
        Marshal.Copy(data, _samples, 0, n);

        double sumSq = 0;
        float peak = 0;
        for (int f = 0, s = 0; f < frames; f++, s += Channels)
        {
            float m = (_samples[s] + _samples[s + 1]) * 0.5f;
            sumSq += (double)m * m;
            float a = m < 0 ? -m : m;
            if (a > peak) peak = a;
            if (_monoFill < FftSize) _mono[_monoFill++] = m;
        }

        float rms = (float)Math.Sqrt(sumSq / Math.Max(1, frames));
        Feed(rms, peak);

        if (_monoFill >= FftSize)
        {
            AnalyzeSpectrum();
            _monoFill = 0;
        }
    }

    private void ProcessSilence(uint frames)
    {
        _monoFill = 0;
        DecayTowardsIdle();
        Feed(0f, 0f);
    }

    private void Decay()
    {
        // No packets for a while (nothing rendering) → ease towards idle.
        DecayTowardsIdle();
        Feed(0f, 0f);
    }

    private void DecayTowardsIdle()
    {
        for (int i = 0; i < _bars.Length; i++) _bars[i] *= 0.85f;
        _bass *= 0.85f; _mid *= 0.85f; _treble *= 0.85f;
    }

    private void Feed(float rms, float peak)
    {
        _rms = Smooth(_rms, rms, 0.6f, 0.15f);
        _peak = Math.Max(peak, _peak * 0.90f);

        // Beat: bass energy well above its slow average, with a refractory period.
        _bassSlow = _bassSlow * 0.965f + _bass * 0.035f;
        long now = Environment.TickCount64;
        if (_bass > 0.22f && _bassSlow > 0.02f && _bass > _bassSlow * 1.45f && now - _lastBeatTicks > 220)
        {
            _beat = 1f;
            _lastBeatTicks = now;
        }
        else
        {
            _beat *= 0.80f;
        }

        lock (_sync)
        {
            Array.Copy(_bars, _shared.Bars, _bars.Length);
            _shared.Bass = _bass;
            _shared.Mid = _mid;
            _shared.Treble = _treble;
            _shared.Rms = _rms;
            _shared.Peak = _peak;
            _shared.Beat = _beat;
            _shared.Active = IsRunning;
        }
    }

    private static float Smooth(float cur, float target, float attack, float release)
    {
        float k = target > cur ? attack : release;
        return cur + (target - cur) * k;
    }

    private void AnalyzeSpectrum()
    {
        for (int i = 0; i < FftSize; i++) { _re[i] = _mono[i] * _window[i]; _im[i] = 0f; }
        Fft(_re, _im);

        float bass = 0, mid = 0, treble = 0;
        int bn = 0, mn = 0, tn = 0;
        for (int i = 1; i < FftSize / 2; i++)
        {
            float freq = (float)i * SampleRate / FftSize;
            float mag = (float)Math.Sqrt(_re[i] * _re[i] + _im[i] * _im[i]) / (FftSize / 2f);
            if (freq < 250f) { bass += mag; bn++; }
            else if (freq < 2000f) { mid += mag; mn++; }
            else if (freq < 16000f) { treble += mag; tn++; }
        }
        _bass = Smooth(_bass, Norm(bn > 0 ? bass / bn : 0f), 0.6f, 0.14f);
        _mid = Smooth(_mid, Norm(mn > 0 ? mid / mn : 0f), 0.6f, 0.14f);
        _treble = Smooth(_treble, Norm(tn > 0 ? treble / tn : 0f), 0.6f, 0.14f);

        for (int b = 0; b < _bars.Length; b++)
        {
            float sum = 0;
            int count = 0;
            for (int i = _bandLo[b]; i <= _bandHi[b]; i++)
            {
                float mag = (float)Math.Sqrt(_re[i] * _re[i] + _im[i] * _im[i]) / (FftSize / 2f);
                sum += mag; count++;
            }
            float v = Norm(count > 0 ? sum / count : 0f);
            _bars[b] = Smooth(_bars[b], v, 0.65f, 0.16f);
        }
    }

    /// <summary>Maps a linear FFT magnitude to 0..1 using a -72 dB .. -8 dB window.</summary>
    private static float Norm(float mag)
    {
        float db = 20f * (float)Math.Log10(mag + 1e-7f);
        float v = (db + 72f) / 64f;
        if (v < 0f) v = 0f;
        else if (v > 1f) v = 1f;
        return v;
    }

    private void BuildBandRanges()
    {
        const float f0 = 45f, f1 = 16000f;
        for (int b = 0; b < _bandLo.Length; b++)
        {
            double lo = f0 * Math.Pow(f1 / f0, (double)b / _bandLo.Length);
            double hi = f0 * Math.Pow(f1 / f0, (double)(b + 1) / _bandLo.Length);
            int ilo = Math.Max(1, (int)(lo * FftSize / SampleRate));
            int ihi = Math.Min(FftSize / 2 - 1, Math.Max(ilo, (int)(hi * FftSize / SampleRate)));
            _bandLo[b] = ilo;
            _bandHi[b] = ihi;
        }
    }

    private static void Fft(float[] re, float[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            float wr = (float)Math.Cos(ang), wi = (float)Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                float cr = 1f, ci = 0f;
                int half = len >> 1;
                for (int k = 0; k < half; k++)
                {
                    int a = i + k, b = a + half;
                    float ur = re[a], ui = im[a];
                    float vr = re[b] * cr - im[b] * ci;
                    float vi = re[b] * ci + im[b] * cr;
                    re[a] = ur + vr; im[a] = ui + vi;
                    re[b] = ur - vr; im[b] = ui - vi;
                    float ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = ncr;
                }
            }
        }
    }

    // --- activation -----------------------------------------------------
    private IAudioClient? ActivateProcessLoopback(uint pid)
    {
        var ap = new AudioClientActivationParams
        {
            ActivationType = 1,                 // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            TargetProcessId = pid,
            ProcessLoopbackMode = 0             // INCLUDE_TARGET_PROCESS_TREE
        };

        int structSize = Marshal.SizeOf<AudioClientActivationParams>();
        IntPtr paramsPtr = Marshal.AllocHGlobal(structSize);
        IntPtr pvPtr = Marshal.AllocHGlobal(24);   // PROPVARIANT on x64
        try
        {
            Marshal.StructureToPtr(ap, paramsPtr, false);

            Marshal.WriteInt64(pvPtr, 0, 0);
            Marshal.WriteInt64(pvPtr, 8, 0);
            Marshal.WriteInt64(pvPtr, 16, 0);
            Marshal.WriteInt16(pvPtr, 0, 65);              // VT_BLOB
            Marshal.WriteInt32(pvPtr, 8, structSize);      // blob.cbSize
            Marshal.WriteIntPtr(pvPtr, 16, paramsPtr);     // blob.pBlobData

            var handler = new ActivationHandler();
            var iid = IID_IAudioClient;
            int hr = ActivateAudioInterfaceAsync(VirtualDevice, in iid, pvPtr, handler, out var op);
            if (hr < 0) { Marshal.ThrowExceptionForHR(hr); }

            var client = handler.Result;
            if (op != null) Marshal.ReleaseComObject(op);
            return client;
        }
        finally
        {
            Marshal.FreeHGlobal(pvPtr);
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    [ComVisible(true)]
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly TaskCompletionSource<IAudioClient> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            try
            {
                op.GetActivateResult(out int hr, out object unk);
                if (hr != 0)
                {
                    _tcs.TrySetException(Marshal.GetExceptionForHR(hr) ?? new COMException("Activation failed", hr));
                    return;
                }
                _tcs.TrySetResult((IAudioClient)unk);
            }
            catch (Exception ex) { _tcs.TrySetException(ex); }
        }

        public IAudioClient Result => _tcs.Task.GetAwaiter().GetResult();
    }

    public void Dispose() => Stop();

    // --- interop --------------------------------------------------------
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct AudioClientActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        void Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity,
            [In] ref WaveFormatEx pFormat, IntPtr audioSessionGuid);
        void GetBufferSize(out uint numBufferFrames);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out uint padding);
        void IsFormatSupported(int shareMode, [In] ref WaveFormatEx pFormat, out IntPtr closestMatch);
        void GetMixFormat(out IntPtr deviceFormat);
        void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr eventHandle);
        void GetService([In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        void GetBuffer(out IntPtr pData, out uint numFramesRead, out uint flags,
            out long devicePosition, out long qpcPosition);
        void ReleaseBuffer(uint numFramesRead);
        void GetNextPacketSize(out uint numFramesInNextPacket);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult,
            [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        in Guid riid, IntPtr activationParams,
        [In] IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("combase.dll")] private static extern int RoInitialize(int initType);
    [DllImport("combase.dll")] private static extern void RoUninitialize();
}
