using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace ValoLive
{
    // ---------- Ses değiştirici ----------
    // Valorant mikrofonu bir ses cihazından okur. Oyuna dokunmadan (Vanguard) sesi değiştirmenin yolu sanal mikrofon:
    // ValoLive gerçek mikrofonu okur, efekti uygular ve sonucu sanal bir kablonun "hoparlör" ucuna çalar; Valorant'ta giriş
    // cihazı olarak kablonun "mikrofon" ucu seçilir. Efektler burada, örnek örnek; gerçekçi sesler VoiceEngine.cs içinde (toplam gecikme ~60 ms).

    /// <summary>Tek kanallı, örnek başına çalışan efekt.</summary>
    abstract class Fx
    {
        public abstract float Run(float x);
    }

    /// <summary>İkinci derece filtre (RBJ "Audio EQ Cookbook").</summary>
    sealed class Biquad : Fx
    {
        readonly int sr;
        double b0, b1, b2, a1, a2, x1, x2, y1, y2;
        Biquad(int sr) { this.sr = sr; }

        void Set(double nb0, double nb1, double nb2, double na0, double na1, double na2)
        {
            b0 = nb0 / na0; b1 = nb1 / na0; b2 = nb2 / na0; a1 = na1 / na0; a2 = na2 / na0;
        }

        double W(double f) => 2 * Math.PI * Math.Min(f, sr * 0.45) / sr;

        public void SetLowpass(double f, double q = 0.707)
        {
            double w = W(f), c = Math.Cos(w), al = Math.Sin(w) / (2 * q);
            Set((1 - c) / 2, 1 - c, (1 - c) / 2, 1 + al, -2 * c, 1 - al);
        }

        public static Biquad Lowpass(int sr, double f, double q = 0.707) { var b = new Biquad(sr); b.SetLowpass(f, q); return b; }

        public static Biquad Highpass(int sr, double f, double q = 0.707)
        {
            var b = new Biquad(sr);
            double w = b.W(f), c = Math.Cos(w), al = Math.Sin(w) / (2 * q);
            b.Set((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + al, -2 * c, 1 - al);
            return b;
        }

        public static Biquad Peak(int sr, double f, double q, double db)
        {
            var b = new Biquad(sr);
            double w = b.W(f), c = Math.Cos(w), al = Math.Sin(w) / (2 * q), A = Math.Pow(10, db / 40);
            b.Set(1 + al * A, -2 * c, 1 - al * A, 1 + al / A, -2 * c, 1 - al / A);
            return b;
        }

        public static Biquad Shelf(int sr, double f, double db, bool high)
        {
            var b = new Biquad(sr);
            double w = b.W(f), c = Math.Cos(w), A = Math.Pow(10, db / 40), al = Math.Sin(w) / 2 * Math.Sqrt(2), s = 2 * Math.Sqrt(A) * al;
            if (high) b.Set(A * ((A + 1) + (A - 1) * c + s), -2 * A * ((A - 1) + (A + 1) * c), A * ((A + 1) + (A - 1) * c - s),
                            (A + 1) - (A - 1) * c + s, 2 * ((A - 1) - (A + 1) * c), (A + 1) - (A - 1) * c - s);
            else b.Set(A * ((A + 1) - (A - 1) * c + s), 2 * A * ((A - 1) - (A + 1) * c), A * ((A + 1) - (A - 1) * c - s),
                       (A + 1) + (A - 1) * c + s, -2 * ((A - 1) + (A + 1) * c), (A + 1) + (A - 1) * c - s);
            return b;
        }

        public override float Run(float x)
        {
            double y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = x; y2 = y1; y1 = y;
            return (float)y;
        }
    }

    /// <summary>Gecikme hattı: kesirli okumalı halka tampon.</summary>
    sealed class DelayLine
    {
        readonly float[] buf;
        readonly int mask;
        int pos;
        public DelayLine(int minSize) { int n = 1; while (n < minSize + 4) n <<= 1; buf = new float[n]; mask = n - 1; }
        public void Push(float x) { buf[pos & mask] = x; pos++; }
        /// <summary>d örnek önceki değer (d ≥ 1; 1 = son yazılan).</summary>
        public float Tap(double d)
        {
            double r = pos - d;
            int i = (int)Math.Floor(r);
            float f = (float)(r - i);
            float a = buf[i & mask], b = buf[(i + 1) & mask];
            return a + (b - a) * f;
        }
    }

    /// <summary>
    /// Ton kaydırma: iki okuma kafası gecikme hattında farklı hızda kayar, yarım pencere arayla ve güç korumalı
    /// (sin/cos) geçişle karışır. Süreyi değiştirmez; gecikmesi pencerenin yarısı kadar.
    /// </summary>
    sealed class Pitch : Fx
    {
        readonly DelayLine line;
        readonly double w, step;
        double phase;
        public Pitch(int sr, double semitones, double windowMs = 40)
        {
            w = sr * windowMs / 1000;
            step = (1 - Math.Pow(2, semitones / 12)) / w;
            line = new DelayLine((int)w + 2);
        }

        public override float Run(float x)
        {
            line.Push(x);
            phase += step;
            if (phase >= 1) phase -= 1; else if (phase < 0) phase += 1;
            double p2 = phase + 0.5; if (p2 >= 1) p2 -= 1;
            return line.Tap(1 + phase * w) * (float)Math.Sin(Math.PI * phase) + line.Tap(1 + p2 * w) * (float)Math.Sin(Math.PI * p2);
        }
    }

    /// <summary>Halka modülasyonu: sesi bir sinüsle çarpar (robot, uzaylı).</summary>
    sealed class Ring : Fx
    {
        readonly double inc; readonly float mix; double ph;
        public Ring(int sr, double hz, float mix = 1) { inc = 2 * Math.PI * hz / sr; this.mix = mix; }
        public override float Run(float x)
        {
            ph += inc; if (ph > 2 * Math.PI) ph -= 2 * Math.PI;
            return x * (1 - mix) + x * (float)Math.Sin(ph) * mix;
        }
    }

    /// <summary>Yumuşak bozulma (tanh).</summary>
    sealed class Drive : Fx
    {
        readonly float g, norm;
        public Drive(float gain) { g = gain; norm = 1f / (float)Math.Tanh(gain); }
        public override float Run(float x) => (float)Math.Tanh(g * x) * norm;
    }

    /// <summary>Bit ve örnek düşürme (telsiz, telefon, robot).</summary>
    sealed class Crush : Fx
    {
        readonly float q; readonly int hold; int n; float last;
        public Crush(int bits, int hold) { q = (float)Math.Pow(2, bits - 1); this.hold = Math.Max(1, hold); }
        public override float Run(float x)
        {
            if (n++ % hold == 0) last = (float)Math.Round(x * q) / q;
            return last;
        }
    }

    /// <summary>Yankı: geri beslemeli gecikme, her dönüşte biraz boğuklaşır.</summary>
    sealed class Echo : Fx
    {
        readonly DelayLine line; readonly double d; readonly float fb, mix; float lp;
        public Echo(int sr, double ms, float feedback, float mix) { d = sr * ms / 1000; line = new DelayLine((int)d + 2); fb = feedback; this.mix = mix; }
        public override float Run(float x)
        {
            float t = line.Tap(d);
            lp += (t - lp) * 0.4f;
            line.Push(x + lp * fb);
            return x + t * mix;
        }
    }

    /// <summary>Değişken gecikme: koro, flanger, vibrato (dry 0 = yalnızca kayan ses, yani vibrato).</summary>
    sealed class ModDelay : Fx
    {
        readonly DelayLine line; readonly double baseD, depth, inc; readonly float fb, dry, wet; double ph; float last;
        public ModDelay(int sr, double baseMs, double depthMs, double hz, float feedback, float dry, float wet)
        {
            baseD = sr * baseMs / 1000; depth = sr * depthMs / 1000; inc = 2 * Math.PI * hz / sr;
            line = new DelayLine((int)(baseD + depth) + 4); fb = feedback; this.dry = dry; this.wet = wet;
        }
        public override float Run(float x)
        {
            ph += inc; if (ph > 2 * Math.PI) ph -= 2 * Math.PI;
            line.Push(x + last * fb);
            last = line.Tap(1 + baseD + depth * (0.5 + 0.5 * Math.Sin(ph)));
            return x * dry + last * wet;
        }
    }

    /// <summary>Genlik dalgalanması.</summary>
    sealed class Tremolo : Fx
    {
        readonly double inc; readonly float depth; double ph;
        public Tremolo(int sr, double hz, float depth) { inc = 2 * Math.PI * hz / sr; this.depth = depth; }
        public override float Run(float x)
        {
            ph += inc; if (ph > 2 * Math.PI) ph -= 2 * Math.PI;
            return x * (1 - depth * (0.5f + 0.5f * (float)Math.Sin(ph)));
        }
    }

    /// <summary>Kesim frekansı dalgalanan alçak geçiren (su altı).</summary>
    sealed class Wobble : Fx
    {
        readonly Biquad lp; readonly double baseF, depth, inc; double ph; int n;
        public Wobble(int sr, double baseHz, double depthHz, double rate) { lp = Biquad.Lowpass(sr, baseHz, 1.2); baseF = baseHz; depth = depthHz; inc = 2 * Math.PI * rate / sr * 16; }
        public override float Run(float x)
        {
            if ((n++ & 15) == 0) { ph += inc; lp.SetLowpass(baseF + depth * Math.Sin(ph), 1.2); }
            return lp.Run(x);
        }
    }

    /// <summary>Freeverb'ün küçük hâli: dört geri beslemeli tarak + iki tüm geçiren.</summary>
    sealed class Verb : Fx
    {
        readonly float[][] comb; readonly int[] ci = new int[4]; readonly float[] store = new float[4];
        readonly float[][] ap; readonly int[] ai = new int[2];
        readonly float fb, damp, wet, dry;
        public Verb(int sr, float size, float wet, float damp = 0.3f, float dry = 1)
        {
            double k = sr / 44100.0 * (0.6 + 0.6 * size);
            comb = new[] { 1116, 1188, 1277, 1356 }.Select(l => new float[(int)(l * k)]).ToArray();
            ap = new[] { 556, 441 }.Select(l => new float[(int)(l * sr / 44100.0)]).ToArray();
            fb = 0.7f + 0.25f * size; this.damp = damp; this.wet = wet; this.dry = dry;
        }
        public override float Run(float x)
        {
            float input = x * (1 - fb) * 0.9f, sum = 0;
            for (int i = 0; i < 4; i++)
            {
                var b = comb[i];
                float o = b[ci[i]];
                store[i] = o * (1 - damp) + store[i] * damp;
                b[ci[i]] = input + store[i] * fb;
                if (++ci[i] >= b.Length) ci[i] = 0;
                sum += o;
            }
            for (int i = 0; i < 2; i++)
            {
                var b = ap[i];
                float bo = b[ai[i]];
                b[ai[i]] = sum + bo * 0.5f;
                sum = bo - sum;
                if (++ai[i] >= b.Length) ai[i] = 0;
            }
            return x * dry + sum * wet;
        }
    }

    /// <summary>Gürültü kapısı: konuşma yokken kapanır (bozulma ve tonlama gürültüyü büyütmesin).</summary>
    sealed class Gate : Fx
    {
        readonly float thr, rel, att, envRel; float env, g;
        public Gate(int sr, float thresholdDb = -48)
        {
            thr = (float)Math.Pow(10, thresholdDb / 20);
            att = 1 - (float)Math.Exp(-1.0 / (sr * 0.002)); rel = 1 - (float)Math.Exp(-1.0 / (sr * 0.12)); envRel = (float)Math.Exp(-1.0 / (sr * 0.05));
        }
        public override float Run(float x)
        {
            float a = Math.Abs(x);
            env = a > env ? a : env * envRel;
            float target = env > thr ? 1 : 0;
            g += (target - g) * (target > g ? att : rel);
            return x * g;
        }
    }

    sealed class Gain : Fx
    {
        readonly float g;
        public Gain(double db) { g = (float)Math.Pow(10, db / 20); }
        public override float Run(float x) => x * g;
    }

    /// <summary>Paralel zincirler (ör. iki farklı tonda ses birlikte).</summary>
    sealed class Parallel : Fx
    {
        readonly Fx[][] chains; readonly float[] gains;
        public Parallel(float[] gains, params Fx[][] chains) { this.gains = gains; this.chains = chains; }
        public override float Run(float x)
        {
            float s = 0;
            for (int c = 0; c < chains.Length; c++)
            {
                float v = x;
                foreach (var f in chains[c]) v = f.Run(v);
                s += v * gains[c];
            }
            return s;
        }
    }

    /// <summary>Hazır sesler ve şu an seçili olan.</summary>
    static class VoiceFx
    {
        public sealed class Preset
        {
            public string Id, Name;
            public Func<int, Fx[]> Make;   // örnekleme hızı → zincir (boş: değişmeden geçer)
        }

        static Preset P(string id, string name, Func<int, Fx[]> make) => new Preset { Id = id, Name = name, Make = make };

        public static readonly Preset[] All =
        {
            P("normal", L.T("Normal"), sr => new Fx[0]),
            P("robot", L.T("Robot"), sr => new Fx[] { new Gate(sr), new Lpc(sr, Lpc.Source.Pulse, 98), new ModDelay(sr, 5, 0, 0, 0.45f, 0.75f, 0.45f), new Gain(0.5) }),
            P("kadin", L.T("Kadın"), sr => new Fx[] { new Psola(sr, 7, 1.17), Biquad.Highpass(sr, 150), Biquad.Shelf(sr, 4000, 2, true), new Gain(2.5) }),
            P("kalin", L.T("Kalın"), sr => new Fx[] { new Psola(sr, -4, 0.88), Biquad.Shelf(sr, 220, 2, false) }),
            P("ince", L.T("İnce"), sr => new Fx[] { new Psola(sr, 3, 1.25), Biquad.Highpass(sr, 250), Biquad.Peak(sr, 1600, 1.2, 3), new Gain(1) }),
            P("cocuk", L.T("Çocuk"), sr => new Fx[] { new Psola(sr, 9, 1.3), Biquad.Highpass(sr, 250), new Gain(3) }),
            P("sincap", L.T("Sincap"), sr => new Fx[] { new Psola(sr, 12, 1.55), Biquad.Highpass(sr, 300), new Gain(3) }),
            P("dev", L.T("Dev"), sr => new Fx[] { new Psola(sr, -7, 0.78), Biquad.Shelf(sr, 200, 3, false), new Verb(sr, 0.3f, 0.15f), new Gain(-0.5) }),
            P("canavar", L.T("Canavar"), sr => new Fx[] { new Gate(sr), new Psola(sr, -10, 0.72), new Tremolo(sr, 33, 0.35f), new Drive(3), Biquad.Lowpass(sr, 3400), new Verb(sr, 0.4f, 0.18f), new Gain(-7) }),
            P("iblis", L.T("İblis"), sr => new Fx[] { new Gate(sr), new Parallel(new[] { 0.8f, 0.55f }, new Fx[] { new Psola(sr, -12, 0.7) }, new Fx[] { new Psola(sr, -5, 0.85) }), new Drive(2), new Verb(sr, 0.6f, 0.28f), new Gain(-6.5) }),
            P("lejyon", L.T("Lejyon"), sr => new Fx[] { new Gate(sr), new Parallel(new[] { 0.6f, 0.5f, 0.45f }, new Fx[] { new Psola(sr, -12, 0.75) }, new Fx[] { new Psola(sr, 0.15, 1) }, new Fx[] { new Psola(sr, 7, 1.12) }), new Verb(sr, 0.7f, 0.3f), new Gain(-1) }),
            P("fisilti", L.T("Fısıltı"), sr => new Fx[] { new Gate(sr), new Lpc(sr, Lpc.Source.Noise), Biquad.Highpass(sr, 200), new Gain(2) }),
            P("autotune", L.T("Autotune"), sr => new Fx[] { new Psola(sr, 0, 1, Scale.Snap()), new Verb(sr, 0.3f, 0.12f) }),
            P("robotsarki", L.T("Şarkıcı robot"), sr => new Fx[] { new Gate(sr), new Lpc(sr, Lpc.Source.Tuned, tune: Scale.Snap()), new ModDelay(sr, 12, 4, 0.8, 0, 0.8f, 0.5f), new Gain(1) }),
            P("sualti", L.T("Su altı"), sr => new Fx[] { new ModDelay(sr, 4, 3, 1.3, 0, 0, 1), new Wobble(sr, 520, 260, 0.7), new Verb(sr, 0.5f, 0.35f, 0.6f), new Gain(-1) }),
            P("telsiz", L.T("Telsiz"), sr => new Fx[] { new Gate(sr), Biquad.Highpass(sr, 500), Biquad.Lowpass(sr, 2800), new Drive(5), new Crush(8, 3), new Gain(-6) }),
            P("megafon", L.T("Megafon"), sr => new Fx[] { Biquad.Highpass(sr, 650), Biquad.Lowpass(sr, 4000), Biquad.Peak(sr, 1800, 1, 6), new Drive(3), new Gain(-5) }),
            P("telefon", L.T("Telefon"), sr => new Fx[] { Biquad.Highpass(sr, 400), Biquad.Lowpass(sr, 3200), new Crush(12, Math.Max(1, sr / 8000)), new Gain(4.5) }),
            P("magara", L.T("Mağara"), sr => new Fx[] { new Echo(sr, 300, 0.45f, 0.5f), new Verb(sr, 0.9f, 0.4f, 0.2f), new Gain(-2) }),
            P("uzayli", L.T("Uzaylı"), sr => new Fx[] { new Psola(sr, 3, 1.35), new Ring(sr, 320, 0.25f), new ModDelay(sr, 2, 2, 0.25, 0.6f, 0.6f, 0.6f), new Gain(3) }),
            P("hayalet", L.T("Hayalet"), sr => new Fx[] { new Gate(sr), new Lpc(sr, Lpc.Source.Noise), Biquad.Highpass(sr, 300), new ModDelay(sr, 3, 2, 0.5, 0, 0, 1), new Tremolo(sr, 0.8, 0.4f), new Verb(sr, 1, 0.7f, 0.2f, 0.6f), new Gain(6.5) }),
            P("yasli", L.T("Yaşlı"), sr => new Fx[] { new Psola(sr, -1.5, 0.95, vibratoHz: 5.5, vibratoSt: 0.45), Biquad.Highpass(sr, 220), Biquad.Lowpass(sr, 3800), new Gain(2) }),
            P("sarhos", L.T("Sarhoş"), sr => new Fx[] { new Psola(sr, -1, 0.97, vibratoHz: 0.3, vibratoSt: 1.3), new Echo(sr, 120, 0.2f, 0.2f) }),
        };

        public static Preset Find(string id) => All.FirstOrDefault(p => p.Id == id) ?? All[0];

        static volatile string current = "normal";
        /// <summary>Seçili ses ("normal": değişmeden). Değişince her işleyici zincirini yeniden kurar.</summary>
        public static string Current { get => current; set => current = Find(value).Id; }

        /// <summary>Yayına giden mikrofona da uygulansın mı (ses değiştirici açık ve "yayında da" seçiliyse).</summary>
        public static volatile bool OnStream;
    }

    /// <summary>Bir ses akışına seçili efekti uygular; seçim değişince zinciri yeniden kurar.</summary>
    sealed class VoiceRunner
    {
        Fx[] chain = new Fx[0];
        string id;
        int rate;
        float[] mono = new float[0];

        /// <summary>Tek kanal, yerinde.</summary>
        public void Mono(float[] buf, int n, int sampleRate)
        {
            string want = VoiceFx.Current;
            if (want != id || sampleRate != rate) { id = want; rate = sampleRate; chain = VoiceFx.Find(want).Make(sampleRate); }
            if (chain.Length == 0) return;
            var c = chain;
            for (int i = 0; i < n; i++)
            {
                float v = buf[i];
                for (int k = 0; k < c.Length; k++) v = c[k].Run(v);
                // Yumuşak tavan: efektler (yankı, bozulma) sesi yükseltse de kırpılmasın.
                float a = Math.Abs(v);
                if (a > 0.8f) v = Math.Sign(v) * (0.8f + 0.2f * (float)Math.Tanh((a - 0.8f) / 0.2f));
                buf[i] = v;
            }
        }

        /// <summary>Çok kanallı (iç içe) ses: kanallar tek sese indirilir, efekt uygulanır, her kanala geri yazılır.</summary>
        public void Interleaved(float[] buf, int frames, int channels, int sampleRate)
        {
            if (VoiceFx.Current == "normal") return;
            if (mono.Length < frames) mono = new float[frames];
            for (int f = 0; f < frames; f++)
            {
                float s = 0;
                for (int c = 0; c < channels; c++) s += buf[f * channels + c];
                mono[f] = s / channels;
            }
            Mono(mono, frames, sampleRate);
            for (int f = 0; f < frames; f++)
                for (int c = 0; c < channels; c++) buf[f * channels + c] = mono[f];
        }
    }

    /// <summary>
    /// Bir çıkış cihazına düşük gecikmeyle tek kanallı ses çalar. Kaynak hızı farklıysa doğrusal yeniden örnekler.
    /// Mikrofonun ve cihazın saatleri ayrı olduğundan tampon doluluğu izlenir: boşalırsa kısa sessizlikle doldurulur,
    /// fazla dolarsa (gecikme birikmesin) fazlası atılır.
    /// </summary>
    sealed class RenderOut : IDisposable
    {
        readonly IAudioClient client;
        readonly IAudioRenderClient render;
        readonly int channels, rate, srcRate;
        readonly bool isFloat;
        readonly uint size;
        const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
        float[] res = new float[0], outF = new float[0];
        short[] outS = new short[0];
        double resPos; float resLast;
        public readonly string Name;

        public RenderOut(IMMDevice dev, int srcRate)
        {
            this.srcRate = srcRate;
            try { Name = AudioDevices.NameOf(dev); } catch { Name = "?"; }
            var iid = typeof(IAudioClient).GUID;
            dev.Activate(ref iid, 23, IntPtr.Zero, out object o);
            client = (IAudioClient)o;
            client.GetMixFormat(out IntPtr fmt);
            WaveFmt.Read(fmt, out channels, out rate, out isFloat);
            client.Initialize(0, 0, 1000000, 0, fmt, IntPtr.Zero); // 100 ms tampon; doluluk ~20 ms tutulur
            Marshal.FreeCoTaskMem(fmt);
            client.GetBufferSize(out size);
            var rid = typeof(IAudioRenderClient).GUID;
            client.GetService(ref rid, out object rs);
            render = (IAudioRenderClient)rs;
            client.Start();
        }

        public void Write(float[] mono, int n)
        {
            float[] src = mono;
            int m = n;
            if (rate != srcRate)
            {
                double step = (double)srcRate / rate;
                int cap = (int)(n / step) + 2;
                if (res.Length < cap) res = new float[cap];
                m = 0;
                while (true)
                {
                    int i0 = (int)Math.Floor(resPos);
                    if (i0 + 1 >= n) break;
                    float a = i0 < 0 ? resLast : mono[i0], t = (float)(resPos - i0);
                    res[m++] = a + (mono[i0 + 1] - a) * t;
                    resPos += step;
                }
                resPos -= n; resLast = mono[n - 1];
                src = res;
            }
            client.GetCurrentPadding(out uint pad);
            uint target = (uint)(rate / 50), max = (uint)(rate * 3 / 50); // 20 ms hedef, 60 ms üstü atılır
            if (pad == 0) Put(null, 0, target);                            // boşaldı (başlangıç ya da mikrofon geride): sessizlikle tampon
            else if (pad > max) return;                                    // cihaz geride: gecikme birikmesin
            client.GetCurrentPadding(out pad);
            Put(src, 0, (uint)Math.Min(m, size - pad));
        }

        void Put(float[] src, int off, uint frames)
        {
            if (frames == 0) return;
            render.GetBuffer(frames, out IntPtr data);
            int total = (int)frames * channels;
            if (src == null) { render.ReleaseBuffer(frames, AUDCLNT_BUFFERFLAGS_SILENT); return; }
            if (isFloat)
            {
                if (outF.Length < total) outF = new float[total];
                for (int f = 0; f < frames; f++) for (int c = 0; c < channels; c++) outF[f * channels + c] = src[off + f];
                Marshal.Copy(outF, 0, data, total);
            }
            else
            {
                if (outS.Length < total) outS = new short[total];
                for (int f = 0; f < frames; f++) for (int c = 0; c < channels; c++) outS[f * channels + c] = (short)(Math.Max(-1f, Math.Min(1f, src[off + f])) * 32767);
                Marshal.Copy(outS, 0, data, total);
            }
            render.ReleaseBuffer(frames, 0);
        }

        public void Dispose() { try { client.Stop(); } catch { } }
    }

    static class WaveFmt
    {
        static readonly Guid FloatSub = new Guid("00000003-0000-0010-8000-00aa00389b71");

        public static void Read(IntPtr fmt, out int channels, out int rate, out bool isFloat)
        {
            ushort tag = (ushort)Marshal.ReadInt16(fmt, 0);
            channels = Marshal.ReadInt16(fmt, 2);
            rate = Marshal.ReadInt32(fmt, 4);
            int bits = Marshal.ReadInt16(fmt, 14);
            if (tag == 0xFFFE)
            {
                var g = new byte[16];
                Marshal.Copy(fmt + 24, g, 0, 16);
                isFloat = new Guid(g) == FloatSub;
            }
            else isFloat = tag == 3;
            if (!(isFloat && bits == 32) && !(!isFloat && bits == 16))
                throw new NotSupportedException(L.F("Desteklenmeyen ses biçimi ({0} bit).", bits));
        }
    }

    /// <summary>
    /// Mikrofon → efekt → sanal mikrofon (ve istenirse kulaklık). Kendi iş parçacığında, mikrofon paket verdikçe
    /// (olay güdümlü, ~10 ms) çalışır. Cihaz kaybolursa (çıkarıldı) kısa bekleyip yeniden bağlanır.
    /// </summary>
    sealed class VoiceChanger : IDisposable
    {
        const int AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
        const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

        readonly string micId, outId;
        volatile bool running;
        Thread thread;
        readonly ManualResetEventSlim ready = new ManualResetEventSlim();
        Exception error;

        public volatile bool Monitor;          // kendi sesini varsayılan çıkıştan (kulaklık) duy
        public volatile float InLevel, OutLevel; // son paketlerin zirvesi (arayüz göstergesi)
        public volatile string Problem;        // çalışırken çıkan sorun (arayüz durum satırı), yoksa null

        public VoiceChanger(string micId, string outId) { this.micId = micId; this.outId = outId; }

        /// <summary>Sanal kablo olabilecek çıkışlar (kablonun "hoparlör" ucu), en olası başta.</summary>
        public static List<KeyValuePair<string, string>> VirtualOutputs()
        {
            var all = AudioDevices.Endpoints(true);
            int Rank(string n)
            {
                n = n.ToLowerInvariant();
                if (n.Contains("cable input") || n.Contains("vb-audio")) return 0;
                if (n.Contains("voicemeeter")) return 1;
                if (n.Contains("virtual") || n.Contains("sanal")) return 2;
                if (n.Contains("steam streaming microphone")) return 3;
                return 9;
            }
            return all.Where(d => Rank(d.Value) < 9).OrderBy(d => Rank(d.Value)).ToList();
        }

        /// <summary>Kablonun öbür ucu: Valorant'ta seçilecek giriş cihazının adı.</summary>
        public static string CaptureSideOf(string renderName)
        {
            if (string.IsNullOrEmpty(renderName)) return "";
            int a = renderName.IndexOf('('), b = renderName.LastIndexOf(')');
            string inner = a >= 0 && b > a ? renderName.Substring(a, b - a + 1) : null;
            var caps = AudioDevices.Endpoints(false);
            if (renderName.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var c = caps.FirstOrDefault(x => x.Value.IndexOf("CABLE Output", StringComparison.OrdinalIgnoreCase) >= 0);
                if (c.Value != null) return c.Value;
            }
            if (inner != null)
            {
                var c = caps.FirstOrDefault(x => x.Value.EndsWith(inner, StringComparison.OrdinalIgnoreCase));
                if (c.Value != null) return c.Value;
            }
            return renderName;
        }

        public void Start()
        {
            running = true;
            thread = new Thread(Run) { IsBackground = true, Name = "voice", Priority = ThreadPriority.AboveNormal };
            thread.Start();
            if (!ready.Wait(5000)) throw new Exception(L.T("Ses cihazı yanıt vermedi."));
            if (error != null) throw error;
        }

        void Run()
        {
            IMMDeviceEnumerator en;
            try { en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom(); }
            catch (Exception e) { error = e; ready.Set(); return; }
            bool opened = false;
            var runner = new VoiceRunner();
            using (var evt = new AutoResetEvent(false))
                while (running)
                {
                    IAudioClient cap = null;
                    RenderOut outDev = null, mon = null;
                    try
                    {
                        IMMDevice mic;
                        if (string.IsNullOrEmpty(micId)) en.GetDefaultAudioEndpoint(EDataFlow.Capture, 0, out mic);
                        else en.GetDevice(micId, out mic);
                        var iid = typeof(IAudioClient).GUID;
                        mic.Activate(ref iid, 23, IntPtr.Zero, out object o);
                        cap = (IAudioClient)o;
                        cap.GetMixFormat(out IntPtr fmt);
                        WaveFmt.Read(fmt, out int ch, out int rate, out bool isFloat);
                        cap.Initialize(0, AUDCLNT_STREAMFLAGS_EVENTCALLBACK, 200000, 0, fmt, IntPtr.Zero);
                        Marshal.FreeCoTaskMem(fmt);
                        cap.SetEventHandle(evt.SafeWaitHandle.DangerousGetHandle());
                        var cid = typeof(IAudioCaptureClient).GUID;
                        cap.GetService(ref cid, out object cs);
                        var capture = (IAudioCaptureClient)cs;

                        en.GetDevice(outId, out IMMDevice vdev);
                        outDev = new RenderOut(vdev, rate);
                        cap.Start();
                        Problem = null;
                        if (!opened) { opened = true; ready.Set(); }

                        var raw = new float[0];
                        var shorts = new short[0];
                        var mono = new float[0];
                        var sent = new float[0];
                        while (running)
                        {
                            evt.WaitOne(100);
                            // Kulaklıktan dinleme: açıldıysa varsayılan çıkışa ikinci bir kopya.
                            if (Monitor && mon == null)
                                try { en.GetDefaultAudioEndpoint(EDataFlow.Render, 0, out IMMDevice md); mon = new RenderOut(md, rate); } catch { }
                            else if (!Monitor && mon != null) { mon.Dispose(); mon = null; }

                            while (true)
                            {
                                capture.GetNextPacketSize(out uint packet);
                                if (packet == 0) break;
                                capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _);
                                int n = (int)frames, total = n * ch;
                                if (raw.Length < total) { raw = new float[total]; shorts = new short[total]; }
                                if (mono.Length < n) { mono = new float[n]; sent = new float[n]; }
                                if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0) Array.Clear(raw, 0, total);
                                else if (isFloat) Marshal.Copy(data, raw, 0, total);
                                else { Marshal.Copy(data, shorts, 0, total); for (int i = 0; i < total; i++) raw[i] = shorts[i] / 32768f; }
                                capture.ReleaseBuffer(frames);

                                float inPk = 0, outPk = 0;
                                for (int f = 0; f < n; f++)
                                {
                                    float s = 0;
                                    for (int c = 0; c < ch; c++) s += raw[f * ch + c];
                                    mono[f] = s / ch;
                                    inPk = Math.Max(inPk, Math.Abs(mono[f]));
                                }
                                runner.Mono(mono, n, rate);
                                for (int f = 0; f < n; f++) outPk = Math.Max(outPk, Math.Abs(mono[f]));
                                InLevel = Math.Max(inPk, InLevel * 0.85f);
                                OutLevel = Math.Max(outPk, OutLevel * 0.85f);
                                outDev.Write(mono, n);
                                mon?.Write(mono, n);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        if (!opened) { error = e; ready.Set(); return; }
                        Problem = e.Message;
                        for (int i = 0; i < 10 && running; i++) Thread.Sleep(100);
                    }
                    finally
                    {
                        try { cap?.Stop(); } catch { }
                        outDev?.Dispose();
                        mon?.Dispose();
                    }
                }
        }

        public void Dispose()
        {
            running = false;
            thread?.Join(1000);
        }
    }
}
