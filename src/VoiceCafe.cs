using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace ValoLive
{
    // ---------- Ses değiştirici: internet kafe ortamı ----------
    // Gerçek kayıtlar: %LOCALAPPDATA%\ValoLive\kafe\ortam (uğultu, klavye; sürekli döner) ve \sesler (bağırışlar; arada
    // rastgele biri patlar). Her bağırış farklı hız/perde, uzaklık ve yükseklikte çalınır: aynı kayıt aynı duyulmaz.
    // Ortam kaydı yoksa uğultu bağırışların kendisinden (kısık, boğuk, üst üste) yapılır. Klavye/fare tıkırtısı sentez.

    /// <summary>Kafe kayıtları (48 kHz tek kanal). Arka planda yüklenir; ses iş parçacığı yalnızca hazır diziyi okur.</summary>
    static class CafeBank
    {
        public const int Rate = 48000;
        public static string Dir => Path.Combine(Settings.Dir, "kafe");
        public static string BedDir => Path.Combine(Dir, "ortam");
        public static string ShoutDir => Path.Combine(Dir, "sesler");
        static readonly string[] Exts = { ".wav", ".mp3", ".ogg", ".m4a", ".flac", ".opus", ".aac", ".wma" };

        public static volatile float[][] Beds = new float[0][], Shouts = new float[0][];
        static int loading, loadedOnce;
        /// <summary>Yükleme bitince (arayüz sayıyı güncellesin).</summary>
        public static event Action Loaded;

        public static void EnsureLoaded() { if (Volatile.Read(ref loadedOnce) == 0) Reload(); }

        public static void Reload()
        {
            if (Interlocked.Exchange(ref loading, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Directory.CreateDirectory(BedDir); Directory.CreateDirectory(ShoutDir);
                    Beds = LoadAll(BedDir, 6, 180);
                    Shouts = LoadAll(ShoutDir, 200, 10);
                }
                catch { }
                finally { Volatile.Write(ref loadedOnce, 1); Volatile.Write(ref loading, 0); }
                try { Loaded?.Invoke(); } catch { }
            });
        }

        static float[][] LoadAll(string dir, int maxFiles, double maxSec)
        {
            var list = new List<float[]>();
            foreach (var f in Directory.GetFiles(dir).Where(f => Exts.Contains(Path.GetExtension(f).ToLowerInvariant())).OrderBy(f => f).Take(maxFiles))
            {
                float[] a = null;
                try { a = Path.GetExtension(f).ToLowerInvariant() == ".wav" ? ReadWav(f) : null; } catch { }
                if (a == null) try { a = Decode(f); } catch { }
                if (a == null || a.Length < Rate / 10) continue;
                if (a.Length > maxSec * Rate) Array.Resize(ref a, (int)(maxSec * Rate));
                Normalize(a);
                list.Add(a);
            }
            return list.ToArray();
        }

        /// <summary>Zirveyi -3 dB'ye getirir (kayıtlar farklı yükseklikte olsa da karışım dengeli kalsın).</summary>
        static void Normalize(float[] a)
        {
            float pk = 0;
            foreach (var v in a) pk = Math.Max(pk, Math.Abs(v));
            if (pk < 1e-4f) return;
            float g = 0.7f / pk;
            for (int i = 0; i < a.Length; i++) a[i] *= g;
        }

        /// <summary>PCM 8/16/24/32 bit ya da 32 bit kayan noktalı WAV → 48 kHz tek kanal. Desteklenmezse null.</summary>
        static float[] ReadWav(string path)
        {
            using (var r = new BinaryReader(File.OpenRead(path)))
            {
                if (new string(r.ReadChars(4)) != "RIFF") return null;
                r.ReadInt32();
                if (new string(r.ReadChars(4)) != "WAVE") return null;
                int fmtTag = 0, ch = 0, rate = 0, bits = 0;
                while (r.BaseStream.Position + 8 <= r.BaseStream.Length)
                {
                    string id = new string(r.ReadChars(4));
                    int len = r.ReadInt32();
                    long next = r.BaseStream.Position + len + (len & 1);
                    if (id == "fmt ")
                    {
                        fmtTag = r.ReadInt16(); ch = r.ReadInt16(); rate = r.ReadInt32(); r.ReadInt32(); r.ReadInt16(); bits = r.ReadInt16();
                        if (fmtTag == unchecked((short)0xFFFE) || fmtTag == -2) { r.ReadInt16(); r.ReadInt16(); r.ReadInt32(); fmtTag = r.ReadInt16(); }
                    }
                    else if (id == "data")
                    {
                        if (ch <= 0 || rate <= 0 || !(fmtTag == 1 || (fmtTag == 3 && bits == 32))) return null;
                        int bps = bits / 8, frames = (int)Math.Min(len, r.BaseStream.Length - r.BaseStream.Position) / (bps * ch);
                        var bytes = r.ReadBytes(frames * bps * ch);
                        var mono = new float[frames];
                        for (int f = 0; f < frames; f++)
                        {
                            double s = 0;
                            for (int c = 0; c < ch; c++)
                            {
                                int o = (f * ch + c) * bps;
                                switch (bits)
                                {
                                    case 8: s += (bytes[o] - 128) / 128.0; break;
                                    case 16: s += BitConverter.ToInt16(bytes, o) / 32768.0; break;
                                    case 24: s += ((bytes[o] << 8 | bytes[o + 1] << 16 | bytes[o + 2] << 24) >> 8) / 8388608.0; break;
                                    case 32: s += fmtTag == 3 ? BitConverter.ToSingle(bytes, o) : BitConverter.ToInt32(bytes, o) / 2147483648.0; break;
                                    default: return null;
                                }
                            }
                            mono[f] = (float)(s / ch);
                        }
                        return Resample(mono, rate);
                    }
                    r.BaseStream.Position = next;
                }
            }
            return null;
        }

        static float[] Resample(float[] a, int rate)
        {
            if (rate == Rate) return a;
            double step = rate / (double)Rate;
            var o = new float[(int)(a.Length / step)];
            for (int i = 0; i < o.Length; i++)
            {
                double p = i * step; int k = (int)p; float t = (float)(p - k);
                o[i] = k + 1 < a.Length ? a[k] + (a[k + 1] - a[k]) * t : a[a.Length - 1];
            }
            return o;
        }

        /// <summary>Diğer biçimler (mp3, ogg...) ffmpeg ile çözülür (yayın için zaten var).</summary>
        static float[] Decode(string path)
        {
            var psi = new ProcessStartInfo(Streamer.FindFfmpeg(), "-v quiet -i \"" + path + "\" -t 180 -ac 1 -ar " + Rate + " -f f32le -")
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using (var p = Process.Start(psi))
            using (var ms = new MemoryStream())
            {
                p.StandardOutput.BaseStream.CopyTo(ms);
                p.WaitForExit(5000);
                var b = ms.ToArray();
                if (b.Length < 4) return null;
                var a = new float[b.Length / 4];
                Buffer.BlockCopy(b, 0, a, 0, a.Length * 4);
                return a;
            }
        }

        // ---- Kayıt: ses değiştiricinin mikrofonundan, efektsiz ----
        static volatile float[] rec;
        static int recPos, recRate;
        public static bool Recording => rec != null;

        public static void StartRecording(double seconds) { recPos = 0; recRate = 0; rec = new float[(int)(seconds * Rate)]; }

        /// <summary>Ses iş parçacığından: ham mikrofon (tek kanal). Kayıt yoksa hemen döner.</summary>
        public static void Tap(float[] buf, int n, int rate)
        {
            var r = rec;
            if (r == null) return;
            recRate = rate;
            int k = Math.Min(n, r.Length - recPos);
            if (k > 0) Array.Copy(buf, 0, r, recPos, k);
            recPos += Math.Max(0, k);
        }

        /// <summary>Kaydı bitirir; baştaki ve sondaki sessizlik kırpılıp sesler klasörüne yazılır. Konuşma yoksa false.</summary>
        public static bool StopRecording()
        {
            var r = rec; rec = null;
            if (r == null || recRate == 0) return false;
            int n = Math.Min(recPos, r.Length);
            float pk = 0;
            for (int i = 0; i < n; i++) pk = Math.Max(pk, Math.Abs(r[i]));
            if (pk < 0.02f) return false;
            float thr = pk * 0.08f;
            int a = 0, b = n - 1;
            while (a < n && Math.Abs(r[a]) < thr) a++;
            while (b > a && Math.Abs(r[b]) < thr) b--;
            int pad = recRate / 20;
            a = Math.Max(0, a - pad); b = Math.Min(n - 1, b + pad * 3);
            var clip = new float[b - a + 1];
            Array.Copy(r, a, clip, 0, clip.Length);
            for (int i = 0; i < pad && i < clip.Length; i++) { clip[i] *= i / (float)pad; clip[clip.Length - 1 - i] *= i / (float)pad; }
            Directory.CreateDirectory(ShoutDir);
            WriteWav(Path.Combine(ShoutDir, "kayit-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".wav"), clip, recRate);
            Reload();
            return true;
        }

        static void WriteWav(string path, float[] a, int rate)
        {
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write("RIFF".ToCharArray()); w.Write(36 + a.Length * 2); w.Write("WAVE".ToCharArray());
                w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write("data".ToCharArray()); w.Write(a.Length * 2);
                foreach (var v in a) w.Write((short)Math.Max(-32767, Math.Min(32767, v * 32767)));
            }
        }
    }

    /// <summary>Sesin arkasına sürekli internet kafe ortamı ekler (konuşmasan da çalar; bas-konuş tuşuyla duyulur).</summary>
    sealed class Cafe : Fx
    {
        sealed class Voice
        {
            public float[] Clip; public double Pos, Step; public float Gain; public Biquad Tone;
        }

        readonly int sr;
        readonly float level;
        readonly Random rnd = new Random();
        // Ortam yatağı: iki parça arasında çapraz geçiş.
        Voice bedA, bedB; int bedLeft, xfade, xfadeLen;
        readonly Voice[] babble = new Voice[5];
        readonly Voice[] shouts = new Voice[2];
        int nextShout, lastShout = -1, lastShout2 = -1;
        // Tıkırtılar
        readonly Biquad keyTone, keyTone2;
        int keysLeft, keyGap, mouseGap; double keyEnv, clickEnv; float keyAmp;
        readonly ConvVerb room;
        double drift = 1, driftTarget = 1; int driftLeft;

        public Cafe(int sr, float amount = 0.5f)
        {
            this.sr = sr;
            level = 0.25f + 1.1f * Math.Max(0, Math.Min(1, amount));
            CafeBank.EnsureLoaded();
            keyTone = Biquad.Peak(sr, 3200, 1.5, 12); keyTone2 = Biquad.Highpass(sr, 900);
            room = new ConvVerb(sr, Room.Salon, 0.35f, 1);
            nextShout = (int)(sr * (1.5 + 3 * rnd.NextDouble()));
            keyGap = sr / 2; mouseGap = sr;
        }

        double Speed() => CafeBank.Rate / (double)sr;

        Voice Play(float[] clip, double semis, float gain, double cutoff, bool randomStart)
        {
            return new Voice
            {
                Clip = clip, Step = Speed() * Math.Pow(2, semis / 12), Gain = gain,
                Pos = randomStart ? rnd.NextDouble() * Math.Max(0, clip.Length - sr) : 0,
                Tone = Biquad.Lowpass(sr, Math.Min(cutoff, sr * 0.45)),
            };
        }

        static float Read(Voice v)
        {
            int k = (int)v.Pos;
            if (k + 1 >= v.Clip.Length) return 0;
            float t = (float)(v.Pos - k);
            float s = v.Clip[k] + (v.Clip[k + 1] - v.Clip[k]) * t;
            v.Pos += v.Step;
            return v.Tone.Run(s) * v.Gain;
        }

        static bool Done(Voice v) => v == null || (int)v.Pos + 1 >= v.Clip.Length;

        public override float Run(float x)
        {
            var beds = CafeBank.Beds; var clips = CafeBank.Shouts;
            double amb = 0;

            // Yükseklik yavaşça dalgalanır: kafe bazen sakin, bazen gürültülü.
            if (--driftLeft <= 0) { driftTarget = 0.6 + 0.8 * rnd.NextDouble(); driftLeft = sr * (4 + rnd.Next(8)); }
            drift += (driftTarget - drift) * (1.0 / (sr * 2));

            if (beds.Length > 0)
            {
                if (bedA == null || --bedLeft <= 0 || Done(bedA))
                {
                    bedB = bedA;
                    bedA = Play(beds[rnd.Next(beds.Length)], (rnd.NextDouble() - 0.5) * 1.2, 0.33f, 9000, true);
                    bedLeft = sr * (15 + rnd.Next(20));
                    xfadeLen = xfade = sr * 2;
                }
                double a = Read(bedA);
                if (xfade > 0 && bedB != null && !Done(bedB)) { double t = xfade-- / (double)xfadeLen; a = a * (1 - t) + Read(bedB) * t; }
                amb += a;
            }
            else if (clips.Length > 0)
            {
                // Uğultu: kayıtlardan, kısık ve boğuk, birkaç tanesi hep üst üste.
                for (int i = 0; i < babble.Length; i++)
                {
                    if (Done(babble[i]))
                    {
                        if (rnd.NextDouble() > 3.0 / sr) continue; // ortalama ~1/3 sn'de bir yeni ses
                        babble[i] = Play(clips[rnd.Next(clips.Length)], -5 + 9 * rnd.NextDouble(), 0.05f + 0.06f * (float)rnd.NextDouble(), 1300 + 1500 * rnd.NextDouble(), false);
                    }
                    amb += Read(babble[i]);
                }
            }

            // Bağırışlar: hiçbiri art arda tekrar etmez, her biri farklı kişi gibi.
            if (clips.Length > 0 && --nextShout <= 0)
            {
                for (int i = 0; i < shouts.Length; i++)
                    if (Done(shouts[i]))
                    {
                        int k = rnd.Next(clips.Length);
                        for (int t = 0; t < 6 && clips.Length > 2 && (k == lastShout || k == lastShout2); t++) k = rnd.Next(clips.Length);
                        lastShout2 = lastShout; lastShout = k;
                        double semis = (rnd.NextDouble() < 0.5 ? -1 : 1) * (1 + 4.5 * rnd.NextDouble());
                        float near = (float)rnd.NextDouble();
                        shouts[i] = Play(clips[k], semis, 0.12f + 0.3f * near, 2500 + 9000 * near, false);
                        break;
                    }
                nextShout = (int)(sr * (3 + 9 * rnd.NextDouble()));
            }
            for (int i = 0; i < shouts.Length; i++) if (!Done(shouts[i])) amb += Read(shouts[i]);

            // Klavye: hızlı yazma dizileri; fare: tek tük tıklar.
            double tick = 0;
            if (--keyGap <= 0)
            {
                if (keysLeft <= 0) { keysLeft = 4 + rnd.Next(26); keyGap = sr * (1 + rnd.Next(4)); }
                else { keysLeft--; keyEnv = 1; keyAmp = 0.02f + 0.03f * (float)rnd.NextDouble(); keyGap = (int)(sr * (0.06 + 0.12 * rnd.NextDouble())); }
            }
            if (--mouseGap <= 0) { clickEnv = 1; mouseGap = (int)(sr * (0.15 + (rnd.NextDouble() < 0.6 ? 0.1 : 2.5) * rnd.NextDouble())); }
            if (keyEnv > 1e-3 || clickEnv > 1e-3)
            {
                double nz = rnd.NextDouble() * 2 - 1;
                tick = nz * (keyEnv * keyAmp + clickEnv * 0.015);
                keyEnv *= Math.Exp(-1.0 / (sr * 0.006)); clickEnv *= Math.Exp(-1.0 / (sr * 0.003));
            }
            amb += keyTone.Run(keyTone2.Run((float)tick));

            return x + room.Run((float)(amb * drift * level));
        }
    }
}
