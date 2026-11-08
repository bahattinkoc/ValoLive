using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ValoLive
{
    /// <summary>
    /// Yayın/test kaydı boyunca oyunun FPS'i (valocap --fps): DXGI Present olayları Windows olay kaydından (ETW)
    /// okunur, oyuna hiçbir şey enjekte edilmez. 5 sn'de bir [FPS] satırı, yayın sonunda özet. valocap yoksa ya da
    /// kullanıcı olay kaydını okuyamıyorsa (Performance Log Users değil) sessizce kapalı kalır.
    /// </summary>
    static class GameFps
    {
        static readonly object lk = new object();
        static Process proc;
        static long frames; static double seconds, worstLow = double.MaxValue, longestMs;
        static bool idle;

        public static void Start()
        {
            Stop();
            var exe = Streamer.FindEngine();
            if (exe == null) return;
            // Geliştirme testi: VALOLIVE_FPSEXE ile başka bir uygulama ölçülebilir.
            string game = Environment.GetEnvironmentVariable("VALOLIVE_FPSEXE") ?? CaptureGuard.ValorantExe + ".exe";
            var p = new Process
            {
                StartInfo = new ProcessStartInfo(exe, $"--fps \"{game}\"")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8,
                },
            };
            p.OutputDataReceived += (s, e) => { if (e.Data != null) OnLine(e.Data); };
            lock (lk) { frames = 0; seconds = 0; worstLow = double.MaxValue; longestMs = 0; idle = false; }
            try { p.Start(); p.BeginOutputReadLine(); lock (lk) proc = p; }
            catch (Exception ex) { Diag.Event("FPS", "ölçüm başlatılamadı: " + ex.Message); }
        }

        static void OnLine(string line)
        {
            if (line.StartsWith("error")) { Diag.Event("FPS", "ölçülemiyor: " + line.Substring(6)); return; }
            if (line == "fps none")
            {
                lock (lk) { if (idle) return; idle = true; }
                Diag.Event("FPS", "oyun kare vermiyor (kapalı ya da simge durumunda)");
                return;
            }
            var m = Regex.Match(line, @"^fps avg=(\d+) low1=(\d+) max_ms=([\d.]+) frames=(\d+)");
            if (!m.Success) return;
            double avg = D(m.Groups[1].Value), low = D(m.Groups[2].Value), max = D(m.Groups[3].Value);
            long n = long.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
            lock (lk)
            {
                idle = false;
                frames += n; seconds += n / Math.Max(1, avg);
                worstLow = Math.Min(worstLow, low); longestMs = Math.Max(longestMs, max);
            }
            Diag.Event("FPS", $"oyun {avg:0} · en kötü %1 {low:0} · en uzun kare {max:0.0} ms");
        }

        static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

        /// <summary>Ölçümü kapatır; ölçüm olduysa özet metni ("" yoksa).</summary>
        public static string Stop()
        {
            Process p;
            lock (lk) { p = proc; proc = null; }
            if (p == null) return "";
            try { if (!p.HasExited) { p.StandardInput.WriteLine("quit"); p.StandardInput.Flush(); } } catch { }
            try { if (!p.WaitForExit(2000)) p.Kill(); } catch { }
            lock (lk) return frames == 0 ? "" : $" · oyun FPS ort {frames / Math.Max(0.001, seconds):0} (en kötü %1 {worstLow:0}, en uzun kare {longestMs:0.0} ms)";
        }
    }
}
