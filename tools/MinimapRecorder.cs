using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

/// <summary>
/// Geliştirme aracı: Valorant ön plandayken sol üstteki minimap bölgesini saniyede bir PNG olarak kaydeder.
/// Minimap okuyucusunu gerçek kareler üzerinde geliştirip ölçmek için örnek toplar. Yalnızca o bölgeyi alır.
/// Kullanım: MinimapRecorder.exe [saniye_aralığı=1] — Ctrl+C ile durur.
/// </summary>
static class MinimapRecorder
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    const int Size = 480; // 1920x1080'de varsayılan minimap bu karenin içinde kalır
    // Üst orta skor çubuğu (ajan portreleri, skor, süre): ölen rakibi okumak için. Sağ üstteki öldürme akışı (oyuncu adları) dışarıda kalır.
    static readonly Rectangle Top = new Rectangle(480, 0, 960, 140);

    static void Main(string[] args)
    {
        double interval = args.Length > 0 && double.TryParse(args[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 1;
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ValoLive", "minimap-samples", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(dir);
        Console.WriteLine("Kayit klasoru: " + dir);
        Console.WriteLine("Valorant on plandayken her " + interval + " sn'de bir minimap kaydedilir. Durdurmak icin Ctrl+C.");

        bool stop = false;
        Console.CancelKeyPress += (o, e) => { e.Cancel = true; stop = true; };
        int saved = 0;
        long lastHash = 0;
        string topDir = Path.Combine(dir, "ust");
        Directory.CreateDirectory(topDir);
        using (var bmp = new Bitmap(Size, Size, PixelFormat.Format24bppRgb))
        using (var g = Graphics.FromImage(bmp))
        using (var top = new Bitmap(Top.Width, Top.Height, PixelFormat.Format24bppRgb))
        using (var gt = Graphics.FromImage(top))
        {
            while (!stop)
            {
                var t0 = DateTime.Now;
                if (GameInFront())
                {
                    g.CopyFromScreen(0, 0, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);
                    long h = Hash(bmp);
                    if (h != lastHash) // menüde donmuş görüntüyü tekrar tekrar kaydetme
                    {
                        lastHash = h;
                        string name = DateTime.Now.ToString("HHmmss_fff") + ".png";
                        bmp.Save(Path.Combine(dir, name), ImageFormat.Png);
                        gt.CopyFromScreen(Top.X, Top.Y, 0, 0, Top.Size, CopyPixelOperation.SourceCopy);
                        top.Save(Path.Combine(topDir, name), ImageFormat.Png);
                        saved++;
                        Console.Write("\rKaydedilen kare: " + saved + "   ");
                    }
                }
                int wait = (int)(interval * 1000 - (DateTime.Now - t0).TotalMilliseconds);
                if (wait > 0) Thread.Sleep(wait);
            }
        }
        Console.WriteLine("\nBitti. " + saved + " kare: " + dir);
    }

    static bool GameInFront()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
        try { return Process.GetProcessById((int)pid).ProcessName.StartsWith("VALORANT", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    static long Hash(Bitmap b)
    {
        long h = 17;
        for (int y = 0; y < b.Height; y += 24)
            for (int x = 0; x < b.Width; x += 24)
                h = h * 31 + b.GetPixel(x, y).ToArgb();
        return h;
    }
}
