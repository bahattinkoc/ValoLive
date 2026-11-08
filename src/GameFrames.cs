using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace ValoLive
{
    /// <summary>
    /// Ekran okuyan özellikler (rakip hafızası, yayın anları) için oyun karesi. Ekran kartı motoru açıkken bölge
    /// motorun zaten yakaladığı oyun karesinden gelir: GDI CopyFromScreen her çağrıda DWM'i bekletir, oyunu yavaşlatır.
    /// Motor yoksa (yayın kapalı, ffmpeg yolu) ya da yanıt gecikirse eskisi gibi ekrandan kopyalanır.
    ///
    /// Paylaşılan bellek: 64 bayt başlık + BGRA. ValoLive x, y, w, h yazıp req'i artırır; valocap kopyalayıp
    /// status'u yazar, sonra done = req yapar. Bölge maske ve ters ekran uygulanmadan önceki ham oyun karesinden alınır.
    /// </summary>
    static class GameFrames
    {
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref Point p);

        const int Header = 64, Capacity = 8 << 20;   // en büyük bölge: 1440p'de minimap ~640x640, 4K'da ~960x960
        const int TimeoutMs = 150;
        static readonly object lk = new object();
        static MemoryMappedFile map;
        static MemoryMappedViewAccessor view;
        static IntPtr ptr;
        static EventWaitHandle ready;   // <ad>_ev: valocap iş bitince tetikler (Sleep(1) ~15 ms uyuyordu)
        static IntPtr window;   // motorun yakaladığı pencere; Zero = birincil ekran
        static string current;  // bölümün adı: eski motorun geç gelen kapanışı yenisini koparmasın
        static int req;
        static int misses;
        static bool announced;   // ilk başarılı okumada bir kez günlüğe

        /// <summary>Motor başlamadan önce: bölümü oluşturur, adını verir (--grab).</summary>
        public static string Attach(IntPtr capturedWindow)
        {
            lock (lk)
            {
                Close();
                string name = "valolive_grab_" + Guid.NewGuid().ToString("N");
                map = MemoryMappedFile.CreateNew(name, Header + Capacity);
                view = map.CreateViewAccessor();
                ptr = view.SafeMemoryMappedViewHandle.DangerousGetHandle() + (int)view.PointerOffset;
                Marshal.WriteInt32(ptr, 36, Capacity);
                ready = new EventWaitHandle(false, EventResetMode.AutoReset, name + "_ev");
                window = capturedWindow;
                req = 0; misses = 0; announced = false;
                if (!Enabled) MinimapMemory.Log("oyun karesi ekrandan okunuyor (EngineRegionReads kapalı)");
                return current = name;
            }
        }

        /// <summary>Motor kapandı: okumalar ekrana döner.</summary>
        public static void Detach(string name) { lock (lk) if (name != null && name == current) Close(); }

        static void Close()
        {
            try { view?.Dispose(); map?.Dispose(); ready?.Dispose(); } catch { }
            view = null; map = null; ready = null; ptr = IntPtr.Zero; current = null;
        }

        /// <summary>CopyFromScreen(left, top) yerine: bmp'yi ekranın o noktasından başlayan oyun karesiyle doldurur.
        /// Çağıran iş parçacığı gerçek piksel kullanmalı (MinimapMemory.PerMonitorDpi).</summary>
        public static void Copy(Bitmap bmp, int left, int top)
        {
            if (!FromEngine(bmp, left, top))
                using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(left, top, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);
        }

        /// <summary>settings.json "EngineRegionReads": false iken hep ekrandan okunur (karşılaştırma için).</summary>
        public static volatile bool Enabled = true;

        static bool FromEngine(Bitmap bmp, int left, int top)
        {
            if (!Enabled || ptr == IntPtr.Zero) return false;
            lock (lk)
            {
                if (ptr == IntPtr.Zero) return false;
                var o = new Point(0, 0);
                if (window != IntPtr.Zero && !ClientToScreen(window, ref o)) return false;
                int w = bmp.Width, h = bmp.Height;
                Marshal.WriteInt32(ptr, 8, left - o.X);
                Marshal.WriteInt32(ptr, 12, top - o.Y);
                Marshal.WriteInt32(ptr, 16, w);
                Marshal.WriteInt32(ptr, 20, h);
                ready.Reset();
                Thread.MemoryBarrier();
                Marshal.WriteInt32(ptr, 0, ++req);
                var until = Environment.TickCount + TimeoutMs;
                while (Marshal.ReadInt32(ptr, 4) != req)
                {
                    int rest = until - Environment.TickCount;
                    if (rest <= 0)
                    {
                        // Motor takıldıysa bu okuma ekrandan yapılır.
                        if (++misses % 20 == 1) MinimapMemory.Log($"motor bölge okuması gecikti ({misses}), ekrandan okunuyor");
                        return false;
                    }
                    ready.WaitOne(rest);
                }
                Thread.MemoryBarrier();
                if (Marshal.ReadInt32(ptr, 24) != 0) return false;   // bölge oyun karesinin dışında ya da kare yok
                misses = 0;
                if (!announced) { announced = true; MinimapMemory.Log($"oyun karesi ekran kartı motorundan okunuyor ({w}x{h} @ {left - o.X},{top - o.Y})"); }
                var raw = new byte[w * h * 4];
                Marshal.Copy(ptr + Header, raw, 0, raw.Length);
                ToBitmap(raw, bmp);
                return true;
            }
        }

        /// <summary>Ham BGRA → bmp (24 ya da 32 bit).</summary>
        static void ToBitmap(byte[] raw, Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, bmp.PixelFormat);
            try
            {
                if (bmp.PixelFormat == PixelFormat.Format24bppRgb)
                {
                    var row = new byte[d.Stride];
                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0, s = y * w * 4, t = 0; x < w; x++, s += 4, t += 3) { row[t] = raw[s]; row[t + 1] = raw[s + 1]; row[t + 2] = raw[s + 2]; }
                        Marshal.Copy(row, 0, d.Scan0 + y * d.Stride, w * 3);
                    }
                }
                else for (int y = 0; y < h; y++) Marshal.Copy(raw, y * w * 4, d.Scan0 + y * d.Stride, w * 4);
            }
            finally { bmp.UnlockBits(d); }
        }
    }
}
