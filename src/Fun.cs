using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Speech.Synthesis;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ValoLive
{
    /// <summary>
    /// Chat eğlencesi: izleyici "!balloon selam" yazınca mesajı yayında bir balon taşır. Uçak pankartı, şişede mesaj,
    /// güvercin, kağıt uçak, duvar yazısı, domates/yumurta, çıkartma, ortak havai fişek, emoji yağmuru, akvaryum,
    /// ördek yarışı, sesli okuma, ters ekran ve büyüteç. Kick chat'i zaten okunuyor (KickChat); burada komutlar
    /// süzülür (küfür, bağlantı), sınırlanır (izleyici başına bekleme) ve katmana verilir.
    /// </summary>
    sealed class FunChat : IDisposable
    {
        public sealed class Def { public string Id, Label; public string[] Cmds; public bool Graph; }

        static Def[] defs;
        /// <summary>Eğlenceler: kimlik (ayarlarda), panel yazısı, komutlar (her dilde aynı, İngilizce).</summary>
        public static Def[] Defs => defs ?? (defs = new[]
        {
            new Def { Id = "balloon", Label = L.T("Balon: mesaj balonla süzülür, !pop patlatır"), Cmds = new[] { "balloon" } },
            new Def { Id = "plane", Label = L.T("Uçak pankartı"), Cmds = new[] { "plane" } },
            new Def { Id = "bottle", Label = L.T("Şişede mesaj"), Cmds = new[] { "bottle" } },
            new Def { Id = "pigeon", Label = L.T("Güvercin postası (ve sürprizi)"), Cmds = new[] { "pigeon" } },
            new Def { Id = "paper", Label = L.T("Kağıt uçak"), Cmds = new[] { "paper" } },
            new Def { Id = "spray", Label = L.T("Duvar yazısı (1 dk kalır)"), Cmds = new[] { "spray" } },
            new Def { Id = "tomato", Label = L.T("Domates / yumurta fırlat"), Cmds = new[] { "tomato", "egg" } },
            new Def { Id = "sticker", Label = L.T("Çıkartma yapıştır (emoji)"), Cmds = new[] { "sticker" } },
            new Def { Id = "firework", Label = L.T("Ortak havai fişek"), Cmds = new[] { "firework" } },
            new Def { Id = "rain", Label = L.T("Emoji yağmuru (aynı emojiyi yazınca)"), Cmds = new string[0] },
            new Def { Id = "aquarium", Label = L.T("Akvaryum: chat'e yazan balık olur"), Cmds = new string[0] },
            new Def { Id = "race", Label = L.T("Ördek yarışı"), Cmds = new[] { "race" } },
            new Def { Id = "say", Label = L.T("Sesli oku (robot sesi)"), Cmds = new[] { "say" } },
            new Def { Id = "flip", Label = L.T("Ters ekran (3 sn)"), Cmds = new[] { "flip" }, Graph = true },
            new Def { Id = "zoom", Label = L.T("Büyüteç: nişangâhı büyütür"), Cmds = new[] { "zoom" }, Graph = true },
        });

        static readonly Regex Url = new Regex(@"(https?://|www\.)\S+|\b[\w-]+\.(com|net|org|tv|gg|xyz|ru|io|me|link|ly|co)\b\S*", RegexOptions.IgnoreCase);
        static readonly Regex Repeat = new Regex(@"(.)\1{3,}");
        static readonly Color[] Palette = FunLayer.Palette;

        readonly Settings cfg;
        readonly FunLayer fx;
        readonly AquariumLayer tank;
        readonly Speaker speaker;
        readonly object lk = new object();
        readonly Dictionary<string, DateTime> next = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Dictionary<string, DateTime>> seen = new Dictionary<string, Dictionary<string, DateTime>>();
        DateTime flipNext, zoomNext, rainNext;
        Timer flipOff, zoomOff;

        /// <summary>Önizleme: bekleme süresi ve abone sınırı yok.</summary>
        public bool Preview;
        /// <summary>Ters ekran / büyüteç ffmpeg grafiğinde var mı (yayın başlarken açıksa eklenir).</summary>
        public bool CanFlip, CanZoom;
        /// <summary>ffmpeg canlı komutu: ("flip" | "zoom", açık mı).</summary>
        public Action<string, bool> Graph;
        /// <summary>Kick chat'e duyuru (chat komutları açık ve hesap bağlıysa).</summary>
        public Action<string> Announce;

        public FunChat(Settings cfg, FunLayer fx, AquariumLayer tank, Speaker speaker)
        {
            this.cfg = cfg; this.fx = fx; this.tank = tank; this.speaker = speaker;
            fx.RaceWon += u => Announce?.Invoke("🦆 " + L.F("Ördek yarışını {0} kazandı!", u));
        }

        public void Handle(ChatMessage m)
        {
            if (m == null || !cfg.Fun) return;
            try { lock (lk) HandleCore(m); }
            catch (Exception e) { Diag.Event("EĞLENCE", e.Message); }
        }

        void HandleCore(ChatMessage m)
        {
            var now = DateTime.Now;
            // Diğer chat botları (BotRix, KickBot…) balık olmasın, emoji saymasın.
            bool bot = m.User.EndsWith("bot", StringComparison.OrdinalIgnoreCase);
            var col = UserColor(m);
            if (tank != null && !bot && cfg.FunOn("aquarium")) tank.Join(m.User, col, m.Sub || m.Mod, m.Broadcaster);
            string text = (m.Text ?? "").Trim();
            if (!text.StartsWith("!")) { if (!bot) Rain(m, now); return; }

            int sp = text.IndexOf(' ');
            string cmd = Fold(sp < 0 ? text.Substring(1) : text.Substring(1, sp - 1));
            string rest = sp < 0 ? "" : Clean(text.Substring(sp + 1));
            bool priv = Preview || m.Mod || m.Broadcaster;
            if (cmd == "pop")
            {
                if (cfg.FunOn("balloon") && Ready(m, now, priv)) Spend(m, now, fx.Pop(m.User));
                return;
            }
            var d = Defs.FirstOrDefault(x => x.Cmds.Any(c => Fold(c) == cmd));
            if (d == null || !cfg.FunOn(d.Id)) return;
            if ((d.Id == "say" || d.Graph) && cfg.FunSubsOnly && !priv && !m.Sub) return;
            if (d.Id == "race") { fx.RaceJoin(m.User, col, Preview ? 6 : 20); return; } // katılmak bekleme harcamaz
            if (!Ready(m, now, priv)) return;

            bool ok = false;
            switch (d.Id)
            {
                case "balloon": ok = fx.Balloon(m.User, col, rest); break;
                case "plane": ok = rest != "" && fx.Plane(m.User, col, rest); break;
                case "bottle": ok = rest != "" && fx.Bottle(m.User, col, rest); break;
                case "pigeon": ok = rest != "" && fx.Pigeon(m.User, col, rest); break;
                case "paper": ok = rest != "" && fx.Paper(m.User, col, rest); break;
                case "spray": ok = Rich.Plain(rest).Trim() != "" && fx.Spray(m.User, col, rest); break;
                case "tomato": ok = fx.Throw(cmd == "egg" ? "egg" : "tomato", m.User, col); break;
                case "sticker": ok = fx.Sticker(m.User, col, rest); break;
                case "firework": ok = fx.Firework(m.User, cfg.FunGoal); break;
                case "say":
                    string say = Rich.Plain(rest).Trim();
                    ok = say != "" && speaker != null && speaker.Speak(say) && fx.Say(m.User, col, rest);
                    break;
                case "flip":
                    if (!CanFlip || (!Preview && now < flipNext)) break;
                    flipNext = now.AddMinutes(5);
                    Graph?.Invoke("flip", true);
                    flipOff?.Dispose();
                    flipOff = new Timer(_ => Graph?.Invoke("flip", false), null, 3000, Timeout.Infinite);
                    ok = fx.Banner("flip", m.User, col, 3.2);
                    break;
                case "zoom":
                    if (!CanZoom || (!Preview && now < zoomNext)) break;
                    zoomNext = now.AddMinutes(2);
                    Graph?.Invoke("zoom", true);
                    zoomOff?.Dispose();
                    zoomOff = new Timer(_ => Graph?.Invoke("zoom", false), null, 6000, Timeout.Infinite);
                    ok = fx.Banner("zoom", m.User, col, 6);
                    break;
            }
            Spend(m, now, ok);
            if (ok) Diag.Event("EĞLENCE", $"{d.Id} · {m.User}");
        }

        bool Ready(ChatMessage m, DateTime now, bool priv) => priv || !next.TryGetValue(m.User, out var t) || now >= t;

        void Spend(ChatMessage m, DateTime now, bool ok)
        {
            if (ok && !Preview && !m.Mod && !m.Broadcaster) next[m.User] = now.AddSeconds(Math.Max(0, cfg.FunCooldown));
        }

        /// <summary>Aynı emojiyi (ya da Kick emote'unu) 30 sn içinde yeterince farklı kişi yazınca ekrana yağar.</summary>
        void Rain(ChatMessage m, DateTime now)
        {
            if (!cfg.FunOn("rain")) return;
            foreach (var img in Rich.Tokens(m.Text).Where(t => t.Img != null).Select(t => t.Img).Distinct())
            {
                if (!seen.TryGetValue(img, out var who)) seen[img] = who = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
                who[m.User] = now;
                foreach (var old in who.Where(x => (now - x.Value).TotalSeconds > 30).Select(x => x.Key).ToList()) who.Remove(old);
                if (who.Count >= Math.Max(2, cfg.FunRain) && (Preview || now >= rainNext) && fx.Rain(img))
                {
                    rainNext = now.AddSeconds(40);
                    seen.Clear();
                    Diag.Event("EĞLENCE", "emoji yağmuru");
                    return;
                }
            }
            if (seen.Count > 200) seen.Clear();
        }

        static Color UserColor(ChatMessage m)
        {
            try { if (!string.IsNullOrEmpty(m.Color)) return ColorTranslator.FromHtml(m.Color); } catch { }
            return Palette[Math.Abs((m.User ?? "").GetHashCode()) % Palette.Length];
        }

        /// <summary>Komut karşılaştırması: küçük harf (Türk klavyesindeki İ/ı da i sayılır: !FLİP = !flip).</summary>
        public static string Fold(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s.ToLowerInvariant())
                sb.Append(c == 'ı' || c == 'i' || c == 'î' ? 'i' : c == 'ğ' ? 'g' : c == 'ü' || c == 'û' ? 'u' : c == 'ş' ? 's' : c == 'ö' ? 'o' : c == 'ç' ? 'c' : c == 'â' ? 'a' : c);
            return sb.ToString().Replace("i̇", "i");
        }

        /// <summary>Ekrana çıkacak metin: bağlantılar silinir, yasaklı kelimeler yıldızlanır, uzar giden harfler kısalır, en çok 90 karakter.</summary>
        string Clean(string s)
        {
            s = Url.Replace(s, "");
            var block = (cfg.FunBlock ?? "").Split(',').Select(w => Fold(w.Trim())).Where(w => w != "").ToArray();
            var words = s.Split(' ');
            for (int i = 0; i < words.Length; i++)
            {
                if (words[i].StartsWith("[emote:")) continue;
                string k = new string(Fold(words[i]).Where(char.IsLetterOrDigit).ToArray());
                k = Repeat.Replace(k, "$1");
                if (k != "" && block.Any(b => k == b || (b.Length >= 4 && k.StartsWith(b)))) words[i] = "***";
            }
            s = Repeat.Replace(string.Join(" ", words.Where(w => w != "")).Trim(), "$1$1$1");
            if (s.Length <= 90) return s;
            int cut = 90;
            foreach (Match e in Rich.Emote.Matches(s)) if (e.Index < cut && e.Index + e.Length > cut) cut = e.Index;
            if (cut > 0 && char.IsHighSurrogate(s[cut - 1])) cut--;
            return s.Substring(0, cut).TrimEnd() + "…";
        }

        public void Dispose()
        {
            flipOff?.Dispose(); zoomOff?.Dispose();
        }
    }

    /// <summary>Robot sesi: Windows'un konuşma motoru, varsayılan hoparlörden (oyun sesi o cihazdan yakalanıyorsa yayına da gider).</summary>
    sealed class Speaker : IDisposable
    {
        SpeechSynthesizer synth;
        volatile bool busy;
        public bool Busy => busy;

        /// <summary>Konuşmuyorsa okumaya başlar; meşgulse false (sıraya alınmaz, chat yığılmasın).</summary>
        public bool Speak(string text)
        {
            if (busy) return false;
            try
            {
                if (synth == null)
                {
                    synth = new SpeechSynthesizer();
                    synth.SetOutputToDefaultAudioDevice();
                    var voice = synth.GetInstalledVoices().Where(v => v.Enabled)
                        .FirstOrDefault(v => v.VoiceInfo.Culture.TwoLetterISOLanguageName == L.Culture.TwoLetterISOLanguageName);
                    if (voice != null) synth.SelectVoice(voice.VoiceInfo.Name);
                    synth.SpeakCompleted += (s, e) => busy = false;
                }
                busy = true;
                synth.SpeakAsync(text);
                return true;
            }
            catch (Exception e)
            {
                busy = false;
                Diag.Event("EĞLENCE", "sesli okuma: " + e.Message);
                return false;
            }
        }

        public void Dispose()
        {
            try { synth?.SpeakAsyncCancelAll(); synth?.Dispose(); } catch { }
            synth = null; busy = false;
        }
    }

    /// <summary>Mesaj parçaları: kelimeler, emoji (Twemoji resmi) ve Kick emote'ları (resim).</summary>
    static class Rich
    {
        public sealed class Tok { public string Text, Img; public float X, W; }
        public sealed class Lay { public List<List<Tok>> Lines = new List<List<Tok>>(); public float W, H, LineH; }

        public static readonly Regex Emote = new Regex(@"\[emote:(\d+):([^\]]*)\]");
        static string EmoteUrl(string id) => $"https://files.kick.com/emotes/{id}/fullsize";
        static string EmojiUrl(string code) => $"https://cdn.jsdelivr.net/gh/jdecked/twemoji@15.1.0/assets/72x72/{code}.png";

        public static List<Tok> Tokens(string text)
        {
            var toks = new List<Tok>();
            text = text ?? "";
            int last = 0;
            foreach (Match e in Emote.Matches(text))
            {
                Words(text.Substring(last, e.Index - last), toks);
                toks.Add(new Tok { Img = EmoteUrl(e.Groups[1].Value), Text = "" });
                last = e.Index + e.Length;
            }
            Words(text.Substring(last), toks);
            return toks;
        }

        /// <summary>Yalnızca yazı (sesli okuma, duvar yazısı): emoji ve emote'lar atılır.</summary>
        public static string Plain(string text) => string.Join(" ", Tokens(text).Where(t => t.Img == null).Select(t => t.Text));

        public static void Prefetch(List<Tok> toks) { foreach (var t in toks) if (t.Img != null) Assets.Icon(t.Img); }

        static int Cp(string s, int i) => char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? char.ConvertToUtf32(s, i) : s[i];

        static bool IsEmoji(int c) => (c >= 0x1F000 && c <= 0x1FAFF) || (c >= 0x2600 && c <= 0x27BF) || (c >= 0x2B00 && c <= 0x2BFF)
                                      || (c >= 0x2300 && c <= 0x23FF) || c == 0x3030 || c == 0x303D || c == 0x3297 || c == 0x3299;

        static void Words(string s, List<Tok> toks)
        {
            var word = new StringBuilder();
            void Flush() { if (word.Length > 0) { toks.Add(new Tok { Text = word.ToString() }); word.Clear(); } }
            int i = 0;
            while (i < s.Length)
            {
                if (char.IsSurrogate(s[i]) && Cp(s, i) == s[i]) { i++; continue; } // yarım vekil karakter
                int c = Cp(s, i), n = c > 0xFFFF ? 2 : 1;
                if (char.IsWhiteSpace(s[i])) { Flush(); i++; continue; }
                if (!IsEmoji(c)) { word.Append(s, i, n); i += n; continue; }
                // Emoji dizisi: bayrak çifti, ten rengi, ZWJ ile birleşenler, FE0F.
                Flush();
                var seq = new List<int> { c };
                i += n;
                if (c >= 0x1F1E6 && c <= 0x1F1FF && i < s.Length && Cp(s, i) >= 0x1F1E6 && Cp(s, i) <= 0x1F1FF) { seq.Add(Cp(s, i)); i += 2; }
                while (i < s.Length)
                {
                    int d = Cp(s, i), dn = d > 0xFFFF ? 2 : 1;
                    if (d == 0xFE0F || d == 0x20E3 || (d >= 0x1F3FB && d <= 0x1F3FF)) { seq.Add(d); i += dn; }
                    else if (d == 0x200D && i + 1 < s.Length) { seq.Add(d); i += 1; int e = Cp(s, i); seq.Add(e); i += e > 0xFFFF ? 2 : 1; }
                    else break;
                }
                bool zwj = seq.Contains(0x200D);
                toks.Add(new Tok { Img = EmojiUrl(string.Join("-", seq.Where(x => zwj || x != 0xFE0F).Select(x => x.ToString("x")))), Text = "" });
            }
            Flush();
        }

        public static Lay Layout(Graphics g, List<Tok> toks, Font f, float maxW, int maxLines)
        {
            float fh = f.GetHeight(g), ih = fh * 1.05f, space = f.Size * 0.28f;
            var lay = new Lay { LineH = fh * 1.08f };
            var line = new List<Tok>();
            float x = 0;
            foreach (var t0 in toks)
            {
                var t = new Tok { Text = t0.Text, Img = t0.Img };
                if (t.Img != null)
                {
                    var img = Assets.Icon(t.Img);
                    t.W = ih;
                    if (img != null) lock (img) t.W = ih * img.Width / Math.Max(1, img.Height);
                }
                else
                {
                    t.W = g.MeasureString(t.Text, f, PointF.Empty, StringFormat.GenericTypographic).Width;
                    while (t.W > maxW && t.Text.Length > 2)
                    {
                        t.Text = t.Text.Substring(0, t.Text.Length - 2) + "…";
                        t.W = g.MeasureString(t.Text, f, PointF.Empty, StringFormat.GenericTypographic).Width;
                    }
                }
                if (x > 0 && x + t.W > maxW)
                {
                    lay.Lines.Add(line); line = new List<Tok>(); x = 0;
                    if (lay.Lines.Count >= maxLines) break;
                }
                t.X = x; line.Add(t);
                x += t.W + space;
                lay.W = Math.Max(lay.W, x - space);
            }
            if (line.Count > 0 && lay.Lines.Count < maxLines) lay.Lines.Add(line);
            lay.H = lay.Lines.Count * lay.LineH;
            return lay;
        }

        public static void Draw(Graphics g, Lay lay, Font f, Color c, float x, float y, double alpha)
        {
            if (lay == null) return;
            using (var br = new SolidBrush(Layer.A(c, alpha)))
                foreach (var line in lay.Lines)
                {
                    foreach (var t in line)
                    {
                        if (t.Img != null) Layer.DrawImage(g, Assets.Icon(t.Img), new RectangleF(x + t.X, y + lay.LineH * 0.01f, t.W, lay.LineH * 0.96f), alpha);
                        else g.DrawString(t.Text, f, br, x + t.X, y, StringFormat.GenericTypographic);
                    }
                    y += lay.LineH;
                }
        }
    }

    /// <summary>
    /// Chat eğlencesinin tam ekran katmanı. Yarı çözünürlükte çizilir, ffmpeg iki kat büyütüp bindirir; ekranda hiçbir şey
    /// yokken bindirme tamamen kapanır (Sparse). Duran şeyler (duvar yazısı, çıkartma) bir kez çizilip bırakılır.
    /// </summary>
    sealed class FunLayer : Layer
    {
        public static readonly Color[] Palette = new[] { "#ff4655", "#ffb547", "#3ddc97", "#4fc3f7", "#c890f5", "#ff7eb6", "#ffe066", "#7aa2ff" }.Select(ColorTranslator.FromHtml).ToArray();
        static readonly Color PaperCol = ColorTranslator.FromHtml("#f6ecd2"), PaperEdge = ColorTranslator.FromHtml("#b89a5e"), Ink = ColorTranslator.FromHtml("#3b2a14");
        static readonly Color Gold = ColorTranslator.FromHtml("#ffd76a");

        public override bool Sparse => true;

        sealed class Fx
        {
            public string Kind, User, Text, By;
            public Color Col, Paint;
            public DateTime At;
            public double Life, Delay, Ev = -1;    // Ev: patlama / üstü çizilme anı (yaş, sn)
            public float X, Y, X2;
            public int Dir, Seed, Slot;
            public string Img;
            public List<Rich.Tok> Toks;
            public Rich.Lay Lay; public float LayW = -1, NameW;
            public GraphicsPath Path; public float PW, PH;
            public Bitmap Art;
            public PointF[] Blob;
        }

        sealed class Duck { public string User; public Color Col; public double T, A, W, Ph, Finish; }

        readonly List<Fx> items = new List<Fx>();
        readonly object lk = new object();
        readonly Random rnd = new Random();
        readonly Speaker talk;
        readonly bool live;
        readonly Font fName, fText, fSmall, fBig, fTitle, fBannerName, fBanner;
        public event Action<string> RaceWon;

        // Havai fişek sayacı ve gösterisi
        int fwCount, fwGoal = 10, fwSeed;
        DateTime fwLast, fwPulse, fwShow = DateTime.MinValue;
        readonly List<string> fwBy = new List<string>();
        List<string> fwNames = new List<string>();
        // Ördek yarışı
        List<Duck> ducks;
        DateTime raceAt;
        double joinSec = 20, raceWinT, raceEnd;
        bool raceReady, raceAnnounced;

        volatile bool dirty;
        bool wasMoving, wasAny;
        DateTime emptyAt = DateTime.MaxValue;

        public FunLayer(int screenW, int screenH, float sc, Speaker talk, bool run = true)
            : base(0, 0, screenW / 2, screenH / 2, 30, sc / 2, run)
        {
            Up = 2; this.talk = talk; live = run;
            fName = Assets.Ui(P(15), true); fText = Assets.Ui(P(17)); fSmall = Assets.Ui(P(14), true);
            fBig = Assets.Display(P(34)); fTitle = Assets.Display(P(46));
            fBannerName = Assets.Ui(P(21), true); fBanner = Assets.Ui(P(21));
        }

        // ---------- ekleme (chat iş parçacığından) ----------

        Fx New(string kind, string user, Color col, string text, double life)
        {
            var f = new Fx { Kind = kind, User = user ?? "", Col = col, Text = text ?? "", At = DateTime.Now, Life = life, Seed = rnd.Next(), Dir = rnd.Next(2) == 0 ? -1 : 1 };
            f.Paint = Palette[rnd.Next(Palette.Length)];
            f.Toks = Rich.Tokens(f.Text);
            Rich.Prefetch(f.Toks);
            return f;
        }

        bool Add(Fx f, int max)
        {
            lock (lk)
            {
                if (items.Count(i => i.Kind == f.Kind && i.Ev < 0) >= max) return false;
                items.Add(f);
            }
            Wake();
            return true;
        }

        void Wake()
        {
            dirty = true;
            lock (lk) { emptyAt = DateTime.MaxValue; SetShown(true); }
        }

        /// <summary>Rastgele yer (oran): nişangâhın çevresi ve soldaki chat kutusu boş kalır.</summary>
        PointF Spot(float x0 = 0.12f, float x1 = 0.88f, float y0 = 0.2f, float y1 = 0.72f)
        {
            for (int i = 0; i < 30; i++)
            {
                float x = x0 + (float)rnd.NextDouble() * (x1 - x0), y = y0 + (float)rnd.NextDouble() * (y1 - y0);
                if (Math.Abs(x - 0.5f) < 0.15f && Math.Abs(y - 0.5f) < 0.17f) continue;
                if (x < 0.26f && y > 0.5f) continue;
                return new PointF(x * W, y * H);
            }
            return new PointF(0.75f * W, 0.3f * H);
        }

        float Side() => (rnd.Next(2) == 0 ? 0.1f + 0.2f * (float)rnd.NextDouble() : 0.7f + 0.2f * (float)rnd.NextDouble()) * W;

        public bool Balloon(string user, Color col, string text) { var f = New("balloon", user, col, text, 12); f.X = Side(); return Add(f, 6); }
        public bool Plane(string user, Color col, string text) { var f = New("plane", user, col, text, 10); f.Y = (0.13f + 0.17f * (float)rnd.NextDouble()) * H; return Add(f, 2); }
        public bool Bottle(string user, Color col, string text) { var f = New("bottle", user, col, text, 10); f.X = (rnd.Next(2) == 0 ? 0.3f : 0.72f) * W; return Add(f, 1); }
        public bool Pigeon(string user, Color col, string text) { var f = New("pigeon", user, col, text, 10); var p = Spot(0.2f, 0.8f, 0.36f, 0.66f); f.X = p.X; f.Y = p.Y; return Add(f, 2); }
        public bool Paper(string user, Color col, string text) { var f = New("paper", user, col, text, 7.5); f.Y = (0.3f + 0.25f * (float)rnd.NextDouble()) * H; return Add(f, 3); }
        public bool Throw(string kind, string user, Color col) { var f = New(kind, user, col, "", 6); var p = Spot(); f.X = p.X; f.Y = p.Y; f.X2 = (0.3f + 0.4f * (float)rnd.NextDouble()) * W; return Add(f, 4); }
        public bool Say(string user, Color col, string text) => Add(New("say", user, col, text, 3), 1);
        public bool Banner(string kind, string user, Color col, double secs) => Add(New(kind, user, col, "", secs), 1);

        public bool Sticker(string user, Color col, string text)
        {
            var f = New("sticker", user, col, text, 30);
            f.Img = f.Toks.FirstOrDefault(t => t.Img != null)?.Img;
            if (f.Img == null && Rich.Plain(text).Trim() == "") return false;
            var p = Spot(); f.X = p.X; f.Y = p.Y;
            return Add(f, 6);
        }

        public bool Rain(string img) { var f = New("rain", "", Color.White, "", 7.5); f.Img = img; Assets.Icon(img); return Add(f, 1); }

        /// <summary>En eski balonu patlatır (yeni gelen bir saniye uçsun).</summary>
        public bool Pop(string user)
        {
            lock (lk)
            {
                var now = DateTime.Now;
                var b = items.Where(i => i.Kind == "balloon" && i.Ev < 0 && Age(i, now) > 1).OrderBy(i => i.At).FirstOrDefault();
                if (b == null) return false;
                b.Ev = Age(b, now); b.By = user;
            }
            Wake();
            return true;
        }

        static readonly PointF[] Walls = { new PointF(0.74f, 0.36f), new PointF(0.5f, 0.2f), new PointF(0.32f, 0.4f) };

        /// <summary>Duvar yazısı: üç duvar var; doluysa en eskisinin üstü çizilir, yenisi onun yerine yazılır.</summary>
        public bool Spray(string user, Color col, string text)
        {
            var f = New("spray", user, col, text, 60);
            lock (lk)
            {
                var now = DateTime.Now;
                var on = items.Where(i => i.Kind == "spray" && i.Ev < 0).ToList();
                int slot = -1;
                for (int z = 0; z < Walls.Length && slot < 0; z++) if (on.All(i => i.Slot != z)) slot = z;
                if (slot < 0)
                {
                    var old = on.OrderBy(i => i.At).First();
                    old.Ev = Age(old, now);
                    slot = old.Slot; f.Delay = 0.6;
                }
                f.Slot = slot;
                f.X = Walls[slot].X * W + (float)(rnd.NextDouble() - 0.5) * P(60);
                f.Y = Walls[slot].Y * H + (float)(rnd.NextDouble() - 0.5) * P(40);
                items.Add(f);
            }
            Wake();
            return true;
        }

        /// <summary>Ortak havai fişek: her !firework sayacı doldurur; hedefe ulaşınca gösteri.</summary>
        public bool Firework(string user, int goal)
        {
            lock (lk)
            {
                var now = DateTime.Now;
                if ((now - fwShow).TotalSeconds < 9) return false;
                fwGoal = Math.Max(2, goal);
                fwCount++; fwLast = fwPulse = now;
                if (!fwBy.Contains(user)) fwBy.Add(user);
                if (fwCount >= fwGoal)
                {
                    fwShow = now; fwSeed = rnd.Next();
                    fwNames = fwBy.ToList();
                    fwCount = 0; fwBy.Clear();
                }
            }
            Wake();
            return true;
        }

        /// <summary>Ördek yarışı: ilk !race kayıt penceresini açar; pencere bitince geri sayım ve yarış.</summary>
        public bool RaceJoin(string user, Color col, double join)
        {
            lock (lk)
            {
                var now = DateTime.Now;
                if (ducks == null) { ducks = new List<Duck>(); raceAt = now; joinSec = join; raceReady = raceAnnounced = false; }
                else if ((now - raceAt).TotalSeconds >= joinSec) return false;
                if (ducks.Count >= 8 || ducks.Any(d => d.User == user)) return false;
                ducks.Add(new Duck { User = user, Col = col });
            }
            Wake();
            return true;
        }

        static double Progress(Duck d, double t) =>
            Math.Max(0, Math.Min(1, (t + d.A * (Math.Sin(d.W * t + d.Ph) - Math.Sin(d.Ph)) / d.W) / d.T));

        void PrepareRace()
        {
            if (ducks.Count < 2) ducks.Add(new Duck { User = "ValoBot", Col = ColorTranslator.FromHtml("#9aa3b2") });
            foreach (var d in ducks)
            {
                // Hız dalgalanır ama hiç geri gitmez (A < 1); bitiş anı önceden hesaplanır.
                d.T = 7 + rnd.NextDouble() * 3; d.A = 0.3 + rnd.NextDouble() * 0.45; d.W = 1.2 + rnd.NextDouble() * 2.4; d.Ph = rnd.NextDouble() * 6.283;
                double t = 0;
                while (Progress(d, t) < 1 && t < 30) t += 0.02;
                d.Finish = t;
            }
            raceWinT = ducks.Min(d => d.Finish);
            raceEnd = Math.Min(ducks.Max(d => d.Finish), raceWinT + 4) + 0.3;
            raceReady = true;
        }

        // ---------- zaman ----------

        static double Age(Fx f, DateTime now) => (now - f.At).TotalSeconds;
        bool MeterOn(DateTime now) => fwCount > 0 && (now - fwLast).TotalSeconds < 120;
        bool ShowOn(DateTime now) => (now - fwShow).TotalSeconds < 8.5;

        /// <summary>lk altında: süresi dolanları atar, yarışı ilerletir. Yarış bitince kazananı döner (kilit dışında duyurulur).</summary>
        string Step(DateTime now)
        {
            foreach (var f in items.Where(i => i.Kind == "say"))
            {
                double a = Age(f, now);
                if (talk != null && talk.Busy && a > 0.3) f.Life = Math.Min(30, Math.Max(f.Life, a + 1.0));
            }
            items.RemoveAll(f =>
            {
                double a = Age(f, now);
                bool gone = a > f.Life + f.Delay || (f.Ev >= 0 && a - f.Ev > 1.7);
                if (gone) { f.Path?.Dispose(); f.Art?.Dispose(); }
                return gone;
            });
            if (fwCount > 0 && (now - fwLast).TotalSeconds > 120) { fwCount = 0; fwBy.Clear(); }
            string won = null;
            if (ducks != null)
            {
                double r = (now - raceAt).TotalSeconds;
                if (!raceReady && r >= joinSec) PrepareRace();
                if (raceReady && !raceAnnounced && r >= joinSec + 3 + raceWinT) { raceAnnounced = true; won = ducks.OrderBy(d => d.Finish).First().User; }
                if (raceReady && r > joinSec + 3 + raceEnd + 5) ducks = null;
            }
            return won;
        }

        bool Still(Fx f, DateTime now)
        {
            double a = Age(f, now);
            if (f.Kind == "spray") return f.Ev < 0 && a - f.Delay > 4.2 && a - f.Delay < f.Life - 1.2;
            if (f.Kind == "sticker") return a > 4.6 && a < f.Life - 1.4 && (f.Img == null || f.Art != null);
            return false;
        }

        protected override bool NeedsRender()
        {
            var now = DateTime.Now;
            string won; bool any, moving;
            lock (lk)
            {
                won = Step(now);
                bool meter = MeterOn(now), show = ShowOn(now);
                any = items.Count > 0 || meter || show || ducks != null;
                moving = show || ducks != null || (meter && (now - fwPulse).TotalSeconds < 0.8) || items.Any(f => !Still(f, now));
                if (any) emptyAt = DateTime.MaxValue;
                else if (emptyAt == DateTime.MaxValue) emptyAt = now;
                // Boşalınca son (boş) kare ffmpeg'e ulaşsın, sonra bindirme kapansın.
                SetShown(any || (now - emptyAt).TotalSeconds < 0.5);
            }
            if (won != null) RaceWon?.Invoke(won);
            bool need = moving || wasMoving || any != wasAny || dirty;
            wasMoving = moving; wasAny = any; dirty = false;
            return need;
        }

        static readonly string[] Order = { "spray", "sticker", "tomato", "egg", "bottle", "pigeon", "paper", "plane", "balloon", "say", "rain", "flip", "zoom" };

        protected override void Draw(Graphics g, double t)
        {
            var now = DateTime.Now;
            List<Fx> list; List<Duck> ds; string won = null;
            bool meter, show;
            lock (lk)
            {
                if (!live) won = Step(now); // önizlemede NeedsRender çağrılmaz
                list = items.OrderBy(i => Array.IndexOf(Order, i.Kind)).ToList();
                ds = ducks?.ToList();
                meter = MeterOn(now); show = ShowOn(now);
            }
            if (won != null) RaceWon?.Invoke(won);
            foreach (var f in list)
            {
                double a = Age(f, now);
                try
                {
                    switch (f.Kind)
                    {
                        case "balloon": DrawBalloon(g, f, a); break;
                        case "plane": DrawPlane(g, f, a); break;
                        case "bottle": DrawBottle(g, f, a); break;
                        case "pigeon": DrawPigeon(g, f, a); break;
                        case "paper": DrawPaper(g, f, a); break;
                        case "spray": DrawSpray(g, f, a); break;
                        case "tomato": case "egg": DrawThrow(g, f, a); break;
                        case "sticker": DrawSticker(g, f, a); break;
                        case "rain": DrawRain(g, f, a); break;
                        case "say": DrawSay(g, f, a); break;
                        case "flip": case "zoom": DrawBanner(g, f, a); break;
                    }
                }
                catch (Exception e) { Streamer.Log("eğlence " + f.Kind + ": " + e.Message); }
            }
            if (meter) DrawMeter(g, now);
            if (ds != null) DrawRace(g, now, ds);
            if (show) DrawShow(g, now);
        }

        public override void Dispose()
        {
            base.Dispose();
            lock (lk) { foreach (var f in items) { f.Path?.Dispose(); f.Art?.Dispose(); } items.Clear(); }
            foreach (var f in new[] { fName, fText, fSmall, fBig, fTitle, fBannerName, fBanner }) f.Dispose();
        }

        // ---------- yardımcılar ----------

        static float Ease(double x) { x = Math.Max(0, Math.Min(1, x)); return (float)(1 - Math.Pow(1 - x, 3)); }
        static float Lerp(float a, float b, double k) => a + (b - a) * (float)k;
        static Color Mix(Color a, Color b, float k) => Color.FromArgb(a.A, (int)(a.R + (b.R - a.R) * k), (int)(a.G + (b.G - a.G) * k), (int)(a.B + (b.B - a.B) * k));
        static Color Readable(Color c) => (c.R * 0.3 + c.G * 0.59 + c.B * 0.11) < 110 ? Mix(c, Color.White, 0.5f) : c;
        static double Fade(double a, double life, double inSec = 0.25, double outSec = 0.6) => Math.Max(0, Math.Min(1, Math.Min(a / inSec, (life - a) / outSec)));

        SizeF CardSize(Graphics g, Fx f, float maxW)
        {
            if (f.Lay == null || f.LayW != maxW)
            {
                f.Lay = Rich.Layout(g, f.Toks, fText, maxW - P(22), 3);
                f.LayW = maxW;
                f.NameW = g.MeasureString(f.User, fName, PointF.Empty, StringFormat.GenericTypographic).Width;
            }
            float w = Math.Min(maxW, Math.Max(f.NameW, f.Lay.W) + P(22));
            float h = P(7) + fName.GetHeight(g) + (f.Lay.Lines.Count > 0 ? P(1) + f.Lay.H : 0) + P(7);
            return new SizeF(Math.Max(w, P(70)), h);
        }

        /// <summary>Mesaj kartı: kullanıcı adı kendi renginde, altında mesaj (emoji/emote resim). paper: kağıt görünümü.</summary>
        void CardDraw(Graphics g, Fx f, RectangleF r, double alpha, bool paper)
        {
            if (alpha <= 0.01) return;
            using (var p = Rounded(r, P(9)))
            {
                var st = g.Save();
                g.TranslateTransform(P(3), P(4));
                using (var sh = new SolidBrush(Color.FromArgb((int)(70 * alpha), 0, 0, 0))) g.FillPath(sh, p);
                g.Restore(st);
                using (var br = new SolidBrush(paper ? A(PaperCol, alpha) : Color.FromArgb((int)(220 * alpha), 14, 16, 24))) g.FillPath(br, p);
                using (var pen = new Pen(A(paper ? PaperEdge : f.Col, paper ? alpha : 0.85 * alpha), P(2))) g.DrawPath(pen, p);
            }
            float x = r.X + P(11), y = r.Y + P(7);
            using (var br = new SolidBrush(A(paper ? Mix(f.Col, Color.Black, 0.45f) : Readable(f.Col), alpha)))
                g.DrawString(f.User, fName, br, x, y, StringFormat.GenericTypographic);
            Rich.Draw(g, f.Lay, fText, paper ? Ink : Color.FromArgb(242, 244, 250), x, y + fName.GetHeight(g) + P(1), alpha);
        }

        /// <summary>Kartı (cx, top) merkezli çizer; ekrandan taşmasın diye yatayda içeri itilir.</summary>
        RectangleF Card(Graphics g, Fx f, float cx, float top, float maxW, double alpha, bool paper)
        {
            var sz = CardSize(g, f, maxW);
            float x = Math.Max(P(8), Math.Min(W - P(8) - sz.Width, cx - sz.Width / 2));
            var r = new RectangleF(x, top, sz.Width, sz.Height);
            CardDraw(g, f, r, alpha, paper);
            return r;
        }

        /// <summary>Kenarı koyu, içi renkli yazı (her zeminde okunur).</summary>
        void Outlined(Graphics g, string text, Font f, Color fill, float cx, float y, double alpha, float maxW = 0)
        {
            if (string.IsNullOrEmpty(text) || alpha <= 0.01) return;
            using (var path = new GraphicsPath())
            {
                path.AddString(text, f.FontFamily, (int)f.Style, f.Size, PointF.Empty, StringFormat.GenericTypographic);
                var b = path.GetBounds();
                float k = maxW > 0 && b.Width > maxW ? maxW / b.Width : 1;
                using (var m = new Matrix())
                {
                    m.Translate(cx, y); m.Scale(k, k); m.Translate(-(b.X + b.Width / 2), -b.Y);
                    path.Transform(m);
                }
                using (var pen = new Pen(Color.FromArgb((int)(230 * alpha), 8, 10, 16), Math.Max(2, f.Size * 0.16f)) { LineJoin = LineJoin.Round }) g.DrawPath(pen, path);
                using (var br = new SolidBrush(A(fill, alpha))) g.FillPath(br, path);
            }
        }

        /// <summary>Küçük isim etiketi (koyu hap üstünde).</summary>
        void Tag(Graphics g, string text, Color c, float cx, float y, double alpha)
        {
            if (alpha <= 0.01) return;
            var sz = g.MeasureString(text, fSmall, PointF.Empty, StringFormat.GenericTypographic);
            var r = new RectangleF(cx - sz.Width / 2 - P(8), y, sz.Width + P(16), sz.Height + P(6));
            using (var p = Rounded(r, r.Height / 2))
            using (var br = new SolidBrush(Color.FromArgb((int)(200 * alpha), 10, 12, 18))) g.FillPath(br, p);
            using (var br = new SolidBrush(A(Readable(c), alpha))) g.DrawString(text, fSmall, br, r.X + P(8), r.Y + P(3), StringFormat.GenericTypographic);
        }

        static void Star(Graphics g, float cx, float cy, float r, Color c)
        {
            var pts = new PointF[10];
            for (int i = 0; i < 10; i++)
            {
                double ang = -Math.PI / 2 + i * Math.PI / 5;
                float rr = i % 2 == 0 ? r : r * 0.45f;
                pts[i] = new PointF(cx + (float)Math.Cos(ang) * rr, cy + (float)Math.Sin(ang) * rr);
            }
            using (var br = new SolidBrush(c)) g.FillPolygon(br, pts);
        }

        // ---------- balon ----------

        void DrawBalloon(Graphics g, Fx f, double a)
        {
            double rise = f.Ev >= 0 ? f.Ev : a;
            if (f.Ev < 0 && a >= 9.6) { f.Ev = 9.6; rise = 9.6; } // yükselince kendiliğinden patlar
            float baseY = H + P(150) - (float)(rise / 12.0) * (H + P(430));
            float phase = (float)(rise * 1.4 + f.Seed % 7);
            float sway = (float)Math.Sin(phase) * P(28);
            float bx = f.X + sway, by = baseY, rx = P(44), ry = P(54);
            float cardTop = by + ry + P(72);
            double pa = f.Ev >= 0 ? a - f.Ev : -1;
            if (pa < 0)
            {
                using (var pen = new Pen(Color.FromArgb(210, 235, 235, 240), P(1.6f)))
                    g.DrawBezier(pen, bx, by + ry + P(8), bx - sway * 0.4f, by + ry + P(32), bx + sway * 0.3f, cardTop - P(30), bx + sway * 0.2f, cardTop);
                var rect = new RectangleF(bx - rx, by - ry, rx * 2, ry * 2);
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(rect);
                    using (var br = new PathGradientBrush(path) { CenterPoint = new PointF(bx - rx * 0.35f, by - ry * 0.4f), CenterColor = Mix(f.Paint, Color.White, 0.45f), SurroundColors = new[] { Mix(f.Paint, Color.Black, 0.3f) } })
                        g.FillPath(br, path);
                }
                using (var br = new SolidBrush(Mix(f.Paint, Color.Black, 0.3f)))
                    g.FillPolygon(br, new[] { new PointF(bx, by + ry - P(3)), new PointF(bx - P(7), by + ry + P(9)), new PointF(bx + P(7), by + ry + P(9)) });
                using (var br = new SolidBrush(Color.FromArgb(120, 255, 255, 255))) g.FillEllipse(br, bx - rx * 0.6f, by - ry * 0.68f, rx * 0.45f, ry * 0.5f);
                var st = g.Save();
                g.TranslateTransform(bx + sway * 0.2f, cardTop);
                g.RotateTransform((float)Math.Sin(phase - 0.7) * 5);
                var sz = CardSize(g, f, P(270));
                CardDraw(g, f, new RectangleF(-sz.Width / 2, 0, sz.Width, sz.Height), Math.Min(1, a / 0.3), false);
                g.Restore(st);
                return;
            }
            // Patlama: ışınlar, uçuşan parçalar; kart düşer.
            if (pa < 0.35)
                using (var pen = new Pen(A(Color.White, 1 - pa / 0.35), P(3)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    for (int i = 0; i < 12; i++)
                    {
                        double ang = i * Math.PI / 6;
                        float r1 = rx * (0.6f + (float)pa * 3), r2 = r1 + P(22);
                        g.DrawLine(pen, bx + (float)Math.Cos(ang) * r1, by + (float)Math.Sin(ang) * r1, bx + (float)Math.Cos(ang) * r2, by + (float)Math.Sin(ang) * r2);
                    }
            var rr = new Random(f.Seed);
            if (pa < 1.2)
                using (var br = new SolidBrush(A(f.Paint, 1 - pa / 1.2)))
                    for (int i = 0; i < 10; i++)
                    {
                        double ang = i * 0.628 + rr.NextDouble() * 0.5, v = P(260) + rr.NextDouble() * P(220);
                        float px = bx + (float)(Math.Cos(ang) * v * pa), py = by + (float)(Math.Sin(ang) * v * pa + 0.5 * P(900) * pa * pa);
                        var st = g.Save();
                        g.TranslateTransform(px, py); g.RotateTransform((float)(pa * 600 + i * 40));
                        g.FillRectangle(br, -P(6), -P(3), P(12), P(6));
                        g.Restore(st);
                    }
            Outlined(g, L.T("PAT!"), fBig, Gold, bx, by - P(30) - (float)pa * P(30), 1 - pa / 0.9);
            if (f.By != null) Tag(g, L.F("{0} patlattı", f.By), f.Col, bx, by + P(20), 1 - pa / 1.5);
            var s2 = g.Save();
            g.TranslateTransform(bx, cardTop + (float)(0.5 * P(1600) * pa * pa));
            g.RotateTransform((float)(pa * 40 * f.Dir));
            var sz2 = CardSize(g, f, P(270));
            CardDraw(g, f, new RectangleF(-sz2.Width / 2, 0, sz2.Width, sz2.Height), 1 - pa / 1.5, false);
            g.Restore(s2);
        }

        // ---------- uçak pankartı ----------

        void DrawPlane(Graphics g, Fx f, double a)
        {
            if (f.Lay == null)
            {
                f.Lay = Rich.Layout(g, f.Toks, fBanner, P(720), 1);
                f.NameW = g.MeasureString(f.User + ":", fBannerName, PointF.Empty, StringFormat.GenericTypographic).Width;
            }
            float bannerW = f.NameW + P(10) + f.Lay.W + P(46), bh = P(48), planeL = P(112), rope = P(70);
            float travel = W + bannerW + planeL + rope + P(140);
            double k = a / f.Life;
            int dir = f.Dir;
            float nose = dir > 0 ? -P(60) + travel * (float)k : W + P(60) - travel * (float)k;
            float y = f.Y + (float)Math.Sin(a * 2.1) * P(7);
            float tail = nose - dir * planeL, ropeEnd = tail - dir * rope;
            float bL = dir > 0 ? ropeEnd - bannerW : ropeEnd, near = dir > 0 ? bL + bannerW : bL;
            using (var pen = new Pen(Color.FromArgb(200, 210, 214, 222), P(1.5f)))
            {
                g.DrawLine(pen, tail, y, near, y - bh / 2 + P(5));
                g.DrawLine(pen, tail, y, near, y + bh / 2 - P(5));
            }
            // Dalgalanan bez: uca doğru daha çok dalgalanır.
            int n = 28;
            var top = new PointF[n + 1]; var bot = new PointF[n + 1];
            for (int i = 0; i <= n; i++)
            {
                float u = i / (float)n, x = bL + bannerW * u;
                float free = dir > 0 ? 1 - u : u;
                float wave = (float)Math.Sin(i * 0.7 - a * 7 * dir) * P(5) * (0.2f + free);
                top[i] = new PointF(x, y - bh / 2 + wave); bot[n - i] = new PointF(x, y + bh / 2 + wave);
            }
            using (var path = new GraphicsPath())
            {
                path.AddLines(top); path.AddLines(bot); path.CloseFigure();
                using (var br = new SolidBrush(Color.FromArgb(245, 255, 248, 230))) g.FillPath(br, path);
                using (var pen = new Pen(f.Paint, P(3))) g.DrawPath(pen, path);
            }
            float tx = bL + P(22), ty = y - fBanner.GetHeight(g) / 2 + (float)Math.Sin(n / 2 * 0.7 - a * 7 * dir) * P(2);
            using (var br = new SolidBrush(Mix(f.Col, Color.Black, 0.4f))) g.DrawString(f.User + ":", fBannerName, br, tx, ty, StringFormat.GenericTypographic);
            Rich.Draw(g, f.Lay, fBanner, Ink, tx + f.NameW + P(10), ty, 1);
            var st = g.Save();
            g.TranslateTransform(nose, y);
            if (dir < 0) g.ScaleTransform(-1, 1);
            PlaneBody(g, planeL, f.Paint, a);
            g.Restore(st);
        }

        void PlaneBody(Graphics g, float len, Color c, double a)
        {
            using (var br = new SolidBrush(Mix(c, Color.Black, 0.3f)))
            {
                g.FillPolygon(br, new[] { new PointF(-len + P(4), -P(4)), new PointF(-len - P(2), -P(34)), new PointF(-len + P(16), -P(34)), new PointF(-len + P(36), -P(6)) });
                g.FillPolygon(br, new[] { new PointF(-P(64), P(2)), new PointF(-P(34), P(2)), new PointF(-P(52), P(28)), new PointF(-P(78), P(28)) });
            }
            var body = new RectangleF(-len, -P(14), len, P(28));
            using (var br = new LinearGradientBrush(body, Mix(c, Color.White, 0.35f), Mix(c, Color.Black, 0.25f), 90f))
            using (var p = Rounded(body, P(13))) g.FillPath(br, p);
            using (var br = new SolidBrush(Color.FromArgb(230, 170, 220, 255))) g.FillEllipse(br, -P(40), -P(11), P(20), P(10));
            using (var br = new SolidBrush(Color.FromArgb(60, 255, 255, 255))) g.FillRectangle(br, -len + P(12), -P(9), len - P(30), P(3));
            float blade = P(26) * (float)Math.Abs(Math.Sin(a * 50));
            using (var br = new SolidBrush(Color.FromArgb(150, 220, 224, 230))) g.FillEllipse(br, P(1), -blade, P(5), blade * 2);
            using (var br = new SolidBrush(Color.FromArgb(255, 40, 44, 52))) g.FillEllipse(br, -P(3), -P(4), P(8), P(8));
        }

        // ---------- şişede mesaj ----------

        void Waves(Graphics g, float cx, float wy, float span, double a, double alpha, bool front)
        {
            if (alpha <= 0.01) return;
            var pts = new List<PointF>();
            float x0 = cx - span / 2, x1 = cx + span / 2;
            for (float x = x0; x <= x1 + 1; x += P(8))
                pts.Add(new PointF(x, wy + (front ? P(16) : 0) + (float)Math.Sin(x / s * 0.03 + a * (front ? 3.1 : 2.3) + (front ? 1.3 : 0)) * P(7)));
            pts.Add(new PointF(x1, H)); pts.Add(new PointF(x0, H));
            var rect = new RectangleF(x0, wy - P(20), span, H - wy + P(20));
            var c = front ? Color.FromArgb((int)(150 * alpha), 70, 160, 225) : Color.FromArgb((int)(170 * alpha), 30, 95, 165);
            using (var br = new LinearGradientBrush(rect, c, c, 0f))
            {
                br.InterpolationColors = new ColorBlend { Colors = new[] { Color.FromArgb(0, c), c, c, Color.FromArgb(0, c) }, Positions = new[] { 0f, 0.2f, 0.8f, 1f } };
                g.FillPolygon(br, pts.ToArray());
            }
            if (front)
                using (var pen = new Pen(Color.FromArgb((int)(120 * alpha), 230, 245, 255), P(2))) g.DrawLines(pen, pts.Take(pts.Count - 2).Skip(4).Take(Math.Max(2, pts.Count - 10)).ToArray());
        }

        void DrawBottle(Graphics g, Fx f, double a)
        {
            float wy = H - P(66), span = P(620);
            double fade = Math.Max(0, Math.Min(1, Math.Min(a / 0.6, (f.Life - a) / 0.8)));
            Waves(g, f.X, wy, span, a, fade, false);
            float from = f.X < W / 2 ? f.X - span / 2 - P(40) : f.X + span / 2 + P(40);
            float bx = Lerp(from, f.X, Ease(a / 2.4));
            float sink = a > f.Life - 1.4 ? (float)(a - (f.Life - 1.4)) * P(70) : 0;
            float by = wy - P(4) + (float)Math.Sin(a * 3) * P(5) + sink;
            float ang = -18 + (float)Math.Sin(a * 2.2) * 6;
            var st = g.Save();
            g.TranslateTransform(bx, by); g.RotateTransform(ang);
            BottleBody(g, a < 2.4, a < 2.7, fade);
            g.Restore(st);
            Waves(g, f.X, wy, span, a, fade * 0.8, true);
            if (a >= 2.4 && a < 3.6)
            {
                double c = a - 2.4, rad = ang * Math.PI / 180;
                float nx = bx + (float)Math.Cos(rad) * P(58), ny = by + (float)Math.Sin(rad) * P(58);
                float cx = nx + (float)(P(90) * c), cy = ny - (float)(P(420) * c - 0.5 * P(900) * c * c);
                var s2 = g.Save();
                g.TranslateTransform(cx, cy); g.RotateTransform((float)(c * 720));
                using (var br = new SolidBrush(A(ColorTranslator.FromHtml("#a0703c"), 1 - c / 1.2))) g.FillRectangle(br, -P(6), -P(7), P(12), P(14));
                g.Restore(s2);
                Outlined(g, L.T("POP!"), fSmall, Color.White, nx, ny - P(40), 1 - c / 0.6);
            }
            if (a >= 2.6)
            {
                double u = Ease((a - 2.6) / 0.8), al = Math.Max(0, Math.Min(1, (f.Life - 1.2 - a) / 0.6));
                var sz = CardSize(g, f, P(330));
                float cx = Math.Max(sz.Width / 2 + P(10), Math.Min(W - sz.Width / 2 - P(10), f.X));
                float bottom = wy - P(46), h = sz.Height * (float)u;
                var r = new RectangleF(cx - sz.Width / 2, bottom - sz.Height, sz.Width, sz.Height);
                var s3 = g.Save();
                g.SetClip(new RectangleF(r.X - P(10), bottom - h, r.Width + P(20), h + P(10)));
                CardDraw(g, f, r, al, true);
                g.Restore(s3);
                // Parşömen ruloları: açılan kısmın üstünde ve altında
                using (var br = new SolidBrush(A(ColorTranslator.FromHtml("#d9c08a"), al)))
                {
                    foreach (float ry in new[] { bottom - h - P(6), bottom - P(4) })
                        using (var p = Rounded(new RectangleF(r.X - P(8), ry, r.Width + P(16), P(10)), P(5))) g.FillPath(br, p);
                }
            }
        }

        void BottleBody(Graphics g, bool corked, bool paperIn, double alpha)
        {
            if (paperIn) using (var br = new SolidBrush(A(ColorTranslator.FromHtml("#efe0b9"), alpha))) g.FillRectangle(br, -P(34), -P(9), P(48), P(18));
            var body = new RectangleF(-P(46), -P(19), P(74), P(38));
            using (var p = Rounded(body, P(14)))
            {
                using (var br = new SolidBrush(Color.FromArgb((int)(150 * alpha), 70, 160, 115))) g.FillPath(br, p);
                using (var pen = new Pen(Color.FromArgb((int)(200 * alpha), 150, 220, 180), P(2))) g.DrawPath(pen, p);
            }
            using (var br = new SolidBrush(Color.FromArgb((int)(170 * alpha), 70, 160, 115))) g.FillRectangle(br, P(26), -P(8), P(28), P(16));
            if (corked) using (var br = new SolidBrush(A(ColorTranslator.FromHtml("#a0703c"), alpha))) g.FillRectangle(br, P(52), -P(7), P(11), P(14));
            using (var pen = new Pen(Color.FromArgb((int)(110 * alpha), 255, 255, 255), P(3)) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(pen, -P(34), -P(12), P(14), -P(12));
        }

        // ---------- güvercin ----------

        PointF PigeonAt(Fx f, double a)
        {
            float sx = f.Dir > 0 ? -P(90) : W + P(90), ex = f.Dir > 0 ? W + P(120) : -P(120);
            var hover = new PointF(f.X, f.Y - P(120));
            if (a < 1.8) { double k = Ease(a / 1.8); return new PointF(Lerp(sx, hover.X, k), Lerp(-P(70), hover.Y, k) - (float)Math.Sin(Math.PI * k) * P(40)); }
            if (a < 2.3) return new PointF(hover.X, hover.Y + (float)Math.Sin(a * 6) * P(4));
            double q = Math.Min(1, (a - 2.3) / 2.3);
            return new PointF(Lerp(hover.X, ex, q * q), Lerp(hover.Y, -P(140), q));
        }

        void DrawPigeon(Graphics g, Fx f, double a)
        {
            double al = Math.Max(0, Math.Min(1, (f.Life - a) / 0.8));
            var spot = new PointF(f.X, f.Y);
            // Zarf: güvercinin altında taşınır, 2. saniyede bırakılır, yere düşüp açılır.
            float ew = P(76), eh = P(50);
            PointF ec; float esc = 1;
            if (a < 2.0) { var p = PigeonAt(f, a); ec = new PointF(p.X, p.Y + P(30)); esc = 0.55f; }
            else if (a < 2.35) { var p = PigeonAt(f, 2.0); double k = (a - 2.0) / 0.35; ec = new PointF(Lerp(p.X, spot.X, k), Lerp(p.Y + P(30), spot.Y, k * k)); esc = Lerp(0.55f, 1, k); }
            else ec = spot;
            float open = a < 2.35 ? 0 : Ease((a - 2.35) / 0.35);
            RectangleF card = RectangleF.Empty;
            if (a >= 2.6)
            {
                double k = Ease((a - 2.6) / 0.5);
                var sz = CardSize(g, f, P(320));
                float top = ec.Y - eh / 2 - sz.Height * (float)k + P(10);
                card = Card(g, f, ec.X, top, P(320), al * Math.Min(1, (a - 2.6) / 0.25), true);
            }
            Envelope(g, ec, ew * esc, eh * esc, open, al);
            // Sürpriz: uçup giderken kartın üstüne bırakır.
            if (a >= 3.0 && !card.IsEmpty)
            {
                var p0 = PigeonAt(f, 3.0);
                float tx = card.X + card.Width * 0.72f, ty = card.Y + P(16);
                double fall = Math.Sqrt(2 * Math.Max(P(10), ty - p0.Y) / P(1500));
                double u = Math.Min(1, (a - 3.0) / fall);
                float px = Lerp(p0.X, tx, u), py = Lerp(p0.Y, ty, u * u);
                if (u < 1) using (var br = new SolidBrush(A(Color.White, al))) g.FillEllipse(br, px - P(5), py - P(7), P(10), P(14));
                else Splat(g, tx, ty, f.Seed, al);
            }
            if (a < 4.6)
            {
                var p = PigeonAt(f, a);
                var st = g.Save();
                g.TranslateTransform(p.X, p.Y);
                bool right = f.Dir > 0;
                if (!right) g.ScaleTransform(-1, 1);
                PigeonBody(g, a);
                g.Restore(st);
            }
        }

        void Splat(Graphics g, float x, float y, int seed, double alpha)
        {
            var r = new Random(seed);
            var pts = new PointF[11];
            for (int i = 0; i < pts.Length; i++)
            {
                double ang = i * Math.PI * 2 / pts.Length;
                float rr = P(10) + (float)r.NextDouble() * P(8);
                pts[i] = new PointF(x + (float)Math.Cos(ang) * rr * 1.3f, y + (float)Math.Sin(ang) * rr);
            }
            using (var br = new SolidBrush(A(Color.FromArgb(250, 248, 246, 238), alpha))) g.FillClosedCurve(br, pts);
            using (var pen = new Pen(A(Color.FromArgb(160, 140, 140, 130), alpha), P(1.5f))) g.DrawClosedCurve(pen, pts);
            using (var br = new SolidBrush(A(Color.FromArgb(200, 120, 110, 95), alpha))) g.FillEllipse(br, x - P(4), y - P(3), P(8), P(6));
            using (var br = new SolidBrush(A(Color.FromArgb(240, 248, 246, 238), alpha)))
                for (int i = 0; i < 2; i++) g.FillEllipse(br, x - P(8) + i * P(12), y + P(8), P(5), P(9 + 4 * i));
        }

        void Envelope(Graphics g, PointF c, float w, float h, float open, double alpha)
        {
            var r = new RectangleF(c.X - w / 2, c.Y - h / 2, w, h);
            using (var br = new SolidBrush(A(ColorTranslator.FromHtml("#f4ead2"), alpha))) g.FillRectangle(br, r);
            using (var pen = new Pen(A(ColorTranslator.FromHtml("#b89a5e"), alpha), P(1.6f)))
            {
                g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                g.DrawLine(pen, r.Left, r.Bottom, c.X, c.Y + h * 0.05f);
                g.DrawLine(pen, r.Right, r.Bottom, c.X, c.Y + h * 0.05f);
                // Kapak: kapalıyken aşağı, açılınca yukarı döner.
                float tipY = r.Top + h * 0.55f * (1 - 2 * open);
                var flap = new[] { new PointF(r.Left, r.Top), new PointF(r.Right, r.Top), new PointF(c.X, tipY) };
                using (var br = new SolidBrush(A(ColorTranslator.FromHtml("#eadcb8"), alpha))) g.FillPolygon(br, flap);
                g.DrawPolygon(pen, flap);
            }
            if (open < 0.5) using (var br = new SolidBrush(A(ColorTranslator.FromHtml("#c8323c"), alpha))) g.FillEllipse(br, c.X - P(6), c.Y - P(2), P(12), P(12));
        }

        void PigeonBody(Graphics g, double a)
        {
            using (var br = new SolidBrush(ColorTranslator.FromHtml("#5d6370"))) g.FillPolygon(br, new[] { new PointF(-P(24), -P(2)), new PointF(-P(46), -P(10)), new PointF(-P(46), P(7)) });
            using (var br = new SolidBrush(ColorTranslator.FromHtml("#9aa1ad"))) g.FillEllipse(br, -P(28), -P(15), P(52), P(30));
            using (var br = new SolidBrush(ColorTranslator.FromHtml("#b9bfc8"))) g.FillEllipse(br, -P(12), -P(2), P(30), P(14));
            using (var br = new SolidBrush(Color.FromArgb(170, 120, 90, 170))) g.FillEllipse(br, P(8), -P(14), P(16), P(14));
            using (var br = new SolidBrush(ColorTranslator.FromHtml("#737a88"))) g.FillEllipse(br, P(12), -P(26), P(22), P(22));
            using (var br = new SolidBrush(ColorTranslator.FromHtml("#f0a030"))) g.FillPolygon(br, new[] { new PointF(P(32), -P(18)), new PointF(P(43), -P(14)), new PointF(P(32), -P(11)) });
            using (var br = new SolidBrush(Color.White)) g.FillEllipse(br, P(22), -P(21), P(6), P(6));
            using (var br = new SolidBrush(Color.Black)) g.FillEllipse(br, P(24), -P(20), P(3.5f), P(3.5f));
            var st = g.Save();
            g.TranslateTransform(-P(2), -P(8));
            g.RotateTransform((float)(-Math.Sin(a * 22) * 38 - 8));
            using (var br = new SolidBrush(ColorTranslator.FromHtml("#c3c8d0"))) g.FillEllipse(br, -P(32), -P(9), P(38), P(17));
            using (var pen = new Pen(ColorTranslator.FromHtml("#7d8492"), P(1.2f))) g.DrawArc(pen, -P(30), -P(6), P(30), P(10), 180, 120);
            g.Restore(st);
        }

        // ---------- kağıt uçak ----------

        const double PaperFlight = 3.2;

        PointF PaperAt(Fx f, double t)
        {
            double k = Math.Max(0, Math.Min(1, t / PaperFlight));
            float x0 = f.Dir > 0 ? -P(40) : W + P(40), x3 = f.Dir > 0 ? W - P(24) : P(24);
            float y0 = H * 0.8f, y3 = f.Y;
            float x1 = Lerp(x0, x3, 0.35), y1 = H * 0.15f, x2 = Lerp(x0, x3, 0.7), y2 = H * 0.68f;
            double u = 1 - k;
            float x = (float)(u * u * u * x0 + 3 * u * u * k * x1 + 3 * u * k * k * x2 + k * k * k * x3);
            float y = (float)(u * u * u * y0 + 3 * u * u * k * y1 + 3 * u * k * k * y2 + k * k * k * y3);
            return new PointF(x, y + (float)Math.Sin(t * 5) * P(6));
        }

        void DrawPaper(Graphics g, Fx f, double a)
        {
            double al = Math.Max(0, Math.Min(1, (f.Life - a) / 1.0));
            if (a < PaperFlight)
            {
                var cp = PaperAt(f, Math.Max(0, a - 0.5));
                Card(g, f, cp.X, cp.Y + P(26), P(250), Math.Min(1, a / 0.3), false);
                var p = PaperAt(f, a); var q = PaperAt(f, Math.Max(0, a - 0.04));
                float ang = (float)(Math.Atan2(p.Y - q.Y, p.X - q.X) * 180 / Math.PI);
                using (var pen = new Pen(Color.FromArgb(90, 255, 255, 255), P(1.5f)) { DashPattern = new[] { 2f, 3f } })
                {
                    var tr = Enumerable.Range(0, 8).Select(i => PaperAt(f, Math.Max(0, a - 0.05 * i))).ToArray();
                    g.DrawLines(pen, tr);
                }
                var st = g.Save();
                g.TranslateTransform(p.X, p.Y); g.RotateTransform(ang);
                using (var br = new SolidBrush(Color.FromArgb(250, 250, 251, 253))) g.FillPolygon(br, new[] { new PointF(P(28), 0), new PointF(-P(24), -P(15)), new PointF(-P(10), 0) });
                using (var br = new SolidBrush(ColorTranslator.FromHtml("#d5dae2"))) g.FillPolygon(br, new[] { new PointF(P(28), 0), new PointF(-P(24), P(11)), new PointF(-P(10), 0) });
                using (var pen = new Pen(ColorTranslator.FromHtml("#9aa3b2"), P(1))) g.DrawLine(pen, P(28), 0, -P(10), 0);
                g.Restore(st);
                return;
            }
            double c = a - PaperFlight;
            var hit = PaperAt(f, PaperFlight);
            // Çarpma yıldızları
            if (c < 1.3)
                for (int i = 0; i < 5; i++)
                {
                    double ang = c * 5 + i * Math.PI * 2 / 5;
                    Star(g, hit.X - f.Dir * P(30) + (float)Math.Cos(ang) * P(30), hit.Y - P(34) + (float)Math.Sin(ang) * P(9), P(7), A(Gold, 1 - c / 1.3));
                }
            // Kart: olduğu yerden sallanarak süzülür.
            var cp0 = PaperAt(f, PaperFlight - 0.5);
            var s1 = g.Save();
            g.TranslateTransform(cp0.X + (float)Math.Sin(c * 3) * P(30), cp0.Y + P(26) + (float)c * P(55));
            g.RotateTransform((float)Math.Sin(c * 3) * 10);
            var sz = CardSize(g, f, P(250));
            CardDraw(g, f, new RectangleF(-sz.Width / 2, 0, sz.Width, sz.Height), al, false);
            g.Restore(s1);
            // Buruşmuş top: düşer, bir kez seker.
            float floor = H - P(40), gr = P(1800);
            double t1 = Math.Sqrt(2 * Math.Max(1, floor - hit.Y) / gr);
            float by;
            if (c < t1) by = hit.Y + (float)(0.5 * gr * c * c);
            else { double c2 = c - t1, v = gr * t1 * 0.4; by = floor - (float)Math.Max(0, v * c2 - 0.5 * gr * c2 * c2); }
            float bx = hit.X - f.Dir * (float)(c * P(70));
            var r = new Random(f.Seed);
            var pts = Enumerable.Range(0, 9).Select(i => { double an = i * Math.PI * 2 / 9; float rr = P(14) + (float)r.NextDouble() * P(6); return new PointF((float)Math.Cos(an) * rr, (float)Math.Sin(an) * rr); }).ToArray();
            var s2 = g.Save();
            g.TranslateTransform(bx, by); g.RotateTransform((float)(c * 400 * f.Dir));
            using (var br = new SolidBrush(A(Color.FromArgb(250, 245, 246, 248), al))) g.FillPolygon(br, pts);
            using (var pen = new Pen(A(ColorTranslator.FromHtml("#9aa3b2"), al), P(1.2f)))
            {
                g.DrawPolygon(pen, pts);
                g.DrawLine(pen, pts[0], pts[4]); g.DrawLine(pen, pts[2], pts[6]); g.DrawLine(pen, pts[7], pts[3]);
            }
            g.Restore(s2);
        }

        // ---------- duvar yazısı ----------

        void DrawSpray(Graphics g, Fx f, double a)
        {
            double sa = a - f.Delay;
            if (sa < 0) return;
            if (f.Path == null)
            {
                string text = Rich.Plain(f.Text).Trim();
                var lines = new List<string> { "" };
                foreach (var w in text.Split(' '))
                {
                    if (lines[lines.Count - 1].Length + w.Length > 20 && lines[lines.Count - 1] != "") { if (lines.Count == 2) { lines[1] += "…"; break; } lines.Add(""); }
                    lines[lines.Count - 1] += (lines[lines.Count - 1] == "" ? "" : " ") + w;
                }
                var path = new GraphicsPath();
                using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                    path.AddString(string.Join("\n", lines), fTitle.FontFamily, (int)fTitle.Style, P(64), PointF.Empty, sf);
                var b = path.GetBounds();
                float k = Math.Min(1, P(560) / Math.Max(1, b.Width));
                using (var m = new Matrix()) { m.Scale(k, k); m.Translate(-(b.X + b.Width / 2), -(b.Y + b.Height / 2)); path.Transform(m); }
                b = path.GetBounds();
                f.Path = path; f.PW = b.Width; f.PH = b.Height;
            }
            double al = Math.Max(0, Math.Min(1, (f.Life - sa) / 1.0));
            double ca = f.Ev >= 0 ? a - f.Ev : -1;
            if (ca >= 0) al *= Math.Max(0, 1 - Math.Max(0, ca - 0.45) / 1.2);
            if (al <= 0.01) return;
            var r = new Random(f.Seed);
            var st = g.Save();
            g.TranslateTransform(f.X, f.Y);
            g.RotateTransform((float)(r.NextDouble() * 12 - 6));
            float left = -f.PW / 2 - P(12), full = f.PW + P(24);
            double rev = Math.Min(1, sa / 1.5);
            float edge = left + full * (float)rev;
            var clip = g.Save();
            g.SetClip(new RectangleF(left, -f.PH, edge - left, f.PH * 2 + P(80)));
            using (var m = new Matrix())
            {
                m.Translate(P(4), P(5));
                using (var sh = (GraphicsPath)f.Path.Clone()) { sh.Transform(m); using (var br = new SolidBrush(Color.FromArgb((int)(110 * al), 0, 0, 0))) g.FillPath(br, sh); }
            }
            using (var pen = new Pen(Color.FromArgb((int)(240 * al), 10, 10, 14), P(7)) { LineJoin = LineJoin.Round }) g.DrawPath(pen, f.Path);
            var rect = new RectangleF(-f.PW / 2, -f.PH / 2, f.PW, f.PH);
            using (var br = new LinearGradientBrush(rect, A(Mix(f.Paint, Color.White, 0.4f), al), A(Mix(f.Paint, Color.Black, 0.2f), al), 90f)) g.FillPath(br, f.Path);
            // Akıntılar: harflerin altından aşağı uzar.
            using (var br = new SolidBrush(A(f.Paint, al)))
                for (int i = 0; i < 5; i++)
                {
                    float dx = -f.PW / 2 + (float)r.NextDouble() * f.PW, len = (P(10) + (float)r.NextDouble() * P(34)) * (float)Math.Max(0, Math.Min(1, (sa - 1.2) / 2.5));
                    if (len < 1) continue;
                    g.FillRectangle(br, dx, f.PH / 2 - P(6), P(4), len);
                    g.FillEllipse(br, dx - P(1.5f), f.PH / 2 - P(6) + len - P(2), P(7), P(7));
                }
            g.Restore(clip);
            // Sprey buğusu: açılan kenarda
            if (rev < 1)
            {
                var mist = new Random(f.Seed + (int)(sa * 30));
                using (var br = new SolidBrush(A(f.Paint, 0.55 * al)))
                    for (int i = 0; i < 34; i++)
                    {
                        float d = P(2) + (float)mist.NextDouble() * P(3);
                        g.FillEllipse(br, edge + (float)(mist.NextDouble() - 0.5) * P(30), (float)(mist.NextDouble() - 0.5) * (f.PH + P(20)), d, d);
                    }
            }
            if (rev >= 1)
            {
                string by = "— " + f.User;
                var sz = g.MeasureString(by, fSmall, PointF.Empty, StringFormat.GenericTypographic);
                using (var br = new SolidBrush(A(Readable(f.Col), al * Math.Min(1, (sa - 1.5) / 0.4)))) g.DrawString(by, fSmall, br, f.PW / 2 - sz.Width, f.PH / 2 + P(10), StringFormat.GenericTypographic);
            }
            if (ca >= 0)
            {
                double k = Math.Min(1, ca / 0.4);
                using (var pen = new Pen(Color.FromArgb((int)(240 * al), 255, 48, 64), P(10)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(pen, left, -f.PH / 2, left + full * (float)k, f.PH / 2);
                    if (k >= 1) g.DrawLine(pen, left, f.PH / 2, left + full * (float)Math.Min(1, (ca - 0.4) / 0.3 + 0.001), -f.PH / 2);
                }
            }
            g.Restore(st);
        }

        // ---------- domates / yumurta ----------

        void DrawThrow(Graphics g, Fx f, double a)
        {
            bool egg = f.Kind == "egg";
            const double T = 0.55;
            if (a < T)
            {
                double k = a / T;
                float x = Lerp(f.X2, f.X, k), y = Lerp(H + P(60), f.Y, k) - (float)Math.Sin(Math.PI * k) * P(180), sz = P(16 + 30 * (float)k);
                var st = g.Save();
                g.TranslateTransform(x, y); g.RotateTransform((float)(a * 720));
                if (egg)
                    using (var br = new SolidBrush(Color.FromArgb(250, 250, 246, 235))) g.FillEllipse(br, -sz * 0.4f, -sz / 2, sz * 0.8f, sz);
                else
                {
                    using (var br = new SolidBrush(ColorTranslator.FromHtml("#e0301e"))) g.FillEllipse(br, -sz / 2, -sz / 2, sz, sz);
                    Star(g, 0, -sz * 0.42f, sz * 0.25f, ColorTranslator.FromHtml("#3c9a3c"));
                }
                g.Restore(st);
                return;
            }
            double c = a - T, al = Math.Max(0, Math.Min(1, (f.Life - a) / 1.0));
            var r = new Random(f.Seed);
            if (f.Blob == null)
                f.Blob = Enumerable.Range(0, 14).Select(i => { double an = i * Math.PI * 2 / 14; float rr = (egg ? P(60) : P(72)) * (0.7f + 0.45f * (float)r.NextDouble()); return new PointF((float)Math.Cos(an) * rr, (float)Math.Sin(an) * rr * 0.85f); }).ToArray();
            float slide = (float)(c * P(9));
            var s0 = g.Save();
            g.TranslateTransform(f.X, f.Y + slide);
            float pop = c < 0.12 ? (float)(0.6 + 0.4 * c / 0.12) : 1;
            g.ScaleTransform(pop, pop);
            var main = egg ? Color.FromArgb(250, 247, 243, 232) : ColorTranslator.FromHtml("#d42a1e");
            using (var br = new SolidBrush(A(main, al)))
            {
                for (int i = 0; i < 9; i++)
                {
                    double an = r.NextDouble() * Math.PI * 2; float d = P(95) + (float)r.NextDouble() * P(60), sz = P(5) + (float)r.NextDouble() * P(10);
                    g.FillEllipse(br, (float)Math.Cos(an) * d - sz / 2, (float)Math.Sin(an) * d - sz / 2, sz, sz);
                }
                for (int i = 0; i < 3; i++)
                {
                    float dx = -P(35) + i * P(30) + (float)r.NextDouble() * P(10), len = (float)Math.Min(P(110), c * P(36) * (0.6 + r.NextDouble()));
                    g.FillRectangle(br, dx, P(30), P(10), len + P(10));
                    g.FillEllipse(br, dx - P(2), P(30) + len + P(4), P(14), P(14));
                }
                g.FillClosedCurve(br, f.Blob);
            }
            if (egg)
            {
                using (var pen = new Pen(A(Color.FromArgb(200, 214, 206, 186), al), P(2))) g.DrawClosedCurve(pen, f.Blob);
                float yolk = P(27), yy = (float)(c * P(7));
                var yr = new RectangleF(P(4) - yolk, -P(6) - yolk + yy, yolk * 2, yolk * 2);
                using (var br = new LinearGradientBrush(yr, A(ColorTranslator.FromHtml("#ffcf3a"), al), A(ColorTranslator.FromHtml("#f08c0b"), al), 60f)) g.FillEllipse(br, yr);
                using (var br = new SolidBrush(A(Color.FromArgb(160, 255, 255, 255), al))) g.FillEllipse(br, yr.X + yolk * 0.4f, yr.Y + yolk * 0.35f, yolk * 0.5f, yolk * 0.35f);
                if (c < 0.7)
                    using (var br = new SolidBrush(A(Color.White, 1 - c / 0.7)))
                        for (int i = 0; i < 6; i++)
                        {
                            double an = i * 1.05 + 0.3; float d = (float)(P(60) + c * P(260));
                            float px = (float)Math.Cos(an) * d, py = (float)(Math.Sin(an) * d + 0.5 * P(900) * c * c);
                            g.FillPolygon(br, new[] { new PointF(px, py - P(8)), new PointF(px + P(9), py + P(6)), new PointF(px - P(7), py + P(5)) });
                        }
            }
            else
            {
                var inner = f.Blob.Select(p => new PointF(p.X * 0.6f, p.Y * 0.6f)).ToArray();
                using (var br = new SolidBrush(A(ColorTranslator.FromHtml("#ef4b3a"), al))) g.FillClosedCurve(br, inner);
                using (var br = new SolidBrush(A(ColorTranslator.FromHtml("#f6d28b"), al)))
                    for (int i = 0; i < 8; i++)
                    {
                        double an = r.NextDouble() * Math.PI * 2; float d = (float)r.NextDouble() * P(34);
                        var st = g.Save();
                        g.TranslateTransform((float)Math.Cos(an) * d, (float)Math.Sin(an) * d); g.RotateTransform((float)(an * 57));
                        g.FillEllipse(br, -P(4), -P(2.5f), P(8), P(5));
                        g.Restore(st);
                    }
                using (var br = new SolidBrush(A(Color.FromArgb(90, 255, 255, 255), al))) g.FillEllipse(br, -P(30), -P(36), P(26), P(14));
            }
            g.Restore(s0);
            Outlined(g, egg ? L.T("ÇAT!") : L.T("ŞLAP!"), fBig, egg ? Color.White : Gold, f.X, f.Y - P(120), 1 - c / 0.7);
            Tag(g, f.User, f.Col, f.X, f.Y + slide + P(92), Math.Min(al, Math.Max(0, Math.Min(1, (3.5 - c) / 0.5))));
        }

        // ---------- çıkartma ----------

        /// <summary>Kesilmiş çıkartma görünümü: beyaz kenar ve gölge bir kez hazırlanır.</summary>
        Bitmap StickerArt(Image img, float size)
        {
            float iw, ih;
            lock (img) { float k = size / Math.Max(img.Width, img.Height); iw = img.Width * k; ih = img.Height * k; }
            int pad = (int)Math.Ceiling(P(12));
            var bmp = new Bitmap((int)Math.Ceiling(iw) + pad * 2, (int)Math.Ceiling(ih) + pad * 2, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                var r = new RectangleF(pad, pad, iw, ih);
                lock (img)
                {
                    Tint(g, img, new RectangleF(r.X + P(3), r.Y + P(5), iw, ih), Color.FromArgb(90, 0, 0, 0));
                    for (int i = 0; i < 16; i++)
                    {
                        double an = i * Math.PI / 8;
                        Tint(g, img, new RectangleF(r.X + (float)Math.Cos(an) * P(5), r.Y + (float)Math.Sin(an) * P(5), iw, ih), Color.White);
                    }
                    g.DrawImage(img, r);
                }
            }
            return bmp;
        }

        static void Tint(Graphics g, Image img, RectangleF r, Color c)
        {
            var cm = new ColorMatrix(new[]
            {
                new float[] { 0, 0, 0, 0, 0 }, new float[] { 0, 0, 0, 0, 0 }, new float[] { 0, 0, 0, 0, 0 },
                new float[] { 0, 0, 0, c.A / 255f, 0 }, new float[] { c.R / 255f, c.G / 255f, c.B / 255f, 0, 1 },
            });
            using (var ia = new ImageAttributes())
            {
                ia.SetColorMatrix(cm);
                g.DrawImage(img, new[] { new PointF(r.Left, r.Top), new PointF(r.Right, r.Top), new PointF(r.Left, r.Bottom) }, new RectangleF(0, 0, img.Width, img.Height), GraphicsUnit.Pixel, ia);
            }
        }

        void DrawSticker(Graphics g, Fx f, double a)
        {
            float size = P(118);
            double sc = a < 0.22 ? 2.3 - 1.45 * (a / 0.22) : a < 0.4 ? 0.85 + 0.15 * ((a - 0.22) / 0.18) : 1;
            var r = new Random(f.Seed);
            float rot = (float)(r.NextDouble() * 28 - 14), fall = 0;
            double al = Math.Min(1, a / 0.1), end = f.Life - 1.3;
            if (a > end) { double e = a - end; rot += (float)(e * 70 * f.Dir); fall = (float)(e * e * P(700)); al = Math.Max(0, 1 - e / 1.3); }
            var st = g.Save();
            g.TranslateTransform(f.X, f.Y + fall); g.RotateTransform(rot); g.ScaleTransform((float)sc, (float)sc);
            if (f.Img != null)
            {
                if (f.Art == null) { var img = Assets.Icon(f.Img); if (img != null) f.Art = StickerArt(img, size); }
                if (f.Art != null) DrawImage(g, f.Art, new RectangleF(-f.Art.Width / 2f, -f.Art.Height / 2f, f.Art.Width, f.Art.Height), al);
            }
            else
            {
                string text = Rich.Plain(f.Text).Trim();
                if (text.Length > 16) text = text.Substring(0, 15) + "…";
                var sz = g.MeasureString(text, fBig, PointF.Empty, StringFormat.GenericTypographic);
                var box = new RectangleF(-sz.Width / 2 - P(18), -sz.Height / 2 - P(10), sz.Width + P(36), sz.Height + P(20));
                using (var p = Rounded(box, box.Height / 2))
                {
                    using (var br = new SolidBrush(A(f.Paint, al))) g.FillPath(br, p);
                    using (var pen = new Pen(A(Color.White, al), P(5))) g.DrawPath(pen, p);
                }
                using (var br = new SolidBrush(A(Color.White, al))) g.DrawString(text, fBig, br, -sz.Width / 2, -sz.Height / 2, StringFormat.GenericTypographic);
            }
            g.Restore(st);
            if (a > 0.3 && a < 4.6) Tag(g, f.User, f.Col, f.X, f.Y + size * 0.62f, Math.Min(1, Math.Min((a - 0.3) / 0.3, (4.6 - a) / 0.4)));
        }

        // ---------- emoji yağmuru ----------

        void DrawRain(Graphics g, Fx f, double a)
        {
            var img = Assets.Icon(f.Img);
            if (img == null) return;
            var r = new Random(f.Seed);
            double al = Math.Max(0, Math.Min(1, (f.Life - a) / 0.6));
            for (int i = 0; i < 46; i++)
            {
                double delay = r.NextDouble() * 4.2;
                float x = (float)r.NextDouble() * W, v = P(380) + (float)r.NextDouble() * P(320), sz = P(34) + (float)r.NextDouble() * P(28);
                double spin = (r.NextDouble() - 0.5) * 300, t = a - delay;
                if (t < 0) continue;
                float y = -sz + v * (float)t;
                if (y > H + sz) continue;
                var st = g.Save();
                g.TranslateTransform(x + (float)Math.Sin(t * 2 + i) * P(16), y); g.RotateTransform((float)(spin * t));
                float iw = sz;
                lock (img) iw = sz * img.Width / Math.Max(1, img.Height);
                DrawImage(g, img, new RectangleF(-iw / 2, -sz / 2, iw, sz), al);
                g.Restore(st);
            }
        }

        // ---------- sesli okuma: konuşan robot ----------

        void DrawSay(Graphics g, Fx f, double a)
        {
            bool talking = talk != null && talk.Busy;
            double inK = Ease(a / 0.35), outK = a > f.Life - 0.4 ? Math.Min(1, (a - (f.Life - 0.4)) / 0.4) : 0;
            float off = (float)((1 - inK) + outK) * P(300);
            float rx = W - P(100) + off, ry = H * 0.6f;
            // Konuşma balonu: robotun solunda
            var sz = CardSize(g, f, P(360));
            var box = new RectangleF(rx - P(80) - sz.Width, ry - P(70), sz.Width, sz.Height);
            using (var br = new SolidBrush(Color.FromArgb(220, 14, 16, 24)))
                g.FillPolygon(br, new[] { new PointF(box.Right - P(2), ry - P(46)), new PointF(rx - P(48), ry - P(22)), new PointF(box.Right - P(2), ry - P(28)) });
            CardDraw(g, f, box, 1, false);
            // Robot kafası
            var head = new RectangleF(rx - P(44), ry - P(40), P(88), P(76));
            using (var pen = new Pen(ColorTranslator.FromHtml("#5f7489"), P(3))) g.DrawLine(pen, rx, head.Top, rx, head.Top - P(18));
            bool blink = (a * 2 % 1) < 0.5;
            using (var br = new SolidBrush(blink ? ColorTranslator.FromHtml("#ff4655") : ColorTranslator.FromHtml("#8a2430"))) g.FillEllipse(br, rx - P(6), head.Top - P(28), P(12), P(12));
            using (var p = Rounded(head, P(16)))
            {
                using (var br = new LinearGradientBrush(head, ColorTranslator.FromHtml("#b3c4d6"), ColorTranslator.FromHtml("#5f7489"), 90f)) g.FillPath(br, p);
                using (var pen = new Pen(ColorTranslator.FromHtml("#2a3442"), P(2.5f))) g.DrawPath(pen, p);
            }
            float eyeH = (a % 3.2) < 0.12 ? P(2) : P(14);
            using (var br = new SolidBrush(ColorTranslator.FromHtml("#5ff0ff")))
                foreach (float ex in new[] { rx - P(20), rx + P(20) }) g.FillEllipse(br, ex - P(7), ry - P(14) - eyeH / 2, P(14), eyeH);
            float mh = talking ? P(5) + P(15) * (float)Math.Abs(Math.Sin(a * 16)) : P(5);
            var mouth = new RectangleF(rx - P(20), ry + P(14) - mh / 2, P(40), mh);
            using (var p = Rounded(mouth, Math.Min(mh / 2, P(4))))
            using (var br = new SolidBrush(ColorTranslator.FromHtml("#1b2330"))) g.FillPath(br, p);
            if (talking) using (var pen = new Pen(Color.FromArgb(200, 255, 255, 255), P(1.5f))) g.DrawLine(pen, mouth.Left + P(3), mouth.Top + P(2), mouth.Right - P(3), mouth.Top + P(2));
        }

        // ---------- ters ekran / büyüteç yazıları ----------

        void DrawBanner(Graphics g, Fx f, double a)
        {
            double al = Fade(a, f.Life, 0.25, 0.4);
            if (f.Kind == "flip")
            {
                Outlined(g, L.F("{0} ekranı ters çevirdi!", f.User), fBig, f.Paint, W / 2, H * 0.17f, al, W * 0.8f);
                var st = g.Save();
                g.TranslateTransform(W / 2, H * 0.29f); g.RotateTransform((float)(a * 360));
                using (var pen = new Pen(A(Color.White, al), P(5)) { EndCap = LineCap.ArrowAnchor, StartCap = LineCap.Round })
                {
                    g.DrawArc(pen, -P(26), -P(26), P(52), P(52), 20, 140);
                    g.DrawArc(pen, -P(26), -P(26), P(52), P(52), 200, 140);
                }
                g.Restore(st);
                return;
            }
            // Büyüteç: ffmpeg'in büyüttüğü kutunun (Streamer) çevresine çerçeve ve sap.
            int z = Math.Max(60, (int)(140 * (H * Up) / 1080.0)) * 2;
            float B = 2f * z / Up, pop = a < 0.2 ? Lerp(0.85f, 1, a / 0.2) : 1;
            var box = new RectangleF(W / 2 - B / 2 * pop, H / 2 - B / 2 * pop, B * pop, B * pop);
            var s2 = g.Save();
            g.TranslateTransform(box.Right - P(10), box.Bottom - P(10)); g.RotateTransform(45);
            using (var p = Rounded(new RectangleF(0, -P(14), P(150), P(28)), P(12)))
            {
                using (var br = new SolidBrush(A(ColorTranslator.FromHtml("#6b3f1f"), al))) g.FillPath(br, p);
                using (var pen = new Pen(A(ColorTranslator.FromHtml("#2d1a0c"), al), P(2.5f))) g.DrawPath(pen, p);
            }
            g.Restore(s2);
            var rim = RectangleF.Inflate(box, P(8), P(8));
            using (var p = Rounded(rim, P(26)))
            using (var br = new LinearGradientBrush(rim, A(ColorTranslator.FromHtml("#eef2f6"), al), A(ColorTranslator.FromHtml("#6d7785"), al), 45f))
            using (var pen = new Pen(br, P(16))) g.DrawPath(pen, p);
            using (var pen = new Pen(A(Color.FromArgb(160, 255, 255, 255), al), P(4)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(pen, box.X + P(14), box.Y + P(14), B * 0.5f, B * 0.5f, 190, 60);
            Tag(g, L.F("{0} büyüteçle bakıyor", f.User), f.Col, W / 2, rim.Bottom + P(14), al);
        }

        // ---------- havai fişek ----------

        void DrawMeter(Graphics g, DateTime now)
        {
            int count, goal; double pulse;
            lock (lk) { count = fwCount; goal = fwGoal; pulse = (now - fwPulse).TotalSeconds; }
            float w = P(26), x = W - P(60) - w / 2, top = H * 0.36f, h = H * 0.3f;
            var tube = new RectangleF(x, top, w, h);
            using (var p = Rounded(tube, w / 2))
            {
                using (var br = new SolidBrush(Color.FromArgb(190, 10, 12, 18))) g.FillPath(br, p);
                var st = g.Save();
                float fh = h * Math.Min(1, count / (float)goal);
                g.SetClip(p);
                var fill = new RectangleF(x, top + h - fh, w, fh + 1);
                if (fh > 1) using (var br = new LinearGradientBrush(new RectangleF(x, top, w, h), ColorTranslator.FromHtml("#ffe066"), ColorTranslator.FromHtml("#ff4655"), 90f)) g.FillRectangle(br, fill);
                g.Restore(st);
                using (var pen = new Pen(pulse < 0.6 ? A(Gold, 1 - pulse / 0.6) : Color.FromArgb(70, 255, 255, 255), pulse < 0.6 ? P(4) : P(1.5f))) g.DrawPath(pen, p);
            }
            // Roket
            float rx = x + w / 2, ry = top - P(30);
            using (var br = new SolidBrush(ColorTranslator.FromHtml("#ff4655")))
                g.FillPolygon(br, new[] { new PointF(rx, ry - P(18)), new PointF(rx + P(8), ry - P(6)), new PointF(rx + P(8), ry + P(12)), new PointF(rx - P(8), ry + P(12)), new PointF(rx - P(8), ry - P(6)) });
            using (var br = new SolidBrush(Color.White)) g.FillEllipse(br, rx - P(3.5f), ry - P(4), P(7), P(7));
            float flame = P(8) + P(5) * (float)Math.Abs(Math.Sin(now.Ticks / 1e6));
            using (var br = new SolidBrush(ColorTranslator.FromHtml("#ffb547"))) g.FillPolygon(br, new[] { new PointF(rx - P(5), ry + P(12)), new PointF(rx + P(5), ry + P(12)), new PointF(rx, ry + P(12) + flame) });
            Outlined(g, $"{count}/{goal}", fBig, Color.White, rx, top + h + P(8), 1);
            Outlined(g, "!firework", fSmall, Gold, rx, top + h + P(50), 1);
            if (pulse < 0.8) Outlined(g, "+1", fBig, Gold, rx, top - P(70) - (float)pulse * P(40), 1 - pulse / 0.8);
        }

        void DrawShow(Graphics g, DateTime now)
        {
            double t; int seed; List<string> names;
            lock (lk) { t = (now - fwShow).TotalSeconds; seed = fwSeed; names = fwNames; }
            var r = new Random(seed);
            for (int i = 0; i < 9; i++)
            {
                double t0 = i * 0.6 + r.NextDouble() * 0.25;
                float bx = W * (0.12f + 0.76f * (float)r.NextDouble()), byy = H * (0.12f + 0.26f * (float)r.NextDouble());
                var c = Palette[r.Next(Palette.Length)];
                int ps = r.Next();
                double rt = t - t0;
                if (rt < 0) continue;
                const double rise = 0.9;
                if (rt < rise)
                {
                    float y = Lerp(H + P(20), byy, Ease(rt / rise));
                    using (var pen = new Pen(A(c, 0.7), P(3)) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(pen, bx, y, bx, y + P(46));
                    using (var br = new SolidBrush(Color.White)) g.FillEllipse(br, bx - P(4), y - P(4), P(8), P(8));
                    continue;
                }
                double bt = rt - rise;
                if (bt > 1.9) continue;
                if (bt < 0.18) using (var br = new SolidBrush(A(Color.White, 0.8 * (1 - bt / 0.18)))) { float fr = P(40) * (float)(bt / 0.18); g.FillEllipse(br, bx - fr, byy - fr, fr * 2, fr * 2); }
                var pr = new Random(ps);
                double fade = 1 - bt / 1.9;
                for (int j = 0; j < 56; j++)
                {
                    double ang = j * Math.PI * 2 / 56 + pr.NextDouble() * 0.2, v = P(420) + pr.NextDouble() * P(300);
                    float d = (float)(v * (1 - Math.Exp(-2.2 * bt)) / 2.2), d0 = (float)(v * (1 - Math.Exp(-2.2 * Math.Max(0, bt - 0.12))) / 2.2);
                    float drop = (float)(0.5 * P(220) * bt * bt);
                    float px = bx + (float)Math.Cos(ang) * d, py = byy + (float)Math.Sin(ang) * d + drop;
                    var col = j % 4 == 0 ? Color.White : c;
                    // Kuyruk: parçacığın biraz önceki yerinden şimdiki yerine
                    using (var pen = new Pen(A(col, fade * 0.5), P(3)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawLine(pen, bx + (float)Math.Cos(ang) * d0, byy + (float)Math.Sin(ang) * d0 + drop, px, py);
                    float sz = P(6) * (float)(0.6 + 0.4 * Math.Sin(bt * 30 + j));
                    using (var br = new SolidBrush(A(col, fade))) g.FillEllipse(br, px - sz, py - sz, sz * 2, sz * 2);
                }
            }
            double al = Fade(t, 8.5, 0.4, 0.8);
            Outlined(g, L.T("CHAT HAVAİ FİŞEK PATLATTI!"), fTitle, Gold, W / 2, H * 0.22f, al, W * 0.85f);
            if (names.Count > 0)
            {
                string list = string.Join(", ", names.Take(6)) + (names.Count > 6 ? " +" + (names.Count - 6) : "");
                Outlined(g, L.F("Teşekkürler: {0}", list), fName, Color.White, W / 2, H * 0.22f + P(62), al, W * 0.8f);
            }
        }

        // ---------- ördek yarışı ----------

        void DrawRace(Graphics g, DateTime now, List<Duck> ds)
        {
            double r, join; bool ready; double winT;
            lock (lk) { r = (now - raceAt).TotalSeconds; join = joinSec; ready = raceReady; winT = raceWinT; }
            int n = Math.Max(1, ds.Count);
            float lane = P(34), x0 = W * 0.24f, x1 = W * 0.8f, top = H * 0.6f, nameW = P(140);
            double rt = r - join - 3;
            bool results = ready && rt >= winT;
            var panel = new RectangleF(x0 - nameW - P(18), top - P(48), x1 - x0 + nameW + P(36), P(48) + n * lane + P(12));
            double al = Math.Min(1, r / 0.3);
            if (ready && rt > raceEnd + 4.4) al = Math.Max(0, 1 - (rt - raceEnd - 4.4) / 0.6);
            using (var p = Rounded(panel, P(12)))
            {
                using (var br = new SolidBrush(Color.FromArgb((int)(205 * al), 10, 12, 18))) g.FillPath(br, p);
                using (var pen = new Pen(A(ColorTranslator.FromHtml("#ffd23f"), 0.7 * al), P(2))) g.DrawPath(pen, p);
            }
            using (var br = new SolidBrush(A(ColorTranslator.FromHtml("#ffd23f"), al))) g.DrawString(L.T("ÖRDEK YARIŞI"), fBig, br, panel.X + P(14), panel.Y + P(8), StringFormat.GenericTypographic);
            string status = r < join ? L.F("katılmak için !race yaz · {0}", (int)Math.Ceiling(join - r)) : "";
            if (status != "")
            {
                var sz = g.MeasureString(status, fName, PointF.Empty, StringFormat.GenericTypographic);
                using (var br = new SolidBrush(A(Color.White, al))) g.DrawString(status, fName, br, panel.Right - P(14) - sz.Width, panel.Y + P(16), StringFormat.GenericTypographic);
            }
            for (int i = 0; i < ds.Count; i++)
            {
                var d = ds[i];
                float y = top + i * lane;
                using (var br = new SolidBrush(i % 2 == 0 ? Color.FromArgb((int)(90 * al), 40, 120, 200) : Color.FromArgb((int)(60 * al), 40, 120, 200))) g.FillRectangle(br, x0, y, x1 - x0, lane - P(2));
                string name = d.User;
                while (name.Length > 3 && g.MeasureString(name, fName, PointF.Empty, StringFormat.GenericTypographic).Width > nameW) name = name.Substring(0, name.Length - 2) + "…";
                var nsz = g.MeasureString(name, fName, PointF.Empty, StringFormat.GenericTypographic);
                using (var br = new SolidBrush(A(Readable(d.Col), al))) g.DrawString(name, fName, br, x0 - P(8) - nsz.Width, y + (lane - nsz.Height) / 2, StringFormat.GenericTypographic);
                double prog = ready && rt > 0 ? Progress(d, rt) : 0;
                float dx = x0 + P(18) + (x1 - x0 - P(40)) * (float)prog, dy = y + lane / 2 + (rt > 0 && prog < 1 ? (float)Math.Sin(r * 9 + i) * P(2) : 0);
                bool winner = results && d.Finish <= winT + 1e-6;
                DrawDuck(g, dx, dy, P(12), d.Col, winner, al);
            }
            // Bitiş çizgisi: dama
            float sq = P(6);
            for (int i = 0; i < (int)(n * lane / sq); i++)
                for (int j = 0; j < 2; j++)
                    using (var br = new SolidBrush((i + j) % 2 == 0 ? A(Color.White, al) : A(Color.Black, al))) g.FillRectangle(br, x1 + j * sq, top + i * sq, sq, sq);
            if (ready && rt < 0.7 && rt > -3)
            {
                int k = (int)Math.Ceiling(-rt);
                string s = rt < 0 ? k.ToString() : L.T("BAŞLA!");
                double frac = rt < 0 ? -rt - Math.Floor(-rt) : 1 - rt / 0.7;
                Outlined(g, s, fTitle, Gold, (x0 + x1) / 2, top + n * lane / 2 - P(30), Math.Min(1, frac * 2));
            }
            if (results)
            {
                var w = ds.OrderBy(d => d.Finish).First();
                Outlined(g, L.F("{0} KAZANDI!", w.User), fTitle, Gold, (x0 + x1) / 2 - nameW / 2, panel.Y - P(64), al, W * 0.8f);
                var cr = new Random(7);
                double ct = rt - winT;
                for (int i = 0; i < 40; i++)
                {
                    float cx = panel.X + (float)cr.NextDouble() * panel.Width, v = P(120) + (float)cr.NextDouble() * P(160);
                    float cy = panel.Y - P(120) + v * (float)ct;
                    if (cy > panel.Bottom) continue;
                    var st = g.Save();
                    g.TranslateTransform(cx + (float)Math.Sin(ct * 3 + i) * P(10), cy); g.RotateTransform((float)(ct * 300 + i * 30));
                    using (var br = new SolidBrush(A(Palette[i % Palette.Length], al))) g.FillRectangle(br, -P(4), -P(2), P(8), P(4));
                    g.Restore(st);
                }
            }
        }

        void DrawDuck(Graphics g, float x, float y, float r, Color tag, bool crown, double al)
        {
            var body = ColorTranslator.FromHtml("#ffd23f");
            using (var br = new SolidBrush(A(body, al))) { g.FillEllipse(br, x - r * 1.3f, y - r * 0.55f, r * 2.4f, r * 1.3f); g.FillEllipse(br, x + r * 0.2f, y - r * 1.55f, r * 1.15f, r * 1.15f); }
            using (var br = new SolidBrush(A(ColorTranslator.FromHtml("#f2a900"), al))) g.FillEllipse(br, x - r * 0.8f, y - r * 0.3f, r * 1.3f, r * 0.7f);
            using (var br = new SolidBrush(A(ColorTranslator.FromHtml("#ff8a1f"), al))) g.FillPolygon(br, new[] { new PointF(x + r * 1.25f, y - r * 1.1f), new PointF(x + r * 1.85f, y - r * 0.9f), new PointF(x + r * 1.25f, y - r * 0.72f) });
            using (var br = new SolidBrush(A(Color.Black, al))) g.FillEllipse(br, x + r * 0.85f, y - r * 1.2f, r * 0.24f, r * 0.24f);
            using (var br = new SolidBrush(A(tag, al))) g.FillEllipse(br, x - r * 0.95f, y - r * 0.05f, r * 0.45f, r * 0.45f);
            if (crown)
                using (var br = new SolidBrush(A(Gold, al)))
                    g.FillPolygon(br, new[] { new PointF(x + r * 0.25f, y - r * 1.5f), new PointF(x + r * 0.35f, y - r * 2.2f), new PointF(x + r * 0.6f, y - r * 1.8f), new PointF(x + r * 0.8f, y - r * 2.3f), new PointF(x + r * 1.0f, y - r * 1.8f), new PointF(x + r * 1.25f, y - r * 2.2f), new PointF(x + r * 1.3f, y - r * 1.5f) });
        }
    }

    /// <summary>
    /// Akvaryum: yayında chat'e yazan herkes küçük bir balık olur ve sol altta yüzer (abonelerin tacı var, yayıncı altın balık).
    /// Sürekli animasyonlu ama küçük ve 15 FPS.
    /// </summary>
    sealed class AquariumLayer : Layer
    {
        sealed class Fish { public string User; public Color Col; public bool Crown, Gold; public DateTime At; public int Seed; }

        readonly List<Fish> fish = new List<Fish>();
        readonly object lk = new object();
        readonly Font fName, fTitle;
        public Func<bool> Hidden = () => false;
        bool wasHidden;

        public AquariumLayer(float sc, bool run = true)
            : base((int)(14 * sc), (int)(912 * sc), (int)(420 * sc), (int)(154 * sc), 15, sc, run)
        {
            fName = Assets.Ui(P(11), true); fTitle = Assets.Display(P(14));
        }

        public void Join(string user, Color col, bool crown, bool gold)
        {
            lock (lk)
            {
                if (fish.Any(f => string.Equals(f.User, user, StringComparison.OrdinalIgnoreCase))) return;
                if (fish.Count >= 30) fish.Remove(fish.First(f => !f.Gold));
                fish.Add(new Fish { User = user, Col = col, Crown = crown, Gold = gold, At = DateTime.Now, Seed = Math.Abs(user.GetHashCode()) });
            }
        }

        protected override bool NeedsRender()
        {
            bool h = Hidden();
            if (h && wasHidden) return false;
            wasHidden = h;
            return true;
        }

        public override void Dispose() { base.Dispose(); fName.Dispose(); fTitle.Dispose(); }

        protected override void Draw(Graphics g, double t)
        {
            if (Hidden()) return;
            List<Fish> list;
            lock (lk) list = fish.ToList();
            var now = DateTime.Now;
            var tank = new RectangleF(1, P(18), W - 2, H - P(19));
            using (var p = Rounded(tank, P(12)))
            {
                using (var br = new LinearGradientBrush(tank, Color.FromArgb(150, 20, 80, 120), Color.FromArgb(200, 6, 28, 52), 90f)) g.FillPath(br, p);
                var st = g.Save();
                g.SetClip(p);
                // Işık huzmeleri
                for (int i = 0; i < 3; i++)
                {
                    float bx = W * (0.2f + 0.3f * i) + (float)Math.Sin(t * 0.4 + i) * P(20);
                    using (var br = new SolidBrush(Color.FromArgb(16, 255, 255, 255)))
                        g.FillPolygon(br, new[] { new PointF(bx - P(10), tank.Top), new PointF(bx + P(16), tank.Top), new PointF(bx + P(50), tank.Bottom), new PointF(bx - P(6), tank.Bottom) });
                }
                // Kum ve yosun
                var sand = new List<PointF>();
                for (float x = 0; x <= W + P(10); x += P(14)) sand.Add(new PointF(x, tank.Bottom - P(14) + (float)Math.Sin(x / s * 0.05) * P(4)));
                sand.Add(new PointF(W, H)); sand.Add(new PointF(0, H));
                using (var br = new SolidBrush(Color.FromArgb(220, 196, 170, 120))) g.FillPolygon(br, sand.ToArray());
                using (var pen = new Pen(Color.FromArgb(200, 60, 160, 90), P(4)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    foreach (float wx in new[] { 0.08f, 0.13f, 0.86f, 0.92f })
                    {
                        float x = W * wx, sw = (float)Math.Sin(t * 1.3 + wx * 20) * P(8);
                        g.DrawBezier(pen, x, tank.Bottom - P(8), x + sw, tank.Bottom - P(40), x - sw, tank.Bottom - P(60), x + sw * 1.5f, tank.Bottom - P(84));
                    }
                // Kabarcıklar
                using (var pen = new Pen(Color.FromArgb(120, 220, 240, 255), P(1.2f)))
                    for (int i = 0; i < 9; i++)
                    {
                        float span = tank.Height - P(20), y = tank.Bottom - P(16) - (float)((t * (14 + i * 3) * s + i * 37 * s) % span);
                        float x = W * (0.05f + 0.1f * i) + (float)Math.Sin(t * 2 + i) * P(4), d = P(3) + (i % 3) * P(1.5f);
                        g.DrawEllipse(pen, x, y, d, d);
                    }
                bool many = list.Count > 10;
                foreach (var f in list)
                {
                    var r = new Random(f.Seed);
                    float speed = P(16) + (float)r.NextDouble() * P(22), span = W - P(70), phase = (float)r.NextDouble() * span * 2;
                    float yb = tank.Top + P(22) + (float)r.NextDouble() * (tank.Height - P(60));
                    float L = P(20) + (float)r.NextDouble() * P(10);
                    double age = (now - f.At).TotalSeconds;
                    float pos = (float)((t * speed + phase) % (span * 2));
                    bool right = pos < span;
                    float x = P(35) + (right ? pos : span * 2 - pos);
                    float y = yb + (float)Math.Sin(t * 1.3 + f.Seed % 10) * P(5);
                    if (age < 1.2) { y = Lerp(tank.Top - P(10), yb, Ease(age / 1.2)); Splash(g, x, tank.Top + P(2), age); }
                    var c = f.Gold ? ColorTranslator.FromHtml("#ffc83a") : f.Col;
                    if (c.R * 0.3 + c.G * 0.59 + c.B * 0.11 < 90) c = Mix(c, Color.White, 0.45f);
                    FishShape(g, x, y, L, c, right, t + f.Seed % 10, f.Seed % 3, f.Crown || f.Gold);
                    bool showName = !many || age < 8 || ((t + (f.Seed % 30)) % 30) < 4;
                    if (showName)
                    {
                        var sz = g.MeasureString(f.User, fName, PointF.Empty, StringFormat.GenericTypographic);
                        float nx = Math.Max(P(4), Math.Min(W - sz.Width - P(4), x - sz.Width / 2)), ny = y - L * 0.5f - sz.Height - P(4);
                        using (var br = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) g.DrawString(f.User, fName, br, nx + 1, ny + 1, StringFormat.GenericTypographic);
                        using (var br = new SolidBrush(Color.FromArgb(230, 255, 255, 255))) g.DrawString(f.User, fName, br, nx, ny, StringFormat.GenericTypographic);
                    }
                }
                if (list.Count == 0)
                    using (var br = new SolidBrush(Color.FromArgb(200, 220, 235, 245)))
                    {
                        string hint = L.T("Chat'e yaz, balık ol!");
                        var sz = g.MeasureString(hint, fTitle, PointF.Empty, StringFormat.GenericTypographic);
                        g.DrawString(hint, fTitle, br, (W - sz.Width) / 2, tank.Top + (tank.Height - sz.Height) / 2 - P(6), StringFormat.GenericTypographic);
                    }
                g.Restore(st);
                using (var pen = new Pen(Color.FromArgb(80, 255, 255, 255), P(1.5f))) g.DrawPath(pen, p);
            }
            using (var br = new SolidBrush(Color.FromArgb(230, 255, 255, 255)))
                g.DrawString(L.T("AKVARYUM") + (list.Count > 0 ? " · " + list.Count : ""), fTitle, br, P(8), 0, StringFormat.GenericTypographic);
        }

        static float Ease(double x) { x = Math.Max(0, Math.Min(1, x)); return (float)(1 - Math.Pow(1 - x, 3)); }
        static float Lerp(float a, float b, double k) => a + (b - a) * (float)k;
        static Color Mix(Color a, Color b, float k) => Color.FromArgb(a.A, (int)(a.R + (b.R - a.R) * k), (int)(a.G + (b.G - a.G) * k), (int)(a.B + (b.B - a.B) * k));

        void Splash(Graphics g, float x, float y, double age)
        {
            for (int i = 0; i < 2; i++)
            {
                double k = age - i * 0.2;
                if (k < 0 || k > 0.8) continue;
                float rw = P(10) + P(30) * (float)k;
                using (var pen = new Pen(Color.FromArgb((int)(200 * (1 - k / 0.8)), 230, 245, 255), P(1.5f))) g.DrawEllipse(pen, x - rw, y - rw * 0.25f, rw * 2, rw * 0.5f);
            }
        }

        void FishShape(Graphics g, float x, float y, float L, Color c, bool right, double t, int kind, bool crown)
        {
            var st = g.Save();
            g.TranslateTransform(x, y);
            if (!right) g.ScaleTransform(-1, 1);
            float h = kind == 1 ? L * 0.8f : kind == 2 ? L * 0.42f : L * 0.6f;
            float wag = (float)Math.Sin(t * 8) * h * 0.18f;
            using (var br = new SolidBrush(Mix(c, Color.Black, 0.2f)))
                g.FillPolygon(br, new[] { new PointF(-L * 0.42f, 0), new PointF(-L * 0.78f, -h * 0.5f + wag), new PointF(-L * 0.7f, wag * 0.3f), new PointF(-L * 0.78f, h * 0.5f + wag) });
            var body = new RectangleF(-L / 2, -h / 2, L, h);
            using (var br = new LinearGradientBrush(body, Mix(c, Color.White, 0.35f), Mix(c, Color.Black, 0.15f), 90f)) g.FillEllipse(br, body);
            using (var br = new SolidBrush(Color.FromArgb(70, 255, 255, 255))) g.FillEllipse(br, -L * 0.25f, -h * 0.38f, L * 0.4f, h * 0.28f);
            using (var br = new SolidBrush(Color.White)) g.FillEllipse(br, L * 0.18f, -h * 0.22f, L * 0.16f, L * 0.16f);
            using (var br = new SolidBrush(Color.Black)) g.FillEllipse(br, L * 0.23f, -h * 0.18f, L * 0.08f, L * 0.08f);
            if (crown)
                using (var br = new SolidBrush(ColorTranslator.FromHtml("#ffd76a")))
                    g.FillPolygon(br, new[] { new PointF(-L * 0.12f, -h * 0.45f), new PointF(-L * 0.08f, -h * 0.45f - L * 0.28f), new PointF(L * 0.02f, -h * 0.45f - L * 0.14f), new PointF(L * 0.1f, -h * 0.45f - L * 0.3f), new PointF(L * 0.18f, -h * 0.45f - L * 0.14f), new PointF(L * 0.28f, -h * 0.45f - L * 0.28f), new PointF(L * 0.3f, -h * 0.45f) });
            g.Restore(st);
        }
    }
}
