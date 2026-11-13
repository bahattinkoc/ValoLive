using System;

namespace ValoLive
{
    // ---------- Gerçekçi ses motorları ----------
    // İnsan sesi iki parçadan oluşur: gırtlağın titreşimi (perde) ve ağız-boğaz boşluğunun rengi (formantlar).
    // Basit ton kaydırma ikisini birlikte kaydırır (teyp hızlandırma: "kadın" yerine sincap). Buradaki motorlar onları ayırır:
    // Psola perdeyi ve rengi ayrı ayrı değiştirir; Lpc rengi korur, titreşimi gürültü (fısıltı) ya da sabit/notaya
    // oturtulmuş darbelerle (robot, şarkıcı robot) değiştirir.

    /// <summary>
    /// Perde takibi (YIN): sesi ~12 kHz'e indirip her 8 ms'de son ~45 ms'nin titreşim periyodunu bulur.
    /// Period tam hızda örnek cinsinden; Voiced = sesli harf/ünlü (perdesi olan ses) mi.
    /// </summary>
    sealed class PitchTrack
    {
        readonly int dec, mask, win, minLag, maxLag, hop;
        readonly Biquad aa1, aa2;
        readonly float[] buf, x;
        readonly double[] d;
        readonly double[] last = new double[3];
        readonly float gate;
        int pos, phase, sinceHop, filled;
        public double Period;
        public bool Voiced;

        public PitchTrack(int sr, double minHz = 70, double maxHz = 500)
        {
            dec = Math.Max(1, (int)Math.Round(sr / 12000.0));
            double ds = sr / (double)dec;
            aa1 = Biquad.Lowpass(sr, ds * 0.4, 0.54); aa2 = Biquad.Lowpass(sr, ds * 0.4, 1.31);
            minLag = Math.Max(2, (int)(ds / maxHz)); maxLag = (int)Math.Ceiling(ds / minHz) + 1;
            win = (int)(ds * 0.03); hop = Math.Max(1, (int)(ds * 0.008));
            int n = 1; while (n < win + maxLag + 4) n <<= 1;
            buf = new float[n]; mask = n - 1;
            x = new float[win + maxLag + 1]; d = new double[maxLag + 2];
            gate = (float)Math.Pow(10, -50 / 20.0);
            Period = sr / 120.0;
            for (int i = 0; i < 3; i++) last[i] = Period;
        }

        public void Push(float s)
        {
            float v = aa2.Run(aa1.Run(s));
            if (++phase < dec) return;
            phase = 0;
            buf[pos++ & mask] = v;
            if (filled < buf.Length) filled++;
            if (++sinceHop < hop) return;
            sinceHop = 0;
            if (filled >= win + maxLag) Analyze();
        }

        void Analyze()
        {
            int n = win + maxLag;
            for (int i = 0; i < n; i++) x[i] = buf[(pos - n + i) & mask];
            double e = 0;
            for (int i = maxLag; i < n; i++) e += x[i] * x[i];
            if (Math.Sqrt(e / win) < gate) { Voiced = false; return; }
            // Fark fonksiyonu ve birikimli ortalamayla normalleştirme (YIN 2-3. adım).
            double run = 0;
            d[0] = 1;
            for (int t = 1; t <= maxLag; t++)
            {
                double s = 0;
                for (int j = 0; j < win; j++) { double df = x[j] - x[j + t]; s += df * df; }
                run += s;
                d[t] = run > 0 ? s * t / run : 1;
            }
            int best = -1;
            for (int t = minLag; t < maxLag; t++)
                if (d[t] < 0.15) { while (t + 1 < maxLag && d[t + 1] < d[t]) t++; best = t; break; }
            if (best < 0)
            {
                best = minLag;
                for (int t = minLag; t < maxLag; t++) if (d[t] < d[best]) best = t;
                if (d[best] > 0.3) { Voiced = false; return; }
            }
            double tau = best;
            if (best > 1 && best < maxLag)
            {
                double a = d[best - 1], b = d[best], c = d[best + 1], den = a - 2 * b + c;
                if (den > 1e-9) tau += 0.5 * (a - c) / den;
            }
            // Üç tahminin ortancası: tek tük oktav hatalarını yutar.
            last[0] = last[1]; last[1] = last[2]; last[2] = tau * dec;
            double p0 = last[0], p1 = last[1], p2 = last[2];
            Period = Math.Max(Math.Min(p0, p1), Math.Min(Math.Max(p0, p1), p2));
            Voiced = true;
        }
    }

