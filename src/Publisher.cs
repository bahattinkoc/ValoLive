using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace ValoLive
{
    enum TargetState { Connecting, Live, Retrying, Rejected, Failed, Stopped }

    /// <summary>
    /// Tek bir platforma (ya da test dosyasına) giden yayıncı ffmpeg. Yakalamanın MPEG-TS akışı ona kendi kuyruğu ve
    /// yazma iş parçacığıyla gider: ağı yavaşlayan platform ne yakalamayı ne de öteki platformları bekletir.
    /// Kuyruk ~10 sn'lik veriyi aşarsa platform yetişemiyor demektir; süreç kapatılır, Streamer yeniden bağlar.
    /// </summary>
    sealed class Publisher
    {
        const long MaxQueued = 8 * 1024 * 1024;
        const int VideoPid = 0x100;   // ffmpeg mpegts: ilk akış (görüntü) bu PID'de

        public readonly Target Target;
        public DateTime StartedAt { get; private set; }
        public bool Alive { get { var p = proc; return p != null && !p.HasExited; } }
        /// <summary>İstatistik penceresi: gönderilen bayt ve en uzun yazma beklemesi (Streamer sıfırlar).</summary>
        public long Bytes; public double MaxWriteMs;
        /// <summary>(yayıncı, çıkış kodu, son hata satırları)</summary>
        public event Action<Publisher, int, string> Exited;
        public Action<string> Line;   // ffmpeg hata satırları (Streamer günlüğe yazar)

        Process proc;
        Stream input;
        readonly Queue<byte[]> queue = new Queue<byte[]>();
        long queued;
        readonly object lk = new object();
        bool closing, overflowed;
        readonly Queue<string> errors = new Queue<string>();

        // Akışın ortasına katılma: ilk paket PAT (tablo) olmalı, hemen ardından anahtar kare gelmeli.
        bool waitKey;
        readonly List<byte[]> pending = new List<byte[]>();
        long skipped;

        public Publisher(Target t) { Target = t; }

        /// <summary>joinMidStream: yakalama zaten akarken (yeniden bağlanma) → bir sonraki anahtar kareden başla.</summary>
        public void Start(string ffmpeg, string args, bool joinMidStream)
        {
            waitKey = joinMidStream;
            var p = new Process
            {
                StartInfo = new ProcessStartInfo(ffmpeg, args)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8,
                },
                EnableRaisingEvents = true,
            };
            p.ErrorDataReceived += (s, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                lock (errors) { errors.Enqueue(Streamer.MaskSecrets(e.Data.Trim())); while (errors.Count > 4) errors.Dequeue(); }
                Line?.Invoke(e.Data);
            };
            p.Exited += (s, e) =>
            {
                lock (lk) { closing = true; queue.Clear(); queued = 0; Monitor.PulseAll(lk); }
                int code = 0;
                try { code = p.ExitCode; } catch { }
                string err;
                lock (errors) err = (overflowed ? "gönderim kuyruğu doldu (platform veriyi yetiştiremedi) | " : "") + string.Join(" | ", errors);
                Exited?.Invoke(this, code, err);
            };
            StartedAt = DateTime.Now;
            p.Start();
            p.BeginErrorReadLine();
            input = p.StandardInput.BaseStream;
            proc = p;
            new Thread(Write) { IsBackground = true, Name = "pub-" + Target.Id }.Start();
        }

        /// <summary>Tam 188 baytlık TS paketleri (Pump garanti eder). Kopyalanır; çağıran tamponu yeniden kullanabilir.</summary>
        public void Feed(byte[] buf, int count)
        {
            int from = 0;
            if (waitKey)
            {
                from = FindJoin(buf, count);
                if (from < 0) return;
            }
            var chunk = new byte[count - from];
            Buffer.BlockCopy(buf, from, chunk, 0, chunk.Length);
            lock (lk)
            {
                if (closing) return;
                if (queued + chunk.Length > MaxQueued)
                {
                    overflowed = true;
                    closing = true;
                    queue.Clear(); queued = 0;
                    Monitor.PulseAll(lk);
                    Kill();
                    return;
                }
                queue.Enqueue(chunk); queued += chunk.Length;
                Monitor.PulseAll(lk);
            }
        }

        /// <summary>
        /// Katılma noktası: PAT paketinden başlayıp hemen ardından anahtar kare (görüntü PID'inde, yükü başlatan ve
        /// "random access" işaretli paket) gelen yer. ffmpeg her anahtar karenin önüne PAT/PMT yazar. Bulunana kadar
        /// paketler biriktirilir; bulununca biriken + tampondaki kalan döner (biriken önce kuyruğa girer).
        /// 4 MB içinde bulunamazsa (beklenmedik akış) olduğu yerden başlanır.
        /// </summary>
        int FindJoin(byte[] buf, int count)
        {
            for (int i = 0; i + 188 <= count; i += 188)
            {
                int pid = ((buf[i + 1] & 0x1f) << 8) | buf[i + 2];
                bool start = (buf[i + 1] & 0x40) != 0;
                if (pid == 0)
                {
                    pending.Clear();
                    pending.Add(Packet(buf, i));
                    continue;
                }
                if (pending.Count > 0)
                {
                    if (pid == VideoPid && start)
                    {
                        int afc = (buf[i + 3] >> 4) & 3;
                        bool key = afc >= 2 && buf[i + 4] > 0 && (buf[i + 5] & 0x40) != 0;
                        if (key) return Commit(i);
                        pending.Clear();
                        continue;
                    }
                    pending.Add(Packet(buf, i));
                    if (pending.Count > 2000) pending.Clear();
                }
                if ((skipped += 188) > 4 * 1024 * 1024)
                {
                    pending.Clear();
                    waitKey = false;
                    Line?.Invoke("katılma noktası bulunamadı, akışın ortasından başlanıyor");
                    return i;
                }
            }
            return -1;
        }

        int Commit(int at)
        {
            waitKey = false;
            lock (lk)
                foreach (var p in pending) { queue.Enqueue(p); queued += p.Length; }
            pending.Clear();
            return at;
        }

        static byte[] Packet(byte[] buf, int i) { var p = new byte[188]; Buffer.BlockCopy(buf, i, p, 0, 188); return p; }

        void Write()
        {
            var s = input;
            try
            {
                while (true)
                {
                    byte[] chunk;
                    lock (lk)
                    {
                        while (queue.Count == 0 && !closing) Monitor.Wait(lk);
                        if (queue.Count == 0) break;
                        chunk = queue.Dequeue(); queued -= chunk.Length;
                    }
                    var t0 = Stopwatch.GetTimestamp();
                    s.Write(chunk, 0, chunk.Length);
                    s.Flush();
                    double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    Interlocked.Add(ref Bytes, chunk.Length);
                    if (ms > MaxWriteMs) MaxWriteMs = ms;
                }
            }
            catch { } // süreç kapandı: Exited ilgilenir
            try { s.Close(); } catch { } // girişin kapanması ffmpeg'e "akış bitti" der (dosya/yayın düzgün kapanır)
        }

        /// <summary>Düzgün kapanış: kuyruktakiler gönderilir, sonra giriş kapanır.</summary>
        public void Finish() { lock (lk) { closing = true; Monitor.PulseAll(lk); } }

        public bool WaitForExit(int ms) { var p = proc; return p == null || p.WaitForExit(ms); }

        public void Kill() { try { var p = proc; if (p != null && !p.HasExited) p.Kill(); } catch { } }
    }
}
