using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ValoLive
{
    sealed class StreamOptions
    {
        public List<Target> Targets = new List<Target>(); // yayının gittiği platformlar (test kaydında tek hedef: dosya)
        public string Output;          // test kaydının .mp4 dosya yolu
        public bool ToFile;
        public int DurationSec;        // sadece test kaydı için; uygulama bu sürede durdurur
        public int Width, Height, BitrateK;
        public int ScreenWidth, ScreenHeight;
        public bool Denoise;
        public bool WindowCapture;     // Valorant penceresi açıkken yalnızca onu yakala (Windows Graphics Capture); değilse ekran
        public OverlayRenderer Overlay;
        public Func<string> Masks;     // CaptureGuard'ın örtü filtreleri (ekran karesine, panelden önce); her yakalama başlangıcında sorulur
        public List<Layer> Layers = new List<Layer>(); // panelin üstüne bindirilen küçük animasyonlu katmanlar (radar, chat)
        public bool Flip, Zoom;        // chat eğlencesi: ters ekran ve nişangâh büyüteci (grafikte kapalı durur, canlı komutla açılır)
        public bool Gpu;               // ekran kartı motoru (valocap.exe); açılamazsa ffmpeg yoluna dönülür
        public int GpuPriority;        // motorun ekran kartı önceliği (-7..7, 0 normal)
    }

    sealed class Streamer
    {
        Process proc;
        bool stopping;
        readonly Dictionary<string, string> progress = new Dictionary<string, string>();
        readonly Queue<string> lastErrors = new Queue<string>();

        public event Action<Dictionary<string, string>> Progress;
        /// <summary>(beklenmedik mi, son hata satırları)</summary>
        public event Action<bool, string> Exited;

        /// <summary>Yayın sürüyor: en az bir platform bağlı ya da yeniden bağlanıyor.</summary>
        public bool Running => !exitFired && opts != null && (pubs.Any(p => p.Alive) || Active().Any());

        IEnumerable<TargetRun> Active() { lock (runs) return runs.Values.Where(r => r.State == TargetState.Connecting || r.State == TargetState.Live || r.State == TargetState.Retrying).ToList(); }
        /// <summary>Şu anki yakalama yalnızca oyun penceresini alıyor (ekranın geri kalanı görüntüye girmiyor).</summary>
        public bool WindowOnly => capWindow != IntPtr.Zero && Running;

        public static string LogPath => Path.Combine(Settings.Dir, "ffmpeg.log");

        public static string FindFfmpeg()
        {
            var local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(local)) return local;
            var pkgs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WinGet\Packages");
            if (Directory.Exists(pkgs))
            {
                foreach (var dir in Directory.GetDirectories(pkgs, "Gyan.FFmpeg*"))
                {
                    var hit = Directory.GetFiles(dir, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                    if (hit != null) return hit;
                }
            }
            return "ffmpeg";
        }

        static string Inv(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

        const int PanelFps = 15, HiddenFps = 4;
        /// <summary>setpts ifadesi: duvar saatinin bir sonraki 1/fps sınırı (girdi ancak o kadar sık okunur).</summary>
        static string Clock(int fps) => $"(floor(RTCTIME*{fps}/1000000)+1)/{fps}/TB";

        static string RawInput(AudioSource s, string epoch, string pipe) =>
            $"-itsoffset {epoch} -f f32le -ar {s.SampleRate} -ac {s.Channels} -thread_queue_size 1024 -i \"{pipe}\" ";

        /// <summary>
        /// Zamanlama: her şey aynı mutlak saatte (Unix epoch) ifade edilir (-copyts), çıkışta sıfıra çekilir.
        /// - Sesler (tek gerçek girişler) örnek sayısıyla zamanlanır; örnek 0 = epoch (AudioSource garanti eder).
        /// - Video grafiğinin hiç girişi yoktur: ekran (ddagrab) ve panel (movie, bir pipe'tan PNG akışı)
        ///   grafiğin içindeki kaynaklardır. Ekran karesi gerçek saati (RTCTIME) alır, panel epoch'tan
        ///   15 FPS ilerler; ffmpeg panel karesini video ilerledikçe ister, yazıcı da en güncelini verir.
        /// Böylece ffmpeg'in zamanlayıcısı hiçbir girişi beklerken ekran yakalamayı durdurmaz.
        /// </summary>
        static string BuildArgs(StreamOptions o, AudioSource game, AudioSource mic, long epochMs, long baseEpochMs, string gamePipe, string micPipe, string ovPipe, List<string> layerPipes, IntPtr window)
        {
            string epoch = Inv(epochMs / 1000.0);
            // stats_period: ffmpeg stdin'deki canlı komutlara bu aralıkla bakar (örtü gecikmesi).
            // filter_complex_threads 2: ffmpeg varsayılan olarak her karede 12 iş parçacığını birden uyandırıp
            // oyunun çekirdeklerine dalıyordu (ani takılmalar). Kalan filtreler 2 iş parçacığıyla rahat yetişiyor.
            var a = new StringBuilder("-hide_banner -nostats -stats_period 0.05 -progress pipe:2 -copyts -filter_complex_threads 2 ");
            a.Append(Environment.GetEnvironmentVariable("VALOLIVE_FFDEBUG") == "1" ? "-loglevel info -debug_ts " : "-loglevel error ");
            a.Append(RawInput(game, epoch, gamePipe));
            if (mic != null) a.Append(RawInput(mic, epoch, micPipe));

            // VALOLIVE_TESTSRC=1: ekran yerine yapay test görüntüsü (ekranı kaydetmeden geliştirme testi için).
            // Pencere yakalama (gfxcapture = Windows Graphics Capture): yalnızca oyunun kendi karesi kopyalanır; masaüstü
            // çoğaltması (ddagrab) gibi tüm ekranı her karede birleştirmeye zorlamaz. Kare yalnızca pencere çizildikçe gelir
            // (oyun sürekli çizer); boyut ekran boyutuna sabitlenir ki panel ve örtüler aynı yere otursun.
            string screen = window != IntPtr.Zero
                ? $"gfxcapture=hwnd={window.ToInt64()}:capture_cursor=0:max_framerate=60:width={o.ScreenWidth}:height={o.ScreenHeight}:resize_mode=scale_aspect,setpts=RTCTIME/1000000/TB,hwdownload,format=bgra"
                : Environment.GetEnvironmentVariable("VALOLIVE_TESTSRC") == "1"
                ? $"testsrc2=s={o.ScreenWidth}x{o.ScreenHeight}:r=60,realtime,setpts=RTCTIME/1000000/TB,format=bgra"
                : "ddagrab=output_idx=0:framerate=60:draw_mouse=0,setpts=RTCTIME/1000000/TB,hwdownload,format=bgra";
            var fc = new StringBuilder(screen + (o.Masks?.Invoke() ?? ""));
            // Ters ekran: kapalıyken kareye dokunmadan geçer. Örtülerden sonra: örtüler ekranın gerçek yerine oturur.
            if (o.Flip) fc.Append(",hflip@fh=enable=0,vflip@fv=enable=0");
            // Büyüteç: nişangâhın çevresi 2 kat büyütülüp ortaya bindirilir (yalnızca açıkken karışır).
            // framestep@zs: kapalıyken kolu 30 karede bire indirir; bgra ölçekleme her karede çekirdeğin ~%30'unu yiyordu.
            if (o.Zoom)
            {
                int z = Math.Max(60, (int)(140 * o.ScreenHeight / 1080.0)) * 2;
                fc.Append($",split[m0][zs];[zs]framestep@zs=step=30:enable=1,crop={z}:{z}:{(o.ScreenWidth - z) / 2}:{(o.ScreenHeight - z) / 2},scale={2 * z}:{2 * z}:flags=bicubic[zm];" +
                          $"[m0][zm]overlay@zoom=x={(o.ScreenWidth - 2 * z) / 2}:y={(o.ScreenHeight - 2 * z) / 2}:enable=0:format=auto");
            }
            fc.Append("[m];");
            // Panel ve katmanlar ham BGRA borulardan gelir (rawvideo: çözme yok, yalnızca kopya).
            // Zaman damgası duvar saatinin bir sonraki 1/fps sınırı (ekran karesiyle aynı saat): ffmpeg yeni kareyi
            // ancak görüntü o ana gelince ister. İfade öncekine bakmadığı için hız canlı değiştirilebilir
            // (PREV_OUTPTS'li ifade değişince damga donup ffmpeg'i kilitliyordu): görünmeyen katman saniyede 4 kez okunur.
            // probesize=32: açılışta saniyelerce kareyi önden okuyup tamponlamasın (yoksa ilk saniyeler eski/boş görünür).
            // Filtre sözdiziminde '\' iki kat kaçış ister: \ -> \\\\
            string Movie(string pipe, int w, int h, string pts, int fps, string label, int up = 1) =>
                $"movie={pipe.Replace(@"\", @"\\\\")}:f=rawvideo:format_opts=video_size={w}x{h}\\\\:pixel_format=bgra\\\\:probesize=32," +
                $"settb=1/1000,setpts@{pts}={Clock(fps)}" +
                (up > 1 ? $",scale=iw*{up}:ih*{up}:flags=bilinear" : "") + $"[{label}];";
            fc.Append(Movie(ovPipe, o.ScreenWidth, o.ScreenHeight, "pp", o.Overlay.Blank ? HiddenFps : PanelFps, "ov"));
            for (int i = 0; i < layerPipes.Count; i++)
            {
                var l = o.Layers[i];
                fc.Append(Movie(layerPipes[i], l.W, l.H, "lp" + i, l.Shown ? l.Fps : HiddenFps, "ly" + i, l.Up));
            }
            // Boş panel ve görünmeyen katmanlar karıştırılmaz; sonrasını canlı komutlar açar/kapatır (BlankChanged, ShownChanged).
            fc.Append($"[m][ov]overlay@ov=eof_action=repeat:format=auto:enable={(o.Overlay.Blank ? 0 : 1)}");
            for (int i = 0; i < layerPipes.Count; i++)
                fc.Append($"[o{i}];[o{i}][ly{i}]overlay@ly{i}=x={o.Layers[i].X}:y={o.Layers[i].Y}:eof_action=repeat:format=auto:enable={(o.Layers[i].Shown ? 1 : 0)}");
            // RGB→YUV dönüşümü CPU'da değil NVENC'te (CPU'da 1080p60 için çekirdeğin ~%40'ı gidiyordu).
            // NVENC BT.601 sınırlı aralık matrisiyle dönüştürür; akış da öyle etiketlenir (renkler birebir kalır).
            // Etiket şart: yoksa akış "GBR, full range" görünür ve Kick (IVS) bunu yanlış yapılandırılmış sayar.
            if (o.Height != o.ScreenHeight) fc.Append($",scale={o.Width}:{o.Height}:flags=bicubic");
            fc.Append(",format=bgra,fps=60,setparams=range=tv:colorspace=smpte170m:color_primaries=bt709:color_trc=bt709[v]");
            // Ses ayrı bir filtre grafiğinde: video grafiği ses girişlerini beklemesin.
            a.Append($"-filter_complex \"{fc}\" -filter_complex \"{AudioGraph(o, mic, epoch)}\" -map [v] -map [a] ");

            int b = o.BitrateK;
            a.Append($"-c:v h264_nvenc -preset p5 -tune hq -rc cbr -b:v {b}k -maxrate {b}k -bufsize {b}k ");
            // fps_mode vfr: kareleri ddagrab'ın verdiği gibi geçir (MP4'ün varsayılan CFR'si mutlak
            // zaman damgalarıyla eksik kareleri doldurmaya çalışıp boru hattını kilitliyordu).
            // Kick en fazla 2 sn'de bir anahtar kare ister. -g kare sayar; FPS düşünce aralık 2 sn'yi
            // aşıyordu. Bu yüzden anahtar kare zamana göre zorlanır (copyts'te t mutlak saattir, fark alınır).
            a.Append("-fps_mode vfr -g 120 -forced-idr 1 -force_key_frames \"expr:if(isnan(prev_forced_t),1,gte(t-prev_forced_t,1.95))\" ");
            a.Append("-bf 2 -rc-lookahead 20 -profile:v high -spatial-aq 1 ");
            a.Append("-colorspace smpte170m -color_primaries bt709 -color_trc bt709 -color_range tv ");
            // Çıkış zamanı yayının ilk başladığı ana göre: yakalama yeniden başlasa da zaman ileri akmaya devam eder.
            a.Append(Tail(baseEpochMs));
            return a.ToString();
        }

        static string AudioGraph(StreamOptions o, AudioSource mic, string epoch)
        {
            const string fmt = "aresample=48000,aformat=channel_layouts=stereo";
            var af = new StringBuilder($"[0:a]{fmt}");
            if (mic != null)
            {
                af.Append($"[g];[1:a]{fmt},highpass=f=90");
                if (o.Denoise) af.Append(",afftdn=nf=-25");
                af.Append("[mic];[g][mic]amix=inputs=2:duration=first:normalize=0");
            }
            // Sınırlayıcı: oyun ve mikrofon tavansız toplanıyor; silah sesiyle konuşma üst üste binince 1.0 aşılıp
            // yayında patlayan cızırtı oluyordu. Zirveler -1 dB'de yumuşakça bastırılır, altındaki ses değişmez.
            af.Append(",alimiter=limit=0.89:attack=5:release=50:level=disabled");
            // Ses yolu zaman damgasını sıfırdan başlatıyor; sondaki asetpts onu örnek sayısından
            // mutlak saate geri çevirir (örnek 0 = epoch, AudioSource bunu garanti eder).
            af.Append($",asetpts=N/SR/TB+{epoch}/TB[a]");
            return af.ToString();
        }

        static string Tail(long baseEpochMs) =>
            // Çıkış zamanı yayının ilk başladığı ana göre: yakalama yeniden başlasa da zaman ileri akmaya devam eder.
            $"-c:a aac -b:a 160k -ar 48000 -output_ts_offset -{Inv(baseEpochMs / 1000.0)} " +
            // Yayıncı sürece MPEG-TS: her yeni oturumun başı "kesinti" işaretli, ek yerinde bozuk paket uyarısı olmaz.
            "-f mpegts -mpegts_flags +initial_discontinuity pipe:1";

        /// <summary>
        /// Ekran kartı motoru açıkken ffmpeg yalnızca sesi kodlar ve birleştirir: görüntü valocap'tan hazır H.264
        /// (MPEG-TS) gelir ve dokunulmadan kopyalanır. valocap'ın zaman damgası 10 sn + kare/60'tan başlar ve kare 0
        /// epoch anındadır; -itsoffset bunu seslerle aynı mutlak saate taşır.
        /// </summary>
        static string BuildGpuArgs(StreamOptions o, AudioSource game, AudioSource mic, long epochMs, long baseEpochMs, string gamePipe, string micPipe, string videoPipe)
        {
            string epoch = Inv(epochMs / 1000.0);
            var a = new StringBuilder("-hide_banner -nostats -stats_period 0.05 -progress pipe:2 -copyts ");
            a.Append(Environment.GetEnvironmentVariable("VALOLIVE_FFDEBUG") == "1" ? "-loglevel info -debug_ts " : "-loglevel error ");
            a.Append(RawInput(game, epoch, gamePipe));
            if (mic != null) a.Append(RawInput(mic, epoch, micPipe));
            a.Append($"-itsoffset {Inv(epochMs / 1000.0 - 10)} -f mpegts -probesize 500000 -analyzeduration 500000 -thread_queue_size 1024 -i \"{videoPipe}\" ");
            a.Append($"-filter_complex \"{AudioGraph(o, mic, epoch)}\" -map {(mic != null ? 2 : 1)}:v -map [a] -c:v copy ");
            a.Append(Tail(baseEpochMs));
            return a.ToString();
        }

        /// <summary>Yayıncı süreç: yakalamadan gelen MPEG-TS'i yeniden kodlamadan Kick'e (ya da dosyaya) iletir.</summary>
        static string PublisherArgs(StreamOptions o, string output)
        {
            // -readrate KULLANMA: gerçek yayında (6000 kbps, gerçek ses kaynaklarıyla) akış Kick'te ~5 fps
            // donuk göründü; test görüntüsüyle yeniden üretilemedi. Açılıştaki ~3 sn'lik birikmiş gönderim
            // 6000'de Kick'in sınırı içinde kalıyor (yalnızca kaldırılan 7500 seçeneğinde bağlantı kopuyordu).
            var a = new StringBuilder("-hide_banner -nostats -loglevel error -probesize 2000000 -analyzeduration 2000000 -f mpegts -i pipe:0 -map 0 -c copy ");
            if (o.ToFile) a.Append($"-movflags +faststart -y \"{output}\"");
            else a.Append($"-flvflags no_duration_filesize -f flv \"{output}\"");
            return a.ToString();
        }

        /// <summary>Ses için named pipe: ffmpeg bağlanınca kaynak bu pipe'a yazmaya başlar.</summary>
        static NamedPipeServerStream OpenPipe(AudioSource src, long epochMs, out string path)
        {
            var name = "valolive_" + Guid.NewGuid().ToString("N");
            path = @"\\.\pipe\" + name;
            var pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 1 << 20);
            pipe.BeginWaitForConnection(ar =>
            {
                try { pipe.EndWaitForConnection(ar); src.Attach(pipe, epochMs); }
                catch { }
            }, null);
            return pipe;
        }

        /// <summary>
        /// Panel için pipe: ffmpeg her bağlandığında bir yazıcı, en güncel PNG'yi art arda yazar. Pipe tamponu
        /// bilerek çok küçük: boş panel PNG'leri birkaç KB olduğundan büyük tampon yüzlerce eski kare biriktirip
        /// paneli saniyelerce geciktiriyordu. Küçük tamponla yazma ffmpeg okudukça ilerler, kare taze kalır.
        /// ffmpeg filtre grafiğini önce deneme amaçlı kurup kapattığı için pipe birden çok kez açılır;
        /// her bağlantıdan sonra yeni bir dinleyici açılır.
        /// </summary>
        sealed class OverlayFeed : IDisposable
        {
            readonly string name = "valolive_ov_" + Guid.NewGuid().ToString("N");
            readonly Func<byte[]> current;
            readonly List<NamedPipeServerStream> pipes = new List<NamedPipeServerStream>();
            volatile bool closed;

            public string Path => @"\\.\pipe\" + name;

            public OverlayFeed(Func<byte[]> current) { this.current = current; Listen(); }

            void Listen()
            {
                if (closed) return;
                var pipe = new NamedPipeServerStream(name, PipeDirection.Out, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 4096);
                lock (pipes) pipes.Add(pipe);
                pipe.BeginWaitForConnection(ar =>
                {
                    try { pipe.EndWaitForConnection(ar); } catch { return; }
                    Listen();
                    new Thread(() =>
                    {
                        try { while (!closed) { var png = current(); pipe.Write(png, 0, png.Length); } }
                        catch { } // ffmpeg bu bağlantıyı kapattı
                    }) { IsBackground = true, Name = "overlay-pipe" }.Start();
                }, null);
            }

            public void Dispose()
            {
                closed = true;
                lock (pipes) foreach (var p in pipes) { try { p.Dispose(); } catch { } }
            }
        }

        /// <summary>
        /// Ekran kartı motoru için panel ve katmanlar: her biri bir paylaşılan bellek bölgesi ({seq, w, h, 0} + w*h*4 BGRA).
        /// Tek iş parçacığı yeni çizilmiş kareyi (Current dizisi değişince) bir kez kopyalar; boru, kodlama, tekrar yok.
        /// Sıra kilidi: seq yazım sırasında tek, bitince çift; valocap tek ya da okurken değişmiş seq'i atlar.
        /// </summary>
        sealed class SharedFeed : IDisposable
        {
            sealed class Item { public MemoryMappedFile Map; public MemoryMappedViewAccessor View; public Func<byte[]> Current; public byte[] Last; public int Seq; public IntPtr Ptr; }
            readonly string prefix = "valolive_gpu_" + Guid.NewGuid().ToString("N") + "_";
            readonly List<Item> items = new List<Item>();
            volatile bool closed;

            /// <summary>Bölgeyi oluşturur, adını verir (valocap açılmadan önce var olmalı).</summary>
            public string Add(Func<byte[]> current, int w, int h)
            {
                string name = prefix + items.Count;
                var map = MemoryMappedFile.CreateNew(name, 16 + (long)w * h * 4);
                var view = map.CreateViewAccessor();
                var ptr = view.SafeMemoryMappedViewHandle.DangerousGetHandle() + (int)view.PointerOffset;
                Marshal.WriteInt32(ptr, 4, w);
                Marshal.WriteInt32(ptr, 8, h);
                items.Add(new Item { Map = map, View = view, Current = current, Ptr = ptr });
                return name;
            }

            public void Start() => new Thread(Run) { IsBackground = true, Name = "gpu-feed" }.Start();

            void Run()
            {
                while (!closed)
                {
                    foreach (var it in items)
                    {
                        var c = it.Current();
                        if (c == it.Last || c == null) continue;
                        lock (it)
                        {
                            if (closed) return;
                            int len = (int)Math.Min(c.Length, it.View.Capacity - 16);
                            Marshal.WriteInt32(it.Ptr, 0, ++it.Seq);
                            Thread.MemoryBarrier();
                            Marshal.Copy(c, 0, it.Ptr + 16, len);
                            Thread.MemoryBarrier();
                            Marshal.WriteInt32(it.Ptr, 0, ++it.Seq);
                        }
                        it.Last = c;
                    }
                    Thread.Sleep(8);
                }
            }

            public void Dispose()
            {
                closed = true;
                foreach (var it in items) lock (it) { try { it.View.Dispose(); it.Map.Dispose(); } catch { } }
            }
        }

        /// <summary>mic null ise mikrofonsuz yayın. Sesler pipe'lardan gider; stdin 'q' (düzgün kapat) için boş kalır.</summary>
        readonly object stdinLock = new object();
        List<string> pendingCmds;  // ffmpeg süreci başlamadan gelen komutlar

        /// <summary>
        /// Çalışan grafiğe canlı filtre komutu ("hedef komut değer"). ffmpeg stdin'de 'c' tuşunu okur,
        /// satırın kalanı "hedef zaman komut değer" (zaman -1 = hemen).
        /// </summary>
        public void SendCommand(string cmd)
        {
            int sp = cmd.IndexOf(' ');
            lock (stdinLock)
            {
                string line = gpuMode ? Translate(cmd) : "c" + cmd.Substring(0, sp) + " -1" + cmd.Substring(sp) + "\n";
                if (line == null) return;
                if (pendingCmds != null) { pendingCmds.Add(line); return; }
                var p = gpuMode ? engine : proc;
                if (p == null || p.HasExited || stopping) return;
                try { p.StandardInput.Write(line); p.StandardInput.Flush(); } catch { }
            }
        }

        // ---------- Ekran kartı motoru (valocap.exe) ----------

        Process engine;                // çalışan valocap (gpuMode iken)
        volatile bool gpuMode;         // çalışan yakalama motorla mı
        bool gpuBroken;                // bu yayında motor açılamadı: ffmpeg yoluna dönüldü
        readonly int[][] gpuMasks = { new int[4], new int[4], new int[4], new int[4] };

        public static string FindEngine()
        {
            var p = Path.Combine(Path.GetDirectoryName(typeof(Streamer).Assembly.Location), "valocap.exe");
            return File.Exists(p) ? p : null;
        }

        /// <summary>
        /// Uygulamanın geri kalanı ffmpeg filtre komutları gönderir (CaptureGuard, chat eğlencesi); motor açıkken
        /// bunlar valocap komutlarına çevrilir. Karşılığı olmayanlar (vflip, framestep, setpts) motorda gereksizdir.
        /// </summary>
        string Translate(string cmd)
        {
            var p = cmd.Split(' ');
            if (p.Length < 3) return null;
            switch (p[0])
            {
                case "drawbox@vlc": return $"cover {p[2]}\n";
                case "hflip@fh": return $"flip {p[2]}\n";
                case "overlay@zoom": return $"zoom {p[2]}\n";
            }
            if (p[0].StartsWith("drawbox@vlm") && int.TryParse(p[0].Substring(11), out int i) && i >= 0 && i < 4 &&
                "xywh".IndexOf(p[1]) is int k && k >= 0 && p[1].Length == 1 && int.TryParse(p[2], out int v))
            {
                gpuMasks[i][k] = v;
                return $"mask {i} {string.Join(" ", gpuMasks[i])}\n";
            }
            return null;
        }

        /// <summary>CaptureGuard'ın başlangıç filtrelerinden (drawbox) motorun başlangıç komutları.</summary>
        List<string> InitialGpuCommands(string masks)
        {
            var cmds = new List<string>();
            foreach (Match m in Regex.Matches(masks ?? "", @"drawbox@vlm(\d)=x=(-?\d+):y=(-?\d+):w=(-?\d+):h=(-?\d+)"))
            {
                int i = int.Parse(m.Groups[1].Value);
                if (i > 3) continue;
                for (int k = 0; k < 4; k++) gpuMasks[i][k] = int.Parse(m.Groups[k + 2].Value);
                cmds.Add($"mask {i} {string.Join(" ", gpuMasks[i])}\n");
            }
            var c = Regex.Match(masks ?? "", @"drawbox@vlc=[^,\[]*enable=(\d)");
            if (c.Success) cmds.Add($"cover {c.Groups[1].Value}\n");
            cmds.Add($"panel {(opts.Overlay.Blank ? 0 : 1)}\n");
            for (int i = 0; i < opts.Layers.Count; i++) cmds.Add($"layer {i} {(opts.Layers[i].Shown ? 1 : 0)}\n");
            return cmds;
        }

        /// <summary>
        /// valocap'ı başlatır ve video pipe'ını oluşturup "ready" diyene kadar bekler (ffmpeg ancak sonra bağlanabilir).
        /// Açılamazsa null: çağıran ffmpeg yoluna döner.
        /// </summary>
        Process StartEngine(long epochMs, IntPtr window, SharedFeed feed, out string videoPipe, out string grab)
        {
            videoPipe = @"\\.\pipe\valolive_v_" + Guid.NewGuid().ToString("N");
            grab = GameFrames.Attach(window);   // rakip hafızası ve yayın anları oyun karesini motordan okur
            var o = opts;
            var a = new StringBuilder($"--out \"{videoPipe}\" --epoch {epochMs} --size {o.Width}x{o.Height} --canvas {o.ScreenWidth}x{o.ScreenHeight} " +
                                      $"--fps 60 --bitrate {o.BitrateK} --fill {CaptureGuard.Fill.Substring(2)} ");
            a.Append(window != IntPtr.Zero ? $"--window {window.ToInt64()} " : "--monitor 0 ");
            a.Append($"--grab {grab} --gpu-priority {o.GpuPriority} ");
            a.Append($"--panel {feed.Add(() => o.Overlay.Current, o.ScreenWidth, o.ScreenHeight)} ");
            foreach (var l in o.Layers)
            {
                var layer = l;
                a.Append($"--layer {feed.Add(() => layer.Current, l.W, l.H)},{l.X},{l.Y},{l.W},{l.H},{l.Up} ");
            }
            Log("ENGINE " + a);
            var p = new Process
            {
                StartInfo = new ProcessStartInfo(FindEngine(), a.ToString())
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                },
                EnableRaisingEvents = true,
            };
            var ready = new ManualResetEventSlim();
            string firstError = null;
            p.OutputDataReceived += (s, e) =>
            {
                if (e.Data == null) { ready.Set(); return; }
                if (e.Data == "ready") { ready.Set(); return; }
                if (e.Data.StartsWith("error")) firstError = firstError ?? e.Data;
                Log("[engine] " + e.Data);
                // Kaynak değişimi yanıtı: başaramadıysa eski yol (yakalamayı yeniden başlat).
                if (sourcePending && (e.Data.StartsWith("source ") || e.Data.StartsWith("error")))
                {
                    sourcePending = false;
                    if (e.Data.StartsWith("error") && !stopping) { planned = true; KillCapture(); }
                }
                if (!e.Data.StartsWith("stats") || ++engineStats % 6 == 0) Diag.Event("MOTOR", e.Data); // istatistik 30 sn'de bir
            };
            p.Start();
            p.BeginOutputReadLine();
            if (!ready.Wait(5000) || p.HasExited)
            {
                Diag.Event("MOTOR", "ekran kartı motoru açılamadı, ffmpeg yoluna dönülüyor · " + (firstError ?? (p.HasExited ? "kod " + p.ExitCode : "zaman aşımı")));
                try { if (!p.HasExited) p.Kill(); } catch { }
                GameFrames.Detach(grab);
                return null;
            }
            feed.Start();
            return p;
        }
        int engineStats, engineFails;

        // Süreçler: her platform için bir "yayıncı" (Publisher) bağlantıyı yayın boyunca açık tutar; tek "yakalama"
        // (proc) ekranı, sesi ve paneli bir kez kodlayıp MPEG-TS olarak hepsine akıtır. Ekran yakalama kopunca
        // (UAC, mod değişimi: DXGI ACCESS_LOST) yalnızca yakalama yeniden başlar; platform bağlantıları kapanmaz.
        // Bir platform koparsa yalnızca onun yayıncısı yeniden bağlanır, ötekiler ve yakalama sürer.
        volatile Publisher[] pubs = new Publisher[0];
        readonly Dictionary<string, TargetRun> runs = new Dictionary<string, TargetRun>();
        volatile bool fatal, exitFired;
        StreamOptions opts;
        AudioSource gameSrc, micSrc;
        long baseEpochMs;
        Thread pumpThread;
        readonly Queue<DateTime> recent = new Queue<DateTime>();

        // Bekçi: yeniden başlatma zamanlayıcısı alan olarak tutulur (yerel değişkendeki Timer çöp toplayıcıya
        // gidip hiç tetiklenmeyebiliyordu: yakalama ölü kalıyor, yayına dakikalarca görüntü gitmiyordu).
        Timer watchdog, statsTimer;
        DateTime restartDue = DateTime.MaxValue;  // yakalamanın yeniden başlatılacağı an
        DateTime capStartedAt, capDiedAt = DateTime.MinValue, lastFrameAt;
        long lastFrame, totalFrames, capAttempt;
        volatile bool awaitingFirstFrame, capDone;
        // 5 sn'lik istatistik penceresi (pompa ve ilerleme satırlarından)
        long winFrameStart; DateTime winStart, audioStatAt;

        /// <summary>Bir platformun bu yayındaki bağlantı geçmişi (yeniden bağlanma kararları için).</summary>
        sealed class TargetRun
        {
            public Target Target;
            public TargetState State;
            public int Attempts;     // art arda başarısız bağlantı
            public bool EverUp;      // en az bir kez 10 sn ayakta kaldı
            public int QuickDrops;   // ayağa kalktıktan sonra 60 sn dolmadan kopma (üst üste)
            public Timer Retry;
        }

        /// <summary>Platform durumu değişti: (hedef, durum, deneme sayısı).</summary>
        public event Action<Target, TargetState, int> TargetChanged;

        /// <summary>Yakalama kendini toparlarken (yayın açık kalır) bildirilir.</summary>
        public event Action Recovering;

        /// <summary>5 sn'de bir: kare hızı, platform başına en uzun gönderim beklemesi (ms), yakalama süreci yok mu.</summary>
        public event Action<double, Dictionary<Target, double>, bool> Health;

        public void Start(StreamOptions o, AudioSource game, AudioSource mic)
        {
            stopping = false;
            lock (lastErrors) lastErrors.Clear();
            recent.Clear();
            if (opts?.Overlay != null) opts.Overlay.BlankChanged -= OnOverlayBlank;
            opts = o; gameSrc = game; micSrc = mic;
            o.Overlay.BlankChanged += OnOverlayBlank;
            for (int i = 0; i < o.Layers.Count; i++)
            {
                int k = i;
                // Yeniden bağlanmada aynı katmanlarla tekrar Start çağrılır: bir kez abone ol.
                var l = o.Layers[i];
                if (sparseHooked.Add(l)) l.ShownChanged += v => Gate("lp" + k, "ly" + k, v, l.Fps);
            }
            baseEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            capAttempt = 0;
            gpuBroken = false; engineFails = 0;
            restartDue = DateTime.MaxValue;
            planned = false; windowReadySince = DateTime.MaxValue; windowBlockedUntil = DateTime.MinValue;
            winStart = audioStatAt = DateTime.Now; winFrameStart = 0; totalFrames = 0;
            fatal = exitFired = false;
            lock (runs)
            {
                foreach (var r in runs.Values) r.Retry?.Dispose();
                runs.Clear();
                foreach (var t in o.Targets) runs[t.Id] = new TargetRun { Target = t };
            }
            pubs = new Publisher[0];
            foreach (var t in o.Targets) StartPublisher(t, false);
            StartCapture();
            watchdog?.Dispose(); statsTimer?.Dispose();
            watchdog = new Timer(_ => Watch(), null, 250, 250);
            statsTimer = new Timer(_ => Stats(), null, 5000, 5000);
        }

        readonly HashSet<Layer> sparseHooked = new HashSet<Layer>();

        void OnOverlayBlank(bool blank) => Gate("pp", "ov", !blank, PanelFps);

        /// <summary>Açarken önce okuma hızı, sonra karıştırma; kapatırken tersi.</summary>
        void Gate(string pts, string overlay, bool on, int fps)
        {
            if (gpuMode)
            {
                // Motor gizli katmanı hiç okumaz/çizmez; hız ayarı gerekmez.
                string line = (overlay == "ov" ? "panel " : "layer " + overlay.Substring(2) + " ") + (on ? 1 : 0) + "\n";
                lock (stdinLock)
                {
                    if (pendingCmds != null) { pendingCmds.Add(line); return; }
                    var e = engine;
                    try { if (e != null && !e.HasExited && !stopping) { e.StandardInput.Write(line); e.StandardInput.Flush(); } } catch { }
                }
                return;
            }
            if (on) { SendCommand($"setpts@{pts} expr {Clock(fps)}"); SendCommand($"overlay@{overlay} enable 1"); }
            else { SendCommand($"overlay@{overlay} enable 0"); SendCommand($"setpts@{pts} expr {Clock(HiddenFps)}"); }
        }

        /// <summary>
        /// Her 250 ms: zamanı gelen yeniden başlatmayı yapar; yakalama ölü ama başlatma planlanmamışsa planlar;
        /// yakalama çalışıyor ama 4 sn'dir yeni kare üretmiyorsa (donma) sonlandırır ki yeniden başlasın.
        /// </summary>
        void Watch()
        {
            if (stopping || exitFired) return;
            var now = DateTime.Now;
            // Bağlantısı 5 sn ayakta kalan platform canlı sayılır (ffmpeg reddedilen bağlantıda hemen kapanır).
            foreach (var p in pubs)
            {
                TargetRun r;
                lock (runs) runs.TryGetValue(p.Target.Id, out r);
                if (r != null && r.State == TargetState.Connecting && p.Alive && (now - p.StartedAt).TotalSeconds >= 5) SetState(r, TargetState.Live);
            }
            var cap = proc;
            if (cap == null || cap.HasExited)
            {
                // Eski yakalamanın temizliği (ses kaynaklarını ayırma) bitmeden yenisi başlamasın;
                // yoksa geç kalan temizlik yeni yakalamanın sesini koparır.
                if (cap != null && !capDone) return;
                if (restartDue == DateTime.MaxValue) { Diag.Event("BEKÇİ", "yakalama ölü ve yeniden başlatma planlanmamıştı, başlatılıyor"); restartDue = now; }
                if (now >= restartDue)
                {
                    restartDue = DateTime.MaxValue;
                    try { StartCapture(); }
                    catch (Exception ex) { Diag.Event("HATA", "yakalama başlatılamadı: " + ex.Message); restartDue = now.AddSeconds(1); }
                }
                return;
            }
            if (opts.WindowCapture && ++modeTick % 4 == 0 && !awaitingFirstFrame) CheckWindowMode(now);
            var since = awaitingFirstFrame ? capStartedAt : lastFrameAt;
            if ((now - since).TotalSeconds > (awaitingFirstFrame ? 8 : 4))
            {
                // Pencere yakalaması donduysa bir süre ekran yakalamayla devam et (aynı pencereye takılıp kalmasın).
                if (capWindow != IntPtr.Zero) windowBlockedUntil = now.AddSeconds(30);
                Diag.Event("BEKÇİ", awaitingFirstFrame ? "yakalama 8 sn'dir ilk kareyi vermedi, yeniden başlatılıyor"
                                                       : "yakalama 4 sn'dir yeni kare üretmiyor (donma), yeniden başlatılıyor");
                KillCapture();
            }
        }

        // ---------- Pencere yakalama ↔ ekran yakalama geçişi ----------

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);

        IntPtr capWindow;              // çalışan yakalamanın penceresi (Zero = ekran yakalama)
        volatile bool planned;         // bilerek kapatıldı (mod geçişi): kopuş sayılmaz, hemen yeniden başlar
        DateTime windowReadySince, windowBlockedUntil;
        int modeTick;

        /// <summary>Valorant'ın görünür, simge durumunda olmayan ana penceresi; yoksa Zero.</summary>
        static IntPtr GameWindow()
        {
            // Geliştirme testi: VALOLIVE_TESTSRC=1 iken gerçek oyun penceresi asla yakalanmaz; yalnızca
            // VALOLIVE_TESTWINDOW başlıklı (testin kendi açtığı) pencere oyun yerine geçer.
            if (Environment.GetEnvironmentVariable("VALOLIVE_TESTSRC") == "1")
            {
                string title = Environment.GetEnvironmentVariable("VALOLIVE_TESTWINDOW");
                if (string.IsNullOrEmpty(title)) return IntPtr.Zero;
                foreach (var p in Process.GetProcesses())
                    using (p)
                        if (p.MainWindowTitle == title && IsWindowVisible(p.MainWindowHandle) && !IsIconic(p.MainWindowHandle)) return p.MainWindowHandle;
                return IntPtr.Zero;
            }
            foreach (var p in Process.GetProcessesByName(CaptureGuard.ValorantExe))
                using (p)
                {
                    var h = p.MainWindowHandle;
                    if (h != IntPtr.Zero && IsWindowVisible(h) && !IsIconic(h)) return h;
                }
            return IntPtr.Zero;
        }

        /// <summary>
        /// Saniyede bir: pencere yakalanıyorken oyun simge durumuna küçüldüyse/kapandıysa (pencere yakalaması o an kare
        /// vermez) ekran yakalamaya geçilir; ekran yakalanıyorken oyun penceresi 2 sn boyunca hazır kalırsa geri dönülür.
        /// </summary>
        void CheckWindowMode(DateTime now)
        {
            var hw = GameWindow();
            if (capWindow != IntPtr.Zero)
            {
                if (hw != capWindow) Switch("Valorant penceresi küçüldü/kapandı, ekran yakalamaya geçiliyor", IntPtr.Zero);
                return;
            }
            if (hw == IntPtr.Zero || now < windowBlockedUntil) { windowReadySince = DateTime.MaxValue; return; }
            if (windowReadySince == DateTime.MaxValue) windowReadySince = now;
            else if ((now - windowReadySince).TotalSeconds >= 2) Switch("Valorant penceresi hazır, pencere yakalamaya geçiliyor", hw);
        }

        volatile bool sourcePending;   // motora kaynak değişimi gönderildi, yanıt bekleniyor

        void Switch(string why, IntPtr target)
        {
            windowReadySince = DateTime.MaxValue;
            // Motor kaynağı çalışırken değiştirir: yakalama, ses ve Kick'e giden akış hiç kesilmez.
            var e = gpuMode ? engine : null;
            bool testScreen = target == IntPtr.Zero && Environment.GetEnvironmentVariable("VALOLIVE_TESTSRC") == "1"; // testte ekran yakalanmaz
            if (e != null && !e.HasExited && !sourcePending && !testScreen)
            {
                Diag.Event("YAKALAMA", why + " (kesintisiz)");
                lock (stdinLock)
                {
                    try
                    {
                        sourcePending = true;
                        e.StandardInput.Write(target != IntPtr.Zero ? $"source window {target.ToInt64()}\n" : "source monitor 0\n");
                        e.StandardInput.Flush();
                        capWindow = target;
                        GameFrames.SetWindow(target);
                        return;
                    }
                    catch { sourcePending = false; }
                }
            }
            Diag.Event("YAKALAMA", why);
            planned = true;
            KillCapture();
        }

        void Stats()
        {
            if (stopping) return;
            var now = DateTime.Now;
            double secs = Math.Max(0.001, (now - winStart).TotalSeconds);
            long frames = totalFrames - winFrameStart;
            string p(string k) => progress.TryGetValue(k, out var v) ? v : "?";
            // Platform başına: gönderilen hız ve en uzun yazma beklemesi (uzarsa o platformun ağı yetişmiyor).
            var waits = new Dictionary<Target, double>();
            var sent = string.Join(" · ", pubs.Select(x =>
            {
                long b = Interlocked.Exchange(ref x.Bytes, 0); double w = x.MaxWriteMs; x.MaxWriteMs = 0;
                waits[x.Target] = w;
                return $"{x.Target.Name} {b * 8 / secs / 1000:0} kbps (bekleme en çok {w:0} ms)";
            }));
            bool noCapture = proc == null || proc.HasExited;
            Diag.Event("İSTAT", $"fps {frames / secs:0.0} · {(sent == "" ? "platform bağlantısı yok" : sent)}" +
                                $" · düşen kare {p("drop_frames")} · çoğaltılan {p("dup_frames")} · yakalama #{capAttempt}" +
                                (noCapture ? " · YAKALAMA YOK" : ""));
            winStart = now; winFrameStart = totalFrames;
            // Dakikada bir ses kaynaklarının durumu: kırpılma, cihaz kopuşu ve hizalama düzeltmeleri (cızırtı/çıt teşhisi).
            if ((now - audioStatAt).TotalSeconds >= 60)
            {
                audioStatAt = now;
                Diag.Event("SES", $"oyun: {gameSrc?.TakeStats()}" + (micSrc != null ? $" | mikrofon: {micSrc.TakeStats()}" : ""));
            }
            Health?.Invoke(frames / secs, waits, noCapture);
        }

        void ScheduleRestart()
        {
            // Kademeli bekleme: mod geçişi sürerken hemen denemek boşa düşebilir.
            int n = recent.Count;
            int ms = n <= 1 ? 100 : n <= 3 ? 300 : n <= 6 ? 600 : 1000;
            restartDue = DateTime.Now.AddMilliseconds(ms);
        }

        static string Tag(Target t) => t.Id == "file" ? "KAYIT" : t.Name.ToUpperInvariant();

        /// <summary>joinMidStream: yakalama zaten akıyor (yeniden bağlanma); yayıncı bir sonraki anahtar kareden katılır.</summary>
        void StartPublisher(Target t, bool joinMidStream)
        {
            var args = PublisherArgs(opts, t.Url);
            Log($"PUB {t.Name} {args}");
            var p = new Publisher(t) { Line = line => OnLine(line, true, t) };
            p.Exited += OnPublisherExit;
            TargetRun r;
            lock (runs) runs.TryGetValue(t.Id, out r);
            p.Start(FindFfmpeg(), args, joinMidStream);
            lock (runs) pubs = pubs.Where(x => x.Target.Id != t.Id).Concat(new[] { p }).ToArray();
            if (r != null) SetState(r, TargetState.Connecting);
            Diag.Event(Tag(t), opts.ToFile ? "dosyaya kayıt başladı" : $"yayıncı süreç başladı ({t.Name} bağlantısı açılıyor{(joinMidStream ? ", akışa bir sonraki anahtar karede katılır" : "")})");
        }

        void SetState(TargetRun r, TargetState s)
        {
            if (r.State == s && s != TargetState.Retrying) return;
            r.State = s;
            TargetChanged?.Invoke(r.Target, s, r.Attempts);
        }

        /// <summary>
        /// Bir platformun yayıncısı kapandı. Durdururken: hepsi kapanınca yayın biter. Beklenmedik: yalnızca o platform
        /// yeniden bağlanır (yakalama ve öteki platformlar sürer). Hiç ayağa kalkamadan 3 kez kopan platform reddedilmiş
        /// sayılır (URL/anahtar yanlış), 20 denemede toparlanamayan bırakılır. Hiçbir platform kalmazsa yayın biter.
        /// </summary>
        void OnPublisherExit(Publisher p, int code, string err)
        {
            var t = p.Target;
            Log($"PUB EXIT {t.Name} code={code} stopping={stopping} {err}");
            TargetRun r;
            lock (runs) runs.TryGetValue(t.Id, out r);
            if (stopping)
            {
                if (r != null) SetState(r, TargetState.Stopped);
                if (!pubs.Any(x => x.Alive)) FireExited(false, err);
                return;
            }
            Diag.PublisherDropped();
            Diag.Event(Tag(t), $"bağlantı koptu (kod {code}) · {Diag.Explain(err + " " + code)} · son satırlar: {err}");
            if (r == null || opts.ToFile || fatal)
            {
                if (r != null) SetState(r, TargetState.Failed);
                GiveUpIfNone(err);
                return;
            }
            var now = DateTime.Now;
            double up = (now - p.StartedAt).TotalSeconds;
            if (up >= 10) { r.EverUp = true; r.Attempts = 0; }
            r.Attempts++;
            // Ayağa kalktıktan sonra 60 sn dolmadan tekrar tekrar kopuyorsa hat (ya da platform) bu bitrate'i
            // kaldırmıyor: her iki hızlı kopmada 1000 kbps düş (en az 3500). Görüntü tek kodlandığı için hepsine uygulanır.
            r.QuickDrops = up >= 10 && up < 60 ? r.QuickDrops + 1 : up >= 60 ? 0 : r.QuickDrops;
            if (r.QuickDrops >= 2 && opts.BitrateK > 3500)
            {
                r.QuickDrops = 0;
                opts.BitrateK = Math.Max(3500, opts.BitrateK - 1000);
                Diag.Event(Tag(t), $"bağlantı sürekli kopuyor → bitrate {opts.BitrateK} kbps'ye düşürüldü (yakalama yeni değerle yeniden başlıyor)");
                planned = true;
                KillCapture();
            }
            if (!r.EverUp && r.Attempts >= 3)
            {
                Diag.Event(Tag(t), "bağlantı reddedildi (hiç açılamadı): URL / anahtar yanlış olabilir, bu yayında bırakıldı");
                SetState(r, TargetState.Rejected);
                GiveUpIfNone(err);
                return;
            }
            if (r.Attempts > 20)
            {
                Diag.Event(Tag(t), "20 denemede toparlanamadı, bu yayında bırakıldı");
                SetState(r, TargetState.Failed);
                GiveUpIfNone(err);
                return;
            }
            SetState(r, TargetState.Retrying);
            int delay = Math.Min(10000, 2000 * r.Attempts);
            Diag.Event(Tag(t), $"yeniden bağlanılıyor (deneme {r.Attempts}, {delay / 1000} sn sonra)");
            r.Retry?.Dispose();
            r.Retry = new Timer(_ =>
            {
                if (stopping || fatal || exitFired) return;
                try { StartPublisher(t, true); }
                catch (Exception ex) { Diag.Event(Tag(t), "yayıncı başlatılamadı: " + ex.Message); OnPublisherExit(p, -1, ex.Message); }
            }, null, delay, Timeout.Infinite);
        }

        /// <summary>Bağlı ya da bağlanmaya çalışan platform kalmadıysa yayın biter.</summary>
        void GiveUpIfNone(string err)
        {
            if (Active().Any()) return;
            // Yayın burada biter (Stop çağrılmaz): bekçi ve istatistik dursun, yakalama yeniden başlatılmasın.
            stopping = true;
            watchdog?.Dispose(); statsTimer?.Dispose();
            KillCapture();
            FireExited(true, err);
        }

        void FireExited(bool unexpected, string err)
        {
            lock (runs) { if (exitFired) return; exitFired = true; }
            Exited?.Invoke(unexpected, err);
        }

        /// <summary>Platformların son durumu (yayın bitince arayüz neden bittiğini buradan yazar).</summary>
        public List<KeyValuePair<Target, TargetState>> TargetStates()
        {
            lock (runs) return runs.Values.Select(r => new KeyValuePair<Target, TargetState>(r.Target, r.State)).ToList();
        }

        void StartCapture()
        {
            var window = opts.WindowCapture && DateTime.Now >= windowBlockedUntil ? GameWindow() : IntPtr.Zero;
            capWindow = window;
            sourcePending = false;
            // Test görüntüsü (VALOLIVE_TESTSRC) yalnızca ffmpeg yolunda var: motor ekranı yakalamasın.
            bool wantGpu = opts.Gpu && !gpuBroken && FindEngine() != null &&
                           !(Environment.GetEnvironmentVariable("VALOLIVE_TESTSRC") == "1" && window == IntPtr.Zero);
            lock (stdinLock) { pendingCmds = new List<string>(); gpuMode = wantGpu; }
            lock (lastErrors) lastErrors.Clear(); // kopuş nedeni bu yakalamanın kendi satırlarından okunsun
            long epochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string micPath = null;
            var gamePipe = OpenPipe(gameSrc, epochMs, out var gamePath);
            var micPipe = micSrc != null ? OpenPipe(micSrc, epochMs, out micPath) : null;
            OverlayFeed ovFeed = null;
            List<OverlayFeed> layerFeeds = null;
            SharedFeed shared = null;
            Process eng = null;
            string grab = null;
            List<string> initial = null;
            string args = null;
            if (wantGpu)
            {
                initial = InitialGpuCommands(opts.Masks?.Invoke());   // sonraki değişiklikler bekleyen komutlara (çevrilmiş) düşer
                shared = new SharedFeed();
                eng = StartEngine(epochMs, window, shared, out var videoPipe, out grab);
                if (eng != null) args = BuildGpuArgs(opts, gameSrc, micSrc, epochMs, baseEpochMs, gamePath, micPath, videoPipe);
                else
                {
                    shared.Dispose(); shared = null; initial = null;
                    gpuBroken = true;
                    lock (stdinLock) { gpuMode = false; pendingCmds.Clear(); } // ffmpeg grafiği zaten o anki durumla kurulacak
                }
            }
            if (eng == null)
            {
                ovFeed = new OverlayFeed(() => opts.Overlay.Current);
                layerFeeds = opts.Layers.Select(l => new OverlayFeed(() => l.Current)).ToList();
                args = BuildArgs(opts, gameSrc, micSrc, epochMs, baseEpochMs, gamePath, micPath, ovFeed.Path, layerFeeds.Select(f => f.Path).ToList(), window);
            }
            lock (stdinLock) engine = eng;
            Log("START " + args);

            var psi = new ProcessStartInfo(FindFfmpeg(), args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
            };
            var cap = new Process { StartInfo = psi, EnableRaisingEvents = true };
            cap.ErrorDataReceived += (s, e) => OnLine(e.Data, false);
            var pump = new Thread(() => Pump(cap)) { IsBackground = true, Name = "ts-pump" };
            long attempt = ++capAttempt;
            cap.Exited += (s, e) =>
            {
                pump.Join(3000);
                gameSrc.Detach();
                micSrc?.Detach();
                if (eng != null)
                {
                    // Motor ffmpeg'den önce öldüyse kopuşun nedeni odur; üst üste düşerse bu yayında ffmpeg yoluna dönülür.
                    bool engineDied = eng.HasExited;
                    if (engineDied && !stopping && ++engineFails >= 3) { gpuBroken = true; Diag.Event("MOTOR", "ekran kartı motoru 3 kez düştü, bu yayında ffmpeg yoluna dönülüyor"); }
                    try { if (!eng.HasExited) eng.Kill(); } catch { }
                    GameFrames.Detach(grab);
                }
                try { gamePipe.Dispose(); micPipe?.Dispose(); ovFeed?.Dispose(); layerFeeds?.ForEach(f => f.Dispose()); shared?.Dispose(); } catch { }
                int code = 0;
                try { code = cap.ExitCode; } catch { }
                string err = Errors();
                Log($"EXIT code={code} stopping={stopping} {err}");
                if (stopping || exitFired) { capDone = true; return; }
                if (planned)
                {
                    // Mod geçişi: kopuş sayılmaz, toparlanma bildirilmez, hemen yeniden başlar.
                    planned = false;
                    capDiedAt = DateTime.Now;
                    restartDue = DateTime.Now;
                    capDone = true;
                    return;
                }

                // Beklenmedik kopuş: yayın açık kalır, yakalama kısa bir aradan sonra yeniden başlar (bekçi yapar).
                // Bir dakikada 30'dan fazla kopuş varsa yakalama gerçekten bozuktur; yayın kapatılır.
                capDiedAt = DateTime.Now;
                Diag.Event("YAKALAMA", $"#{attempt} koptu ({(capDiedAt - capStartedAt).TotalSeconds:0.0} sn çalıştı, kod {code}) · " +
                                       $"{(Diag.Explain(err) is string x && x != "" ? x : "neden: " + err)}");
                recent.Enqueue(capDiedAt);
                while (recent.Count > 0 && (capDiedAt - recent.Peek()).TotalSeconds > 60) recent.Dequeue();
                if (recent.Count > 30) { Diag.Event("HATA", "yakalama 1 dakikada 30'dan fazla koptu, yayın kapatılıyor"); fatal = true; capDone = true; KillPublishers(); return; }
                Recovering?.Invoke();
                ScheduleRestart();
                capDone = true; // bekçi artık yenisini başlatabilir
            };
            capStartedAt = DateTime.Now;
            capDone = false;
            awaitingFirstFrame = true;
            lastFrame = 0;
            cap.Start();
            proc = cap;
            Diag.Event("YAKALAMA", $"#{attempt} başladı · {(window != IntPtr.Zero ? "Valorant penceresi" : "ekran")}{(eng != null ? " · ekran kartı motoru" : "")}");
            lock (stdinLock)
            {
                var target = eng ?? cap;
                try { foreach (var l in (initial ?? new List<string>()).Concat(pendingCmds)) target.StandardInput.Write(l); target.StandardInput.Flush(); } catch { }
                pendingCmds = null;
            }
            // Normal öncelik (OBS'in varsayılanı): AboveNormal'da ffmpeg oyunun iş parçacıklarını kesip FPS düşürüyordu.
            try { cap.PriorityClass = ProcessPriorityClass.Normal; } catch { }
            cap.BeginErrorReadLine();
            pumpThread = pump;
            pump.Start();
        }

        /// <summary>Yakalamanın MPEG-TS çıktısını yayıncıya aktarır; yalnızca tam 188 baytlık paketler geçer
        /// (kopan yakalamanın yarım kalan son paketi yayına bozuk veri olarak gitmesin).</summary>
        void Pump(Process cap)
        {
            var buf = new byte[188 * 350];
            int fill = 0;
            try
            {
                var src = cap.StandardOutput.BaseStream;
                int n;
                while ((n = src.Read(buf, fill, buf.Length - fill)) > 0)
                {
                    fill += n;
                    int whole = fill - fill % 188;
                    if (whole == 0) continue;
                    // Her platformun kendi kuyruğu var: yazma burada beklemez (bekleme istatistiği Publisher'da).
                    foreach (var p in pubs) p.Feed(buf, whole);
                    Buffer.BlockCopy(buf, whole, buf, 0, fill - whole);
                    fill -= whole;
                }
            }
            catch { } // yakalama kapandı: onun Exited olayı ilgilenir
        }

        void KillCapture()
        {
            try { var p = proc; if (p != null && !p.HasExited) p.Kill(); } catch { }
            try { var e = engine; if (e != null && !e.HasExited) e.Kill(); } catch { }
        }
        void KillPublishers() { foreach (var p in pubs) p.Kill(); }

        void OnLine(string line, bool fromPublisher, Target target = null)
        {
            if (string.IsNullOrEmpty(line)) return;
            // Canlı komut yankısı ("Command reply for stream 1: ret:-40" = ses grafiğinde o filtre yok,
            // zararsız) ilerleme satırıyla herhangi bir noktadan bölünüp iç içe gelebiliyor:
            // "Coframe=22" + "mmand reply…" ya da "Command frame=22" + "or stream 1: ret:-40". Ayıkla.
            if (line.Contains("reply for stream") || Regex.IsMatch(line, @"stream \d+: ret:-?\d+")) return;
            if (!fromPublisher)
            {
                var m = Regex.Match(line, @"^[A-Za-z ]{1,24}(frame=\d+)$");
                if (m.Success) line = m.Groups[1].Value;
            }
            int eq = line.IndexOf('=');
            if (!fromPublisher && eq > 0 && Regex.IsMatch(line.Substring(0, eq), "^[a-z0-9_]+$"))
            {
                string k = line.Substring(0, eq), v = line.Substring(eq + 1).Trim();
                progress[k] = v;
                if (k == "frame" && long.TryParse(v, out var f) && f > lastFrame)
                {
                    totalFrames += f - lastFrame; // yakalama yeniden başlayınca ffmpeg sayacı sıfırlanır; toplam sürer
                    lastFrame = f;
                    lastFrameAt = DateTime.Now;
                    if (awaitingFirstFrame)
                    {
                        awaitingFirstFrame = false;
                        if (capDiedAt != DateTime.MinValue) Diag.CaptureRestarted((lastFrameAt - capDiedAt).TotalMilliseconds);
                        else Diag.Event("YAKALAMA", $"ilk kare {(lastFrameAt - capStartedAt).TotalMilliseconds:0} ms'de geldi");
                    }
                }
                if (k == "progress") Progress?.Invoke(new Dictionary<string, string>(progress));
                return;
            }
            // Canlı komut yankıları hata değildir; hata kuyruğunu ve günlüğü kirletmesin.
            if (line.StartsWith("Enter command") || line.Contains("Command reply")) return;
            // Yayıncı satırlarının kendi hata kuyruğu var (Publisher): kopuş nedeni platforma göre ayrı okunur.
            if (!fromPublisher) lock (lastErrors) { lastErrors.Enqueue(Mask(line.Trim())); while (lastErrors.Count > 4) lastErrors.Dequeue(); }
            Log((fromPublisher ? $"[pub {target?.Name}] " : "") + line);
            Diag.Event(fromPublisher ? (target != null ? Tag(target) : "KICK") + "-ffmpeg" : "YAKALAMA-ffmpeg", line.Trim());
        }

        /// <summary>
        /// Düzgün kapatma: yakalamaya 'q' (kodlayıcıyı boşaltır), sonra yayıncıların girişini kapatır
        /// (dosyayı/yayını sonlandırır). Süreçler 5 sn içinde kapanmazsa sonlandırılır.
        /// </summary>
        public void Stop()
        {
            if (opts == null || exitFired) return;
            lock (runs) foreach (var r in runs.Values) { r.Retry?.Dispose(); r.Retry = null; }
            var cap = proc;
            watchdog?.Dispose(); statsTimer?.Dispose();
            watchdog = statsTimer = null;
            // Motor önce kapanır (kodlayıcıyı boşaltıp son kareleri yazar); ses girişleri sürdüğü için ffmpeg'e yine 'q' gerekir.
            var eng = gpuMode ? engine : null;
            lock (stdinLock)
            {
                stopping = true;
                try { if (eng != null && !eng.HasExited) { eng.StandardInput.Write("quit\n"); eng.StandardInput.Flush(); } } catch { }
            }
            if (eng != null && !eng.WaitForExit(3000)) try { eng.Kill(); } catch { }
            lock (stdinLock)
            {
                try { if (cap != null && !cap.HasExited) { cap.StandardInput.Write('q'); cap.StandardInput.Flush(); } } catch { }
            }
            if (cap != null && !cap.WaitForExit(5000)) KillCapture();
            pumpThread?.Join(3000); // son paketler yayıncılara geçsin
            var all = pubs;
            foreach (var p in all) p.Finish();
            foreach (var p in all) if (!p.WaitForExit(5000)) p.Kill();
            // Hiçbir yayıncı ayakta değildi (hepsi yeniden bağlanmayı bekliyordu): bitişi burada bildir.
            if (!all.Any(p => p.Alive)) FireExited(false, "");
        }

        string Errors() { lock (lastErrors) return string.Join(" | ", lastErrors); }

        static string Mask(string s) => MaskSecrets(s);
        public static string MaskSecrets(string s) => Regex.Replace(s, @"(rtmps?://[^/\s""]+/(?:app/)?)[^\s""]+", "$1***");

        static readonly object logLock = new object();
        public static void Log(string s)
        {
            // ffmpeg hata satırları da tam URL'yi basar: stream key hiçbir satırda log'a düşmesin.
            s = Mask(s);
            try
            {
                lock (logLock)
                {
                    // 2 MB'ı geçince bir öncekinin yerine taşınır: en fazla iki dosya, disk dolmaz.
                    var fi = new FileInfo(LogPath);
                    if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                    {
                        var old = Path.Combine(Settings.Dir, "ffmpeg.old.log");
                        File.Delete(old);
                        File.Move(LogPath, old);
                    }
                    File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {s}\r\n");
                }
            }
            catch { }
        }
    }
}
