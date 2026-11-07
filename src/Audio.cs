using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace ValoLive
{
    // ---------- Core Audio (WASAPI) COM arayüzleri ----------
    enum EDataFlow { Render = 0, Capture = 1 }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(EDataFlow flow, int stateMask, out IMMDeviceCollection devices);
        void GetDefaultAudioEndpoint(EDataFlow flow, int role, out IMMDevice device);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        void GetCount(out int count);
        void Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        void OpenPropertyStore(int access, out IPropertyStore store);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        void GetCount(out int count);
        void GetAt(int index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey { public Guid fmtid; public int pid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PropVariant { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr ptr; }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        void Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        void GetBufferSize(out uint frames);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out uint padding);
        void IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        void GetMixFormat(out IntPtr format);
        void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr handle);
        void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        void GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        void ReleaseBuffer(uint frames);
        void GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioRenderClient
    {
        void GetBuffer(uint frames, out IntPtr data);
        void ReleaseBuffer(uint frames, uint flags);
    }

    static class AudioDevices
    {
        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant pv);

        static readonly Guid FriendlyNameFmt = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0");

        /// <summary>Aktif ses çıkışları (oyun sesi): (id, ad). Boş id = Windows varsayılanı.</summary>
        public static List<KeyValuePair<string, string>> ListOutputs() => List(EDataFlow.Render, L.T("Varsayılan ses çıkışı"));

        /// <summary>Aktif mikrofonlar: (id, ad). Boş id = mikrofon yok.</summary>
        public static List<KeyValuePair<string, string>> ListInputs() => List(EDataFlow.Capture, L.T("(mikrofon yok)"));

        static List<KeyValuePair<string, string>> List(EDataFlow flow, string first)
        {
            var list = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("", first) };
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                en.EnumAudioEndpoints(flow, 1, out var col);
                col.GetCount(out int n);
                for (int i = 0; i < n; i++)
                {
                    col.Item(i, out var dev);
                    dev.GetId(out string id);
                    list.Add(new KeyValuePair<string, string>(id, Name(dev)));
                }
            }
            catch { }
            return list;
        }

        static string Name(IMMDevice dev)
        {
            dev.OpenPropertyStore(0, out var store);
            var key = new PropertyKey { fmtid = FriendlyNameFmt, pid = 14 };
            store.GetValue(ref key, out var pv);
            string name = pv.vt == 31 ? Marshal.PtrToStringUni(pv.ptr) : "?";
            PropVariantClear(ref pv);
            return name;
        }
    }

    /// <summary>
    /// WASAPI ses kaynağı: bir çıkışın "loopback" kaydı (oyun sesi) ya da bir mikrofon.
    /// ffmpeg'e ham float PCM yazar ve zamanlamayı kendisi tutar: örnek 0 tam olarak
    /// yayının başlangıç anına (epoch) denk gelir, cihaz saati kaydıkça örnek eklenip
    /// atılarak gerçek zamana hizalı kalır. Böylece ffmpeg'in okuma gecikmesi senkronu bozmaz.
    /// Loopback modunda cihaza sessizlik çalınır; ortam sessizken de akış kesintisiz sürer.
    /// </summary>
    sealed class AudioSource : IDisposable
    {
        const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
        const double DriftToleranceSec = 0.08;
        static readonly Guid FloatSubFormat = new Guid("00000003-0000-0010-8000-00aa00389b71");

        readonly bool loopback;
        public int Channels { get; private set; }
        public int SampleRate { get; private set; }
        public volatile float Volume = 1f;

        volatile bool running;
        volatile Stream output;
        long epochMs;
        long written;   // ffmpeg'e yazılan kare (frame) sayısı
        bool isFloat;
        int bits;
        Thread thread;
        Exception error;
        readonly ManualResetEventSlim ready = new ManualResetEventSlim();

        public AudioSource(bool loopback) { this.loopback = loopback; }

        public void Start(string deviceId)
        {
            running = true;
            thread = new Thread(Run) { IsBackground = true, Name = loopback ? "loopback" : "mic" };
            thread.Start(deviceId);
            if (!ready.Wait(5000)) throw new Exception(L.T("Ses cihazı yanıt vermedi."));
            if (error != null) throw new Exception(L.F("{0} açılamadı: {1}", loopback ? L.T("Oyun sesi") : L.T("Mikrofon"), error.Message), error);
        }

        /// <summary>Ses bu akışa yazılır; örnek 0 = epochMs anı. Bağlı akış yoksa veri atılır.</summary>
        public void Attach(Stream s, long epochMs)
        {
            this.epochMs = epochMs;
            written = -1;
            output = s;
        }

        public void Detach() { output = null; }

        static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        void Run(object arg)
        {
            IAudioClient cap = null, ren = null;
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                IMMDevice dev;
                var id = arg as string;
                var flow = loopback ? EDataFlow.Render : EDataFlow.Capture;
                if (string.IsNullOrEmpty(id)) en.GetDefaultAudioEndpoint(flow, 0, out dev);
                else en.GetDevice(id, out dev);

                var iid = typeof(IAudioClient).GUID;
                dev.Activate(ref iid, 23, IntPtr.Zero, out object o1);
                cap = (IAudioClient)o1;
                cap.GetMixFormat(out IntPtr fmt);
                ReadFormat(fmt);
                cap.Initialize(0, loopback ? AUDCLNT_STREAMFLAGS_LOOPBACK : 0, 10000000, 0, fmt, IntPtr.Zero);

                IAudioRenderClient render = null;
                uint renSize = 0;
                if (loopback)
                {
                    dev.Activate(ref iid, 23, IntPtr.Zero, out object o2);
                    ren = (IAudioClient)o2;
                    ren.Initialize(0, 0, 2000000, 0, fmt, IntPtr.Zero);
                    var rid = typeof(IAudioRenderClient).GUID;
                    ren.GetService(ref rid, out object rs);
                    render = (IAudioRenderClient)rs;
                    ren.GetBufferSize(out renSize);
                    render.GetBuffer(renSize, out _);
                    render.ReleaseBuffer(renSize, AUDCLNT_BUFFERFLAGS_SILENT);
                    ren.Start();
                }
                Marshal.FreeCoTaskMem(fmt);

                var cid = typeof(IAudioCaptureClient).GUID;
                cap.GetService(ref cid, out object cs);
                var capture = (IAudioCaptureClient)cs;
                cap.Start();
                ready.Set();

                var samples = new float[0];
                var shorts = new short[0];
                var bytes = new byte[0];
                while (running)
                {
                    Thread.Sleep(10);
                    if (render != null)
                    {
                        ren.GetCurrentPadding(out uint pad);
                        uint free = renSize - pad;
                        if (free > 0) { render.GetBuffer(free, out _); render.ReleaseBuffer(free, AUDCLNT_BUFFERFLAGS_SILENT); }
                    }

                    while (running)
                    {
                        capture.GetNextPacketSize(out uint packet);
                        if (packet == 0) break;
                        capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _);
                        int n = (int)frames * Channels;
                        if (samples.Length < n) { samples = new float[n]; shorts = new short[n]; bytes = new byte[n * 4]; }

                        if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0) Array.Clear(samples, 0, n);
                        else if (isFloat) Marshal.Copy(data, samples, 0, n);
                        else
                        {
                            Marshal.Copy(data, shorts, 0, n);
                            for (int i = 0; i < n; i++) samples[i] = shorts[i] / 32768f;
                        }
                        capture.ReleaseBuffer(frames);

                        float vol = Volume;
                        if (vol != 1f) for (int i = 0; i < n; i++) samples[i] *= vol;

                        var o = output;
                        if (o == null) continue;
                        Buffer.BlockCopy(samples, 0, bytes, 0, n * 4);
                        try { WriteAligned(o, bytes, (int)frames); }
                        catch { output = null; }
                    }
                }
            }
            catch (Exception e)
            {
                error = e;
                ready.Set();
            }
            finally
            {
                try { cap?.Stop(); } catch { }
                try { ren?.Stop(); } catch { }
            }
        }

        /// <summary>Yazılan örnek sayısını gerçek zamana (epoch'tan beri geçen süre) hizalı tutar.</summary>
        void WriteAligned(Stream o, byte[] bytes, int frames)
        {
            int frameBytes = Channels * 4;
            // Bu paketin son örneği "şimdi" yakalandı; ilk örneğinin olması gereken konumu:
            long expectedStart = (NowMs() - epochMs) * SampleRate / 1000 - frames;
            if (written < 0)
            {
                // İlk paket: epoch ile şimdi arasını sessizlikle doldur.
                written = 0;
                WriteSilence(o, Math.Max(0, expectedStart), frameBytes);
            }
            long diff = expectedStart - written;
            long tol = (long)(SampleRate * DriftToleranceSec);
            int skip = 0;
            if (diff > tol) WriteSilence(o, diff, frameBytes);        // cihaz geride kaldı: boşluğu doldur
            else if (diff < -tol) skip = (int)Math.Min(frames, -diff); // cihaz önde: fazlayı at
            if (skip >= frames) return;
            o.Write(bytes, skip * frameBytes, (frames - skip) * frameBytes);
            written += frames - skip;
        }

        void WriteSilence(Stream o, long frames, int frameBytes)
        {
            var zero = new byte[Math.Min(frames, SampleRate) * frameBytes];
            while (frames > 0)
            {
                int chunk = (int)Math.Min(frames, zero.Length / frameBytes);
                o.Write(zero, 0, chunk * frameBytes);
                frames -= chunk;
                written += chunk;
            }
        }

        void ReadFormat(IntPtr fmt)
        {
            ushort tag = (ushort)Marshal.ReadInt16(fmt, 0);
            Channels = Marshal.ReadInt16(fmt, 2);
            SampleRate = Marshal.ReadInt32(fmt, 4);
            bits = Marshal.ReadInt16(fmt, 14);
            if (tag == 0xFFFE)
            {
                var guid = new byte[16];
                Marshal.Copy(fmt + 24, guid, 0, 16);
                isFloat = new Guid(guid) == FloatSubFormat;
            }
            else isFloat = tag == 3;
            if (!(isFloat && bits == 32) && !(!isFloat && bits == 16))
                throw new NotSupportedException(L.F("Desteklenmeyen ses biçimi ({0} bit).", bits));
        }

        public void Dispose()
        {
            running = false;
            output = null;
            thread?.Join(1000);
        }
    }
}