    /// <summary>
    /// Perde ve renk ayrı (TD-PSOLA): ses periyot periyot "tanecik"lere bölünür. Taneciklerin aralığı perdeyi,
    /// her taneciğin sıkıştırılıp genişletilmesi rengi (formantları) belirler. Kadın sesi = perde yukarı + renk biraz yukarı;
    /// sincap gibi değil, gerçekten daha kısa bir ses yolundan çıkmış gibi duyulur. Sessiz harfler (s, ş, f) perdesiz,
    /// yalnız renkleri kayar. Sabit gecikme: ~40 ms (bütün Psola'larda aynı, paralel kullanılabilir).
    /// </summary>
    sealed class Psola : Fx
    {
        const double MinHz = 80, MaxHz = 500, MinFormant = 0.7, MaxFormant = 1.6;
        readonly int sr, inMask, outMask;
        readonly float[] inBuf, acc;
        readonly PitchTrack track;
        readonly double baseBeta, alpha, tMin, tMax, hMax, lat, unv;
        readonly double vibInc, vibDepth;
        readonly Func<double, double> tune;
        readonly Random rnd = new Random(7);
        long n = -1;
        double nextS, a, vibPh, jitter;
        bool wasVoiced;

        /// <param name="semitones">perde (yarım ton)</param>
        /// <param name="formant">renk oranı (1 = aynı, 1.2 = daha kısa ses yolu, 0.8 = daha iri)</param>
        /// <param name="tune">giriş perdesi (Hz) → istenen perde (Hz); autotune için (semitones'un üstüne)</param>
        /// <param name="vibratoHz">titreme hızı</param>
        /// <param name="vibratoSt">titreme derinliği (yarım ton); 0'dan büyükse küçük rastgele sapma da eklenir</param>
        public Psola(int sr, double semitones, double formant = 1, Func<double, double> tune = null, double vibratoHz = 0, double vibratoSt = 0)
        {
            this.sr = sr; this.tune = tune;
            baseBeta = Math.Pow(2, semitones / 12);
            alpha = Math.Max(MinFormant, Math.Min(MaxFormant, formant));
            tMin = sr / MaxHz; tMax = sr / MinHz;
            hMax = tMax / MinFormant;
            unv = sr * 0.005;
            lat = hMax + 1.5 * tMax + 8;
            vibInc = 2 * Math.PI * vibratoHz / sr; vibDepth = vibratoSt;
            int size = 1; while (size < lat + 3 * tMax + 16) size <<= 1;
            inBuf = new float[size]; inMask = size - 1;
            size = 1; while (size < 4 * hMax + 16) size <<= 1;
            acc = new float[size]; outMask = size - 1;
            track = new PitchTrack(sr, 70, MaxHz);
            nextS = hMax;
        }

        public override float Run(float x)
        {
            n++;
            inBuf[n & inMask] = x;
            track.Push(x);
            if (vibInc > 0) { vibPh += vibInc; if (vibPh > 2 * Math.PI) vibPh -= 2 * Math.PI; }
            while (nextS <= n + hMax) Emit();
            int j = (int)(n & outMask);
            float y = acc[j];
            acc[j] = 0;
            return y;
        }

        void Emit()
        {
            double s = nextS, target = s - lat;
            bool voiced = track.Voiced;
            if (!voiced)
            {
                // Perdesiz: yarım örtüşen sabit tanecikler (toplamları tam 1), içerikleri renk oranında yeniden örneklenir.
                Grain(s, target, unv, alpha, 1);
                nextS = s + unv;
                wasVoiced = false;
                return;
            }
            double t0 = Math.Max(tMin, Math.Min(tMax, track.Period));
            double beta = baseBeta;
            if (tune != null) { double f = sr / t0; beta *= tune(f) / f; }
            if (vibDepth > 0)
            {
                jitter += (rnd.NextDouble() - 0.5) * 0.15; jitter *= 0.97;
                beta *= Math.Pow(2, (vibDepth * Math.Sin(vibPh) + jitter * vibDepth) / 12);
            }
            if (!wasVoiced) a = target;
            else
            {
                // Tam periyot adımları + benzerlik araması: art arda tanecikler aynı evrede kalır. Ölçülen gerçek periyot
                // (tahminden biraz farklı olabilir) çıkıştaki aralığı da belirler; yoksa üst üste binen tanecikler birbirini söndürür.
                double m = Math.Round((target - a) / t0);
                if (m != 0)
                {
                    double prev = a; a += m * t0;
                    while (a + t0 + 3 > n) { a -= t0; m--; }
                    if (m > 0) { a = Align(prev, a, t0); t0 = Math.Max(t0 * 0.8, Math.Min(t0 * 1.25, (a - prev) / m)); }
                }
            }
            while (a + t0 + 3 > n) a -= t0;
            double h = t0 / alpha;
            Grain(s, a, h, alpha, alpha / beta);
            nextS = s + t0 / beta;
            wasVoiced = true;
        }

