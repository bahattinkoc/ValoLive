using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace ValoLive
{
    // ---------- Ses paneli ----------
    // Kullanıcının klasöre attığı kısa sesler (mp3, wav...) tuşla sanal mikrofona çalınır: takım arkadaşları duyar,
    // kullanıcı da kulaklıktan duyar. Sesler ses değiştiricinin iş parçacığında, efektten sonra karışıma eklenir.

    static class SoundBoard
    {
        public const int Rate = 48000, Max = 12;
        public static string Dir => Path.Combine(Settings.Dir, "ses-paneli");
        static readonly string[] Exts = { ".mp3", ".wav", ".ogg", ".m4a", ".flac", ".opus", ".aac", ".wma" };

        public sealed class Sound { public string Name; public float[] Data; }
        sealed class Playing { public float[] Data; public double Pos; }

        public static volatile Sound[] Sounds = new Sound[0];
        public static volatile float Volume = 0.6f;
        /// <summary>Yükleme bitince (arayüz düğmeleri yenilesin).</summary>
        public static event Action Loaded;

        static int loading, again;
        static readonly object gate = new object();
        static volatile Playing[] playing = new Playing[0];

        public static void Reload()
        {
            // Yükleme sürerken gelen istek kaybolmasın: bitince bir tur daha.
            if (Interlocked.Exchange(ref loading, 1) == 1) { Volatile.Write(ref again, 1); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                do
                {
                    Volatile.Write(ref again, 0);
                    try { Directory.CreateDirectory(Dir); Sounds = LoadAll(); } catch { }
                } while (Volatile.Read(ref again) == 1);
                Volatile.Write(ref loading, 0);
                try { Loaded?.Invoke(); } catch { }
            });
        }

        static Sound[] LoadAll()
        {
            var list = new List<Sound>();
            foreach (var f in Directory.GetFiles(Dir).Where(f => Exts.Contains(Path.GetExtension(f).ToLowerInvariant())).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase))
            {
                if (list.Count >= Max) break;
                float[] a = null;
                try { if (Path.GetExtension(f).ToLowerInvariant() == ".wav") a = ReadWav(f); } catch { }
                if (a == null) try { a = Decode(f); } catch { }
                if (a == null || a.Length < Rate / 20) continue;
                Normalize(a);
                list.Add(new Sound { Name = Path.GetFileNameWithoutExtension(f), Data = a });
            }
            return list.ToArray();
        }

        /// <summary>Zirveyi -3 dB'ye getirir: internetten gelen sesler çok farklı yükseklikte.</summary>
        static void Normalize(float[] a)
        {
            float pk = 0;
            foreach (var v in a) pk = Math.Max(pk, Math.Abs(v));
            if (pk < 1e-4f) return;
            float g = 0.7f / pk;
            for (int i = 0; i < a.Length; i++) a[i] *= g;
        }

        /// <summary>PCM 8/16/24/32 bit ya da 32 bit kayan noktalı WAV → 48 kHz tek kanal. Desteklenmezse null (ffmpeg dener).</summary>
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
                        fmtTag = (ushort)r.ReadInt16(); ch = r.ReadInt16(); rate = r.ReadInt32(); r.ReadInt32(); r.ReadInt16(); bits = r.ReadInt16();
                        if (fmtTag == 0xFFFE && len >= 26) { r.ReadInt16(); r.ReadInt16(); r.ReadInt32(); fmtTag = (ushort)r.ReadInt16(); }
                    }
                    else if (id == "data")
                    {
                        if (ch <= 0 || rate <= 0 || !(fmtTag == 1 || (fmtTag == 3 && bits == 32))) return null;
                        int bps = bits / 8, frames = (int)(Math.Min(len, r.BaseStream.Length - r.BaseStream.Position) / (bps * ch));
                        frames = Math.Min(frames, rate * 30);
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

        /// <summary>mp3 ve diğerleri ffmpeg ile çözülür (yayın için zaten var). En çok 30 sn.</summary>
        static float[] Decode(string path)
        {
            var psi = new ProcessStartInfo(Streamer.FindFfmpeg(), "-v quiet -i \"" + path + "\" -t 30 -vn -ac 1 -ar " + Rate + " -f f32le -")
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

        /// <summary>i. sesi çalar; zaten çalıyorsa baştan başlatır. Aynı anda en çok 3 ses.</summary>
        public static bool Trigger(int i)
        {
            var s = Sounds;
            if (i < 0 || i >= s.Length) return false;
            lock (gate)
            {
                var l = playing.Where(p => p.Data != s[i].Data).ToList();
                l.Add(new Playing { Data = s[i].Data });
                if (l.Count > 3) l.RemoveAt(0);
                playing = l.ToArray();
            }
            return true;
        }

        public static void StopAll() { lock (gate) playing = new Playing[0]; }

        /// <summary>Ses iş parçacığından: çalan seslerin karışımını out'a yazar. Çalan yoksa false (out'a dokunmaz).</summary>
        public static bool Render(float[] output, int n, int rate)
        {
            var ps = playing;
            if (ps.Length == 0) return false;
            Array.Clear(output, 0, n);
            double step = Rate / (double)rate;
            float g = Volume;
            bool ended = false;
            foreach (var p in ps)
            {
                var d = p.Data;
                for (int i = 0; i < n; i++)
                {
                    int k = (int)p.Pos;
                    if (k + 1 >= d.Length) { ended = true; break; }
                    float t = (float)(p.Pos - k);
                    output[i] += (d[k] + (d[k + 1] - d[k]) * t) * g;
                    p.Pos += step;
                }
            }
            if (ended) lock (gate) playing = playing.Where(p => (int)p.Pos + 1 < p.Data.Length).ToArray();
            return true;
        }
    }
}
