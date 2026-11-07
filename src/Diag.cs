using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ValoLive
{
    /// <summary>
    /// Yayın başına tanılama günlüğü (%LOCALAPPDATA%\ValoLive\logs\yayin_*.log). Kopma ve takılmaların
    /// nedenini sonradan kesin görmek için: olaylar (yakalama kopuşu, yeniden başlama süresi, Kick bağlantısı,
    /// ön plan değişimi, örtü) ve 5 sn'de bir istatistik satırı (FPS, düşen kare, Kick'e giden veri, ağ tıkanması).
    /// Hata kodları Türkçe açıklamasıyla yazılır. Stream key hiçbir satıra girmez.
    /// </summary>
    static class Diag
    {
        public static string Dir => Path.Combine(Settings.Dir, "logs");
        static readonly object lk = new object();
        static string file;
        static DateTime started;
        static int captureRestarts, publisherDrops;
        static double gapTotalMs, gapMaxMs;

        public static void Begin(string header)
        {
            lock (lk)
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    foreach (var old in new DirectoryInfo(Dir).GetFiles("yayin_*.log").OrderByDescending(f => f.Name).Skip(30)) old.Delete();
                }
                catch { }
                started = DateTime.Now;
                captureRestarts = publisherDrops = 0;
                gapTotalMs = gapMaxMs = 0;
                file = Path.Combine(Dir, $"yayin_{started:yyyyMMdd_HHmmss}.log");
            }
            Write("OTURUM", header);
        }

        public static void Event(string kind, string text) => Write(kind, text);

        public static void CaptureRestarted(double gapMs)
        {
            lock (lk) { captureRestarts++; gapTotalMs += gapMs; gapMaxMs = Math.Max(gapMaxMs, gapMs); }
            Write("YAKALAMA", $"yeniden görüntü geldi: {gapMs:0} ms kesinti");
        }

        public static void PublisherDropped() { lock (lk) publisherDrops++; }

        public static void End(string why)
        {
            string sum;
            lock (lk)
            {
                if (file == null) return;
                sum = $"{why} · süre {DateTime.Now - started:hh\\:mm\\:ss} · yakalama yeniden başlama {captureRestarts} " +
                      $"(toplam kesinti {gapTotalMs / 1000:0.0} sn, en uzun {gapMaxMs:0} ms) · Kick bağlantı kopması {publisherDrops}";
            }
            Write("ÖZET", sum);
            lock (lk) file = null;
        }

        static void Write(string kind, string text)
        {
            lock (lk)
            {
                if (file == null) return;
                try { File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} [{kind}] {Streamer.MaskSecrets(text)}\r\n"); } catch { }
            }
        }

        /// <summary>ffmpeg / Windows hata kodlarının Türkçe açıklaması (bilinmiyorsa boş).</summary>
        public static string Explain(string err)
        {
            if (string.IsNullOrWhiteSpace(err)) return "hata mesajı yok (süreç dışarıdan sonlandırıldı ya da çöktü)";
            var known = new (string Pattern, string Text)[]
            {
                ("887a0026", "DXGI_ERROR_ACCESS_LOST: ekran modu değişti (tam ekran oyun alta alındı/geri geldi, UAC penceresi, çözünürlük değişimi)"),
                ("887a0027", "DXGI_ERROR_WAIT_TIMEOUT: ekran kartı kare vermedi"),
                ("887a0001", "DXGI_ERROR_INVALID_CALL: ekran yakalama başlatılamadı (genelde mod geçişi sürüyor)"),
                ("887a0004", "DXGI_ERROR_UNSUPPORTED: masaüstü çoğaltma desteklenmiyor (başka bir uygulama tam ekranı tutuyor olabilir)"),
                ("-10053", "WSAECONNABORTED: Kick bağlantısı ağ ya da sunucu tarafında koparıldı"),
                ("-10054", "WSAECONNRESET: Kick sunucusu bağlantıyı sıfırladı"),
                ("-10060", "WSAETIMEDOUT: Kick sunucusuna ulaşılamadı (zaman aşımı)"),
                ("-10051", "WSAENETUNREACH: ağ erişilemez (internet bağlantısı yok)"),
                ("Error opening output", "Kick bağlantıyı açmadı (URL/anahtar ya da ağ)"),
                ("session has been invalidated", "TLS oturumu sunucu tarafından kapatıldı"),
                ("NVENC", "NVIDIA kodlayıcı hatası"),
            };
            var hits = known.Where(k => err.IndexOf(k.Pattern, StringComparison.OrdinalIgnoreCase) >= 0).Select(k => k.Text).ToList();
            return string.Join("; ", hits);
        }

        /// <summary>Valorant'ın son kaydettiği görüntü modu (UE: 0 tam ekran, 1 pencereli tam ekran, 2 pencere). Bulunamazsa -1.</summary>
        public static int ValorantFullscreenMode()
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"VALORANT\Saved\Config");
                var ini = new DirectoryInfo(dir).GetFiles("GameUserSettings.ini", SearchOption.AllDirectories).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
                if (ini == null) return -1;
                var text = File.ReadAllText(ini.FullName);
                foreach (var key in new[] { "LastConfirmedFullscreenMode", "FullscreenMode", "PreferredFullscreenMode" })
                {
                    var m = Regex.Match(text, "^" + key + @"=(\d)", RegexOptions.Multiline);
                    if (m.Success) return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                }
            }
            catch { }
            return -1;
        }

        public static string ModeName(int mode) =>
            mode == 0 ? "Tam ekran (exclusive)" : mode == 1 ? "Pencereli tam ekran" : mode == 2 ? "Pencere" : "bilinmiyor";
    }
}