        /// <summary>Yeni taneciği öncekiyle aynı evreye oturtur: guess ± çeyrek periyotta en benzer konum.</summary>
        double Align(double prev, double guess, double t0)
        {
            int r = (int)(t0 * 0.25), half = (int)(t0 * 0.5);
            long p0 = (long)Math.Round(prev), g0 = (long)Math.Round(guess);
            double best = guess, bc = double.MinValue;
            for (int dd = -r; dd <= r; dd += 2)
            {
                double c = 0, e = 0;
                for (int i = -half; i < half; i += 2)
                {
                    double v = inBuf[(g0 + dd + i) & inMask];
                    c += v * inBuf[(p0 + i) & inMask]; e += v * v;
                }
                double sc = c / Math.Sqrt(e + 1e-12);
                if (sc > bc) { bc = sc; best = g0 + dd; }
            }
            return best;
        }

        /// <summary>s merkezli, h yarı genişlikli Hann taneciği; girişten center ± h·ratio okur.</summary>
        void Grain(double s, double center, double h, double ratio, double gain)
        {
            int j0 = (int)Math.Ceiling(s - h), j1 = (int)Math.Floor(s + h);
            for (int j = j0; j <= j1; j++)
            {
                double dt = j - s;
                double w = 0.5 + 0.5 * Math.Cos(Math.PI * dt / h);
                acc[j & outMask] += (float)(w * gain * Read(center + dt * ratio));
            }
        }

        /// <summary>Kübik (Hermite) aradeğerleme.</summary>
        double Read(double p)
        {
            long i = (long)Math.Floor(p);
            double f = p - i;
            double xm = inBuf[(i - 1) & inMask], x0 = inBuf[i & inMask], x1 = inBuf[(i + 1) & inMask], x2 = inBuf[(i + 2) & inMask];
            double c1 = 0.5 * (x1 - xm), c2 = xm - 2.5 * x0 + 2 * x1 - 0.5 * x2, c3 = 0.5 * (x2 - xm) + 1.5 * (x0 - x1);
            return ((c3 * f + c2) * f + c1) * f + x0;
        }
    }

    /// <summary>
    /// Kaynak-filtre (LPC) vokoder: her 5 ms'de sesin rengini (ağız şekli) bir süzgeç olarak çıkarır, gırtlak titreşimi
    /// yerine başka bir kaynağı o süzgeçten geçirir. Kelimeler aynen anlaşılır, ses kaynağı değişir:
    /// gürültü → fısıltı/hayalet, sabit darbe → robot, notaya oturtulmuş darbe → şarkıcı robot.
    /// Sessiz harflerde (s, ş, f) her zaman gürültü kullanılır. ~16 kHz'de çalışır; ek gecikmesi yok denecek kadar az.
    /// </summary>
    sealed class Lpc : Fx
    {
        public enum Source { Noise, Pulse, Tuned }
        const int Order = 18;
        const double Emph = 0.9;
        readonly int dec, frame, hopN, mask;
        readonly Biquad d1, d2, u1, u2;
        readonly PitchTrack track;
        readonly Source src;
        readonly double lowRate, pulseHz, noiseMix;
        readonly Func<double, double> tune;
        readonly float[] hist;
        readonly double[] win, r = new double[Order + 1], k = new double[Order + 1], kOld = new double[Order + 1], kNew = new double[Order + 1], g = new double[Order + 1];
        readonly double[] aTmp = new double[Order + 1], aPrev = new double[Order + 1];
        readonly Random rnd = new Random(11);
        int phase, hpos, sinceHop, step;
        double gOld, gNew, prevIn, deEmph, pulsePh, held;
        bool voicedNew;

        /// <param name="pulseHz">Pulse için sabit perde (Hz)</param>
        /// <param name="noiseMix">sesli kısımlarda darbeye karışan gürültü (0-1)</param>
        public Lpc(int sr, Source source, double pulseHz = 100, Func<double, double> tune = null, double noiseMix = 0.08)
        {
            src = source; this.pulseHz = pulseHz; this.tune = tune; this.noiseMix = noiseMix;
            dec = Math.Max(1, (int)Math.Round(sr / 16000.0));
            lowRate = sr / (double)dec;
            double fc = lowRate * 0.42;
            d1 = Biquad.Lowpass(sr, fc, 0.54); d2 = Biquad.Lowpass(sr, fc, 1.31);
            u1 = Biquad.Lowpass(sr, fc, 0.54); u2 = Biquad.Lowpass(sr, fc, 1.31);
            frame = (int)(lowRate * 0.025); hopN = (int)(lowRate * 0.005);
            int size = 1; while (size < frame + 4) size <<= 1;
            hist = new float[size]; mask = size - 1;
            win = new double[frame];
            for (int i = 0; i < frame; i++) win[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (i + 0.5) / frame);
            track = new PitchTrack(sr, 70, 500);
        }

