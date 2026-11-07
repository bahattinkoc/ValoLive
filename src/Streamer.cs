using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ValoLive
{
    sealed class StreamOptions
    {
        public string Output;          // rtmps://... ya da .mp4 dosya yolu
        public bool ToFile;
        public int DurationSec;        // sadece test kaydı için; uygulama bu sürede durdurur
        public int Width, Height, BitrateK;
        public int ScreenWidth, ScreenHeight;
        public bool Denoise;
        public bool WindowCapture;     // Valorant penceresi açıkken yalnızca onu yakala (Windows Graphics Capture); değilse ekran
        public OverlayRenderer Overlay;
        public Func<string> Masks;     // CaptureGuard'ın örtü filtreleri (ekran karesine, panelden önce); her yakalama başlangıcında sorulur
        public List<Layer> Layers = new List<Layer>(); // panelin üstüne bindirilen küçük animasyonlu katmanlar (radar, chat)
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

        public bool Running => pub != null && !pub.HasExited;
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

            const string fmt = "aresample=48000,aformat=channel_layouts=stereo";
            // VALOLIVE_TESTSRC=1: ekran yerine yapay test görüntüsü (ekranı kaydetmeden geliştirme testi için).
            // Pencere yakalama (gfxcapture = Windows Graphics Capture): yalnızca oyunun kendi karesi kopyalanır; masaüstü
            // çoğaltması (ddagrab) gibi tüm ekranı her karede birleştirmeye zorlamaz. Kare yalnızca pencere çizildikçe gelir
            // (oyun sürekli çizer); boyut ekran boyutuna sabitlenir ki panel ve örtüler aynı yere otursun.
            string screen = window != IntPtr.Zero
                ? $"gfxcapture=hwnd={window.ToInt64()}:capture_cursor=0:max_framerate=60:width={o.ScreenWidth}:height={o.ScreenHeight}:resize_mode=scale_aspect,setpts=RTCTIME/1000000/TB,hwdownload,format=bgra"
                : Environment.GetEnvironmentVariable("VALOLIVE_TESTSRC") == "1"
                ? $"testsrc2=s={o.ScreenWidth}x{o.ScreenHeight}:r=60,realtime,setpts=RTCTIME/1000000/TB,format=bgra"
                : "ddagrab=output_idx=0:framerate=60:draw_mouse=0,setpts=RTCTIME/1000000/TB,hwdownload,format=bgra";
            var fc = new StringBuilder(screen + (o.Masks?.Invoke() ?? "") + "[m];");
            // Panel epoch'tan 15 FPS ilerler: ffmpeg panel karesini ancak video o ana gelince ister
            // (saniyede ~15 PNG çözümü). probesize=32: açılışta saniyelerce kareyi önden okuyup
            // tamponlamasın (yoksa panel ilk ~8 sn eski/boş görünür).
            // Filtre sözdiziminde '\' iki kat kaçış ister: \ -> \\\\
            string Movie(string pipe, int fps, string label) =>
                $"movie={pipe.Replace(@"\", @"\\\\")}:f=image2pipe:format_opts=probesize=32,setpts=N/({fps}*TB)+{epoch}/TB[{label}];";
            fc.Append(Movie(ovPipe, 15, "ov"));
            for (int i = 0; i < layerPipes.Count; i++) fc.Append(Movie(layerPipes[i], o.Layers[i].Fps, "ly" + i));
            // Panel boşken karıştırma kapalı başlar; sonrasını canlı komutlar açar/kapatır (BlankChanged).
            fc.Append($"[m][ov]overlay@ov=eof_action=repeat:format=auto:enable={(o.Overlay.Blank ? 0 : 1)}");
            for (int i = 0; i < layerPipes.Count; i++)
                fc.Append($"[o{i}];[o{i}][ly{i}]overlay=x={o.Layers[i].X}:y={o.Layers[i].Y}:eof_action=repeat:format=auto");
            // RGB→YUV dönüşümü CPU'da değil NVENC'te (CPU'da 1080p60 için çekirdeğin ~%40'ı gidiyordu).
            // NVENC BT.601 sınırlı aralık matrisiyle dönüştürür; akış da öyle etiketlenir (renkler birebir kalır).
            // Etiket şart: yoksa akış "GBR, full range" görünür ve Kick (IVS) bunu yanlış yapılandırılmış sayar.
            if (o.Height != o.ScreenHeight) fc.Append($",scale={o.Width}:{o.Height}:flags=bicubic");
            fc.Append(",format=bgra,fps=60,setparams=range=tv:colorspace=smpte170m:color_primaries=bt709:color_trc=bt709[v]");
            // Ses ayrı bir filtre grafiğinde: video grafiği ses girişlerini beklemesin.
            var af = new StringBuilder($"[0:a]{fmt}");
            if (mic != null)
            {
                af.Append($"[g];[1:a]{fmt},highpass=f=90");
                if (o.Denoise) af.Append(",afftdn=nf=-25");
                af.Append("[mic];[g][mic]amix=inputs=2:duration=first:normalize=0");
            }
            // Ses yolu zaman damgasını sıfırdan başlatıyor; sondaki asetpts onu örnek sayısından
            // mutlak saate geri çevirir (örnek 0 = epoch, AudioSource bunu garanti eder).
            af.Append($",asetpts=N/SR/TB+{epoch}/TB[a]");
            a.Append($"-filter_complex \"{fc}\" -filter_complex \"{af}\" -map [v] -map [a] ");

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
            a.Append($"-c:a aac -b:a 160k -ar 48000 -output_ts_offset -{Inv(baseEpochMs / 1000.0)} ");
            // Yayıncı sürece MPEG-TS: her yeni oturumun başı "kesinti" işaretli, ek yerinde bozuk paket uyarısı olmaz.
            a.Append("-f mpegts -mpegts_flags +initial_discontinuity pipe:1");
            return a.ToString();
        }

        /// <summary>Yayıncı süreç: yakalamadan gelen MPEG-TS'i yeniden kodlamadan Kick'e (ya da dosyaya) iletir.</summary>
        static string PublisherArgs(StreamOptions o)
        {
            // -readrate KULLANMA: gerçek yayında (6000 kbps, gerçek ses kaynaklarıyla) akış Kick'te ~5 fps
            // donuk göründü; test görüntüsüyle yeniden üretilemedi. Açılıştaki ~3 sn'lik birikmiş gönderim
            // 6000'de Kick'in sınırı içinde kalıyor (yalnızca kaldırılan 7500 seçeneğinde bağlantı kopuyordu).
            var a = new StringBuilder("-hide_banner -nostats -loglevel error -probesize 2000000 -analyzeduration 2000000 -f mpegts -i pipe:0 -map 0 -c copy ");
            if (o.ToFile) a.Append($"-movflags +faststart -y \"{o.Output}\"");
            else a.Append($"-flvflags no_duration_filesize -f flv \"{o.Output}\"");
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
            string line = "c" + cmd.Substring(0, sp) + " -1" + cmd.Substring(sp) + "\n";
            lock (stdinLock)
            {
                if (pendingCmds != null) { pendingCmds.Add(line); return; }
                var p = proc;
                if (p == null || p.HasExited || stopping) return;
                try { p.StandardInput.Write(line); p.StandardInput.Flush(); } catch { }
            }
        }

        // İki süreç: "yayıncı" (pub) Kick bağlantısını yayın boyunca açık tutar; "yakalama" (proc) ekranı,
        // sesi ve paneli kodlayıp MPEG-TS olarak ona akıtır. Ekran yakalama kopunca (tam ekran oyunu alta alma,
        // UAC: DXGI ACCESS_LOST) yalnızca yakalama yeniden başlar; Kick bağlantısı hiç kapanmaz.
        Process pub;
        Stream pubIn;
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
        long winBytes; double winMaxWriteMs; long winFrameStart; DateTime winStart;

        /// <summary>Yakalama kendini toparlarken (yayın açık kalır) bildirilir.</summary>
        public event Action Recovering;

        public void Start(StreamOptions o, AudioSource game, AudioSource mic)
        {
            stopping = false;
            lock (lastErrors) lastErrors.Clear();
            recent.Clear();
            if (opts?.Overlay != null) opts.Overlay.BlankChanged -= OnOverlayBlank;
            opts = o; gameSrc = game; micSrc = mic;
            o.Overlay.BlankChanged += OnOverlayBlank;
            baseEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            capAttempt = 0;
            restartDue = DateTime.MaxValue;
            planned = false; windowReadySince = DateTime.MaxValue; windowBlockedUntil = DateTime.MinValue;
            winStart = DateTime.Now; winBytes = 0; winMaxWriteMs = 0; winFrameStart = 0; totalFrames = 0;
            StartPublisher();
            StartCapture();
            watchdog?.Dispose(); statsTimer?.Dispose();
            watchdog = new Timer(_ => Watch(), null, 250, 250);
            statsTimer = new Timer(_ => Stats(), null, 5000, 5000);
        }

        void OnOverlayBlank(bool blank) => SendCommand("overlay@ov enable " + (blank ? 0 : 1));

        /// <summary>
        /// Her 250 ms: zamanı gelen yeniden başlatmayı yapar; yakalama ölü ama başlatma planlanmamışsa planlar;
        /// yakalama çalışıyor ama 4 sn'dir yeni kare üretmiyorsa (donma) sonlandırır ki yeniden başlasın.
        /// </summary>
        void Watch()
        {
            if (stopping || pub == null || pub.HasExited) return;
            var cap = proc;
            var now = DateTime.Now;
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
                if (hw != capWindow) Switch("Valorant penceresi küçüldü/kapandı, ekran yakalamaya geçiliyor");
                return;
            }
            if (hw == IntPtr.Zero || now < windowBlockedUntil) { windowReadySince = DateTime.MaxValue; return; }
            if (windowReadySince == DateTime.MaxValue) windowReadySince = now;
            else if ((now - windowReadySince).TotalSeconds >= 2) Switch("Valorant penceresi hazır, pencere yakalamaya geçiliyor");
        }

        void Switch(string why)
        {
            Diag.Event("YAKALAMA", why);
            windowReadySince = DateTime.MaxValue;
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
            Diag.Event("İSTAT", $"fps {frames / secs:0.0} · Kick'e {winBytes * 8 / secs / 1000:0} kbps · en uzun gönderim bekleme {winMaxWriteMs:0} ms" +
                                $" · düşen kare {p("drop_frames")} · çoğaltılan {p("dup_frames")} · yakalama #{capAttempt}" +
                                (proc == null || proc.HasExited ? " · YAKALAMA YOK" : ""));
            winStart = now; winBytes = 0; winMaxWriteMs = 0; winFrameStart = totalFrames;
        }

        void ScheduleRestart()
        {
            // Kademeli bekleme: mod geçişi sürerken hemen denemek boşa düşebilir.
            int n = recent.Count;
            int ms = n <= 1 ? 100 : n <= 3 ? 300 : n <= 6 ? 600 : 1000;
            restartDue = DateTime.Now.AddMilliseconds(ms);
        }

        void StartPublisher()
        {
            var args = PublisherArgs(opts);
            Log("PUB " + args);
            var psi = new ProcessStartInfo(FindFfmpeg(), args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
            };
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.ErrorDataReceived += (s, e) => OnLine(e.Data, true);
            p.Exited += (s, e) =>
            {
                int code = 0;
                try { code = p.ExitCode; } catch { }
                string err = Errors();
                Log($"PUB EXIT code={code} stopping={stopping} {err}");
                if (!stopping)
                {
                    Diag.PublisherDropped();
                    Diag.Event("KICK", $"bağlantı koptu (kod {code}) · {Diag.Explain(err + " " + code)} · son satırlar: {err}");
                }
                if (!stopping) KillCapture(); // bağlantı koptu: yakalamanın sürmesinin anlamı yok
                Exited?.Invoke(!stopping, err); // durdurmadan kapandıysa bağlantı kopmuştur
            };
            p.Start();
            p.BeginErrorReadLine();
            pub = p;
            pubIn = p.StandardInput.BaseStream;
            Diag.Event("KICK", opts.ToFile ? "dosyaya kayıt başladı" : "yayıncı süreç başladı (Kick bağlantısı açılıyor)");
        }

        void StartCapture()
        {
            lock (stdinLock) pendingCmds = new List<string>();
            lock (lastErrors) lastErrors.Clear(); // kopuş nedeni bu yakalamanın kendi satırlarından okunsun
            long epochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string micPath = null;
            var gamePipe = OpenPipe(gameSrc, epochMs, out var gamePath);
            var micPipe = micSrc != null ? OpenPipe(micSrc, epochMs, out micPath) : null;
            var ovFeed = new OverlayFeed(() => opts.Overlay.Current);
            var layerFeeds = opts.Layers.Select(l => new OverlayFeed(() => l.Current)).ToList();
            var window = opts.WindowCapture && DateTime.Now >= windowBlockedUntil ? GameWindow() : IntPtr.Zero;
            capWindow = window;
            var args = BuildArgs(opts, gameSrc, micSrc, epochMs, baseEpochMs, gamePath, micPath, ovFeed.Path, layerFeeds.Select(f => f.Path).ToList(), window);
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
                try { gamePipe.Dispose(); micPipe?.Dispose(); ovFeed.Dispose(); layerFeeds.ForEach(f => f.Dispose()); } catch { }
                int code = 0;
                try { code = cap.ExitCode; } catch { }
                string err = Errors();
                Log($"EXIT code={code} stopping={stopping} {err}");
                if (stopping || pub == null || pub.HasExited) { capDone = true; return; }
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
                if (recent.Count > 30) { Diag.Event("HATA", "yakalama 1 dakikada 30'dan fazla koptu, yayın kapatılıyor"); capDone = true; KillPublisher(); return; }
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
            Diag.Event("YAKALAMA", $"#{attempt} başladı · {(window != IntPtr.Zero ? "Valorant penceresi" : "ekran")}");
            lock (stdinLock)
            {
                try { foreach (var l in pendingCmds) cap.StandardInput.Write(l); cap.StandardInput.Flush(); } catch { }
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
                    var t0 = Stopwatch.GetTimestamp();
                    pubIn.Write(buf, 0, whole);
                    pubIn.Flush();
                    // Yazma beklemesi uzarsa yayıncı (dolayısıyla ağ / Kick) veriyi yetiştiremiyor demektir.
                    double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    winBytes += whole;
                    if (ms > winMaxWriteMs) winMaxWriteMs = ms;
                    Buffer.BlockCopy(buf, whole, buf, 0, fill - whole);
                    fill -= whole;
                }
            }
            catch { } // yayıncı kapandıysa onun Exited olayı ilgilenir
        }

        void KillCapture() { try { var p = proc; if (p != null && !p.HasExited) p.Kill(); } catch { } }
        void KillPublisher() { try { var p = pub; if (p != null && !p.HasExited) p.Kill(); } catch { } }

        void OnLine(string line, bool fromPublisher)
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
            lock (lastErrors) { lastErrors.Enqueue(Mask(line.Trim())); while (lastErrors.Count > 4) lastErrors.Dequeue(); }
            Log((fromPublisher ? "[pub] " : "") + line);
            Diag.Event(fromPublisher ? "KICK-ffmpeg" : "YAKALAMA-ffmpeg", line.Trim());
        }

        /// <summary>
        /// Düzgün kapatma: yakalamaya 'q' (kodlayıcıyı boşaltır), sonra yayıncının girişini kapatır
        /// (dosyayı/yayını sonlandırır). Süreçler 5 sn içinde kapanmazsa sonlandırılır.
        /// </summary>
        public void Stop()
        {
            var p = pub;
            if (p == null || p.HasExited) return;
            var cap = proc;
            watchdog?.Dispose(); statsTimer?.Dispose();
            watchdog = statsTimer = null;
            lock (stdinLock)
            {
                stopping = true;
                try { if (cap != null && !cap.HasExited) { cap.StandardInput.Write('q'); cap.StandardInput.Flush(); } } catch { }
            }
            if (cap != null && !cap.WaitForExit(5000)) KillCapture();
            pumpThread?.Join(3000); // son paketler yayıncıya geçsin
            try { pubIn.Close(); } catch { }
            if (!p.WaitForExit(5000)) KillPublisher();
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
