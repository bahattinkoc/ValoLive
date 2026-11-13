using System;
using System.Linq;

namespace ValoLive
{
    // ---------- Ses değiştirici: ortamlar ve ses efektleri ----------

    /// <summary>Karmaşık FFT (radix-2, yerinde; ters dönüşüm 1/n ile ölçekli).</summary>
    static class Fft
    {
        public static void Run(double[] re, double[] im, bool inverse)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { double t = re[i]; re[i] = re[j]; re[j] = t; t = im[i]; im[i] = im[j]; im[j] = t; }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = 2 * Math.PI / len * (inverse ? 1 : -1), wr = Math.Cos(ang), wi = Math.Sin(ang);
                int half = len >> 1;
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int k = 0; k < half; k++)
                    {
                        int a = i + k, b = a + half;
                        double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti; re[a] += tr; im[a] += ti;
                        double nr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = nr;
                    }
                }
            }
            if (inverse) for (int i = 0; i < n; i++) { re[i] /= n; im[i] /= n; }
        }
    }

    enum Room { Oda, Banyo, Salon, Magara, Stadyum }

    /// <summary>
    /// Oda yankısı (konvolüsyon): odanın "dürtü yanıtı" sesle bölüm bölüm FFT üzerinden çarpılır. Yanıtlar burada
    /// sentezlenir: ilk yansımalar (duvarlar) + tizleri baslardan hızlı sönen yoğun kuyruk. Algoritmik yankıdan çok
    /// daha inandırıcı; ıslak sese blok kadar (5-21 ms) gecikme ekler, kuru ses gecikmesiz geçer.
    /// </summary>
    sealed class ConvVerb : Fx
    {
        readonly int B, N, P;
        readonly double[][] hr, hi, xr, xi;
        readonly double[] yr, yi;
        readonly float[] inBlk, prevBlk, outBlk;
        readonly float wet, dry;
        int idx, head;

        public ConvVerb(int sr, Room room, float wet, float dry = 1)
        {
            this.wet = wet; this.dry = dry;
            var ir = Impulse(sr, room);
            B = 256; while (B < 1024 && ir.Length / B > 96) B <<= 1;
            N = 2 * B; P = (ir.Length + B - 1) / B;
            hr = new double[P][]; hi = new double[P][]; xr = new double[P][]; xi = new double[P][];
            for (int p = 0; p < P; p++)
            {
                hr[p] = new double[N]; hi[p] = new double[N]; xr[p] = new double[N]; xi[p] = new double[N];
                for (int k = 0; k < B && p * B + k < ir.Length; k++) hr[p][k] = ir[p * B + k];
                Fft.Run(hr[p], hi[p], false);
            }
            yr = new double[N]; yi = new double[N];
            inBlk = new float[B]; prevBlk = new float[B]; outBlk = new float[B];
        }

        public override float Run(float x)
        {
            float y = outBlk[idx];
            inBlk[idx] = x;
            if (++idx == B) { idx = 0; Process(); }
            return x * dry + y * wet;
        }

        void Process()
        {
            // Üst üste kaydırarak kaydet (overlap-save), eş bölümler; sinyal gerçel: yarım spektrum yeter.
            head = (head + P - 1) % P;
            var r = xr[head]; var im = xi[head];
            for (int k = 0; k < B; k++) { r[k] = prevBlk[k]; r[k + B] = inBlk[k]; }
            Array.Clear(im, 0, N);
            Fft.Run(r, im, false);
            Array.Copy(inBlk, prevBlk, B);
            Array.Clear(yr, 0, N); Array.Clear(yi, 0, N);
            for (int p = 0; p < P; p++)
            {
                int q = (head + p) % P;
                double[] ar = xr[q], ai = xi[q], br = hr[p], bi = hi[p];
                for (int k = 0; k <= B; k++)
                {
                    yr[k] += ar[k] * br[k] - ai[k] * bi[k];
                    yi[k] += ar[k] * bi[k] + ai[k] * br[k];
                }
            }
            for (int k = 1; k < B; k++) { yr[N - k] = yr[k]; yi[N - k] = -yi[k]; }
            Fft.Run(yr, yi, true);
            for (int k = 0; k < B; k++) outBlk[k] = (float)yr[k + B];
        }

        /// <summary>Sentetik oda yanıtı; enerjisi 1'e normalleştirilir (ıslak seviye = wet).</summary>
        static float[] Impulse(int sr, Room room)
        {
            // t60: sönme süresi · hf: tizlerin sönme oranı · pre: ilk yansıma gecikmesi · spread: ilk yansımaların yayıldığı süre
            double t60, hf, pre, spread; int early; double[] echoes = new double[0];
            switch (room)
            {
                case Room.Oda: t60 = 0.45; hf = 0.5; pre = 0.003; spread = 0.025; early = 10; break;
                case Room.Banyo: t60 = 1.3; hf = 0.85; pre = 0.002; spread = 0.015; early = 14; break;
                case Room.Salon: t60 = 2.2; hf = 0.55; pre = 0.015; spread = 0.06; early = 12; break;
                case Room.Magara: t60 = 3.5; hf = 0.3; pre = 0.03; spread = 0.25; early = 7; break;
                default: t60 = 2.6; hf = 0.4; pre = 0.04; spread = 0.12; early = 8; echoes = new[] { 0.28, 0.52 }; break;
            }
            int len = (int)(sr * Math.Min(4, t60 * 1.1 + pre + spread));
            var ir = new double[len];
            var rnd = new Random((int)room * 977 + 3);
            Func<double> gauss = () => (rnd.NextDouble() + rnd.NextDouble() + rnd.NextDouble() - 1.5) * 2;
            // Kuyruk: iki bant, farklı sönme; ilk yansımalardan sonra yumuşakça girer.
            double lp = 0, kLp = 1 - Math.Exp(-2 * Math.PI * 1500.0 / sr), onset = pre + spread * 0.5;
            for (int i = 0; i < len; i++)
            {
                double t = i / (double)sr, n = gauss();
                lp += (n - lp) * kLp;
                double lo = lp * Math.Exp(-6.91 * t / t60), hiB = (n - lp) * Math.Exp(-6.91 * t / (t60 * hf));
                double fade = t < pre ? 0 : Math.Min(1, (t - pre) / Math.Max(0.005, onset - pre));
                ir[i] = (lo + 0.6 * hiB) * fade;
            }
            // İlk yansımalar: dağınık, sönen, işareti rastgele darbeler (biraz yayılmış).
            double tailE = Math.Sqrt(ir.Sum(v => v * v));
            for (int e = 0; e < early; e++)
            {
                double t = pre + spread * Math.Pow(rnd.NextDouble(), 0.7);
                double g = tailE * 0.35 * Math.Exp(-3 * (t - pre) / Math.Max(spread, 0.01)) * (rnd.Next(2) == 0 ? -1 : 1);
                int at = (int)(t * sr);
                for (int k = -3; k <= 3; k++) if (at + k >= 0 && at + k < len) ir[at + k] += g * (1 - Math.Abs(k) / 4.0);
            }
            // Stadyum: tribünlerden dönen belirgin yankılar.
            foreach (var t in echoes)
            {
                int at = (int)(t * sr);
                for (int k = 0; k < sr * 0.03 && at + k < len; k++) ir[at + k] += tailE * 0.5 * gauss() * Math.Exp(-k / (sr * 0.008)) * Math.Exp(-2 * t);
            }
            double norm = Math.Sqrt(ir.Sum(v => v * v));
            return ir.Select(v => (float)(v / norm)).ToArray();
        }
    }

    /// <summary>Konuşma var mı: hızlı tepki, gecikmeli bırakma (efektlerin "konuşma başladı/bitti" anları).</summary>
    sealed class Talk
    {
        readonly float thr, rel; float env;
        readonly int hangN; int quiet;
        public bool On;
        public Talk(int sr, double thresholdDb = -42, double hangSec = 0.3)
        {
            thr = (float)Math.Pow(10, thresholdDb / 20); rel = (float)Math.Exp(-1.0 / (sr * 0.03)); hangN = (int)(sr * hangSec);
        }
        /// <summary>+1: konuşma başladı, -1: bitti, 0: değişmedi.</summary>
        public int Step(float x)
        {
            float a = Math.Abs(x);
            env = a > env ? a : env * rel;
            if (env > thr) { quiet = 0; if (!On) { On = true; return 1; } }
            else if (On && ++quiet > hangN) { On = false; return -1; }
            return 0;
        }
    }

    /// <summary>Telsiz sesleri: konuşma başında "kşşt", bitince onay bip'i ve kısa cızırtı; konuşurken hafif parazit.</summary>
    sealed class Radio : Fx
    {
        readonly int sr, burstLen, beepLen, tailLen;
        readonly Talk talk;
        readonly Biquad bp1, bp2;
        readonly Random rnd = new Random(5);
        int burst = int.MaxValue, beep = int.MaxValue, tail = int.MaxValue;
        double ph;

        public Radio(int sr)
        {
            this.sr = sr; talk = new Talk(sr, -40, 0.25);
            bp1 = Biquad.Highpass(sr, 900); bp2 = Biquad.Lowpass(sr, 4200);
            burstLen = (int)(sr * 0.13); beepLen = (int)(sr * 0.09); tailLen = (int)(sr * 0.16);
        }

        public override float Run(float x)
        {
            int ev = talk.Step(x);
            if (ev > 0) burst = 0;
            if (ev < 0) { beep = 0; tail = -beepLen; }
            double hiss = 0, s = 0;
            if (burst < burstLen) { hiss += 0.22 * (1 - burst / (double)burstLen); burst++; }
            if (tail < tailLen) { if (tail >= 0) hiss += 0.18 * (1 - tail / (double)tailLen); tail++; }
            if (talk.On) hiss += 0.012;
            if (beep < beepLen)
            {
                ph += 2 * Math.PI * 1350 / sr;
                double env = Math.Min(1, Math.Min(beep, beepLen - beep) / (sr * 0.004));
                s = 0.16 * env * Math.Sin(ph);
                beep++;
            }
            float h = bp2.Run(bp1.Run((float)((rnd.NextDouble() * 2 - 1) * hiss)));
            return x + (float)s + h * 1.6f;
        }
    }

    /// <summary>Su altı baloncukları: konuşurken ara ara yükselen kısa "blup" sesleri.</summary>
    sealed class Bubbles : Fx
    {
        sealed class Bub { public double Ph, F, Rise; public int N, Len; public float Amp; }
        readonly int sr;
        readonly double rate;
        readonly Talk talk;
        readonly Random rnd = new Random(9);
        readonly Bub[] live = new Bub[4];

        public Bubbles(int sr, double perSecond = 4) { this.sr = sr; talk = new Talk(sr, -42, 0.4); rate = perSecond / sr; }

        public override float Run(float x)
        {
            talk.Step(x);
            if (talk.On && rnd.NextDouble() < rate)
                for (int i = 0; i < live.Length; i++)
                    if (live[i] == null)
                    {
                        int len = (int)(sr * (0.025 + 0.05 * rnd.NextDouble()));
                        live[i] = new Bub { F = 260 + 500 * rnd.NextDouble(), Rise = Math.Pow(2.6, 1.0 / len), Len = len, Amp = (float)(0.03 + 0.06 * rnd.NextDouble()) };
                        break;
                    }
            double s = 0;
            for (int i = 0; i < live.Length; i++)
            {
                var b = live[i];
                if (b == null) continue;
                b.Ph += 2 * Math.PI * b.F / sr; b.F *= b.Rise;
                double t = b.N / (double)b.Len;
                s += b.Amp * Math.Sin(b.Ph) * Math.Sin(Math.PI * Math.Min(1, t * 4)) * (1 - t);
                if (++b.N >= b.Len) live[i] = null;
            }
            return x + (float)s;
        }
    }
}