        public override float Run(float x)
        {
            track.Push(x);
            float v = d2.Run(d1.Run(x));
            if (phase == 0)
            {
                // Düşük hızda bir örnek: analiz geçmişine yaz, sentezle.
                double e = v - Emph * prevIn; prevIn = v;
                hist[hpos++ & mask] = (float)e;
                if (++sinceHop >= hopN) { sinceHop = 0; Analyze(); }
                held = Synth() * dec;
            }
            else held = 0;   // sıfır doldurma; ardından gelen alçak geçiren aradeğerler
            if (++phase >= dec) phase = 0;
            return u2.Run(u1.Run((float)held));
        }

        void Analyze()
        {
            // Önceki hedefe tam varılmış say; yeni hedefe bu atlama boyunca doğrusal geçilir (süzgeç kararlı kalır).
            Array.Copy(kNew, kOld, Order + 1); gOld = gNew; step = 0;
            for (int lag = 0; lag <= Order; lag++)
            {
                double s = 0;
                for (int i = lag; i < frame; i++)
                    s += hist[(hpos - frame + i) & mask] * win[i] * hist[(hpos - frame + i - lag) & mask] * win[i - lag];
                r[lag] = s;
            }
            double w2 = 0;
            for (int i = 0; i < frame; i++) w2 += win[i] * win[i];
            if (r[0] < 1e-10) { Array.Clear(kNew, 0, Order + 1); gNew = 0; voicedNew = false; return; }
            r[0] *= 1.0001;
            // Levinson-Durbin → yansıma katsayıları.
            Array.Clear(aTmp, 0, Order + 1); aTmp[0] = 1;
            double err = r[0];
            for (int i = 1; i <= Order; i++)
            {
                double acc = r[i];
                for (int j = 1; j < i; j++) acc += aTmp[j] * r[i - j];
                double ki = -acc / err;
                ki = Math.Max(-0.995, Math.Min(0.995, ki));
                kNew[i] = ki;
                Array.Copy(aTmp, aPrev, i);
                for (int j = 1; j < i; j++) aTmp[j] = aPrev[j] + ki * aPrev[i - j];
                aTmp[i] = ki;
                err *= 1 - ki * ki;
            }
            gNew = Math.Sqrt(Math.Max(err, 0) / w2);
            voicedNew = track.Voiced;
        }

        double Synth()
        {
            double t = Math.Min(1, (step + 1) / (double)hopN);
            step++;
            for (int i = 1; i <= Order; i++) k[i] = kOld[i] + (kNew[i] - kOld[i]) * t;
            double gain = gOld + (gNew - gOld) * t;
            double exc = Noise();
            if (src != Source.Noise && voicedNew)
            {
                double hz = pulseHz;
                if (src == Source.Tuned) { double f = lowRate * dec / Math.Max(1, track.Period); hz = tune != null ? tune(f) : f; }
                double period = lowRate / Math.Max(40, Math.Min(1000, hz));
                pulsePh += 1;
                double p = 0;
                if (pulsePh >= period) { pulsePh -= period; p = Math.Sqrt(period); }
                exc = p * (1 - noiseMix) + exc * noiseMix;
            }
            // Tüm kutuplu kafes süzgeci (1/A), sonra ön vurgunun tersi.
            double f0 = exc * gain;
            for (int i = Order; i >= 1; i--) { f0 -= k[i] * g[i - 1]; g[i] = g[i - 1] + k[i] * f0; }
            g[0] = f0;
            deEmph = f0 + Emph * deEmph;
            return deEmph;
        }

        double Noise()
        {
            // Birim varyanslı, kabaca normal gürültü.
            return (rnd.NextDouble() + rnd.NextDouble() + rnd.NextDouble() - 1.5) * 2;
        }
    }

    /// <summary>Autotune notaları.</summary>
    static class Scale
    {
        static readonly int[] Major = { 0, 2, 4, 5, 7, 9, 11 };

        /// <summary>Perdeyi do majörün (la minör) en yakın notasına oturtur; shift yarım ton kadar kaydırır.</summary>
        public static Func<double, double> Snap(double shift = 0) => f =>
        {
            double m = 69 + 12 * Math.Log(f / 440, 2);
            int oct = (int)Math.Floor(m / 12);
            double best = m, bd = 99;
            for (int o = oct - 1; o <= oct + 1; o++)
                foreach (int s in Major)
                {
                    double c = o * 12 + s, dd = Math.Abs(c - m);
                    if (dd < bd) { bd = dd; best = c; }
                }
            return 440 * Math.Pow(2, (best + shift - 69) / 12);
        };
    }
}
