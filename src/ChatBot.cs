using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace ValoLive
{
    /// <summary>
    /// Chat komutları: izleyici "!rank" yazınca yayıncı hesabından, mesajın geldiği chat'e kısa bir cevap gider.
    /// Her komut platform başına 20 sn'de (YouTube'da 60 sn: her cevap günlük kotadan 50 birim) en fazla bir kez
    /// cevaplanır. Riot/platform hatası olursa sessiz kalır.
    /// </summary>
    sealed class ChatBot
    {
        static readonly CultureInfo Tr = new CultureInfo("tr-TR");
        const int CooldownSec = 20, YouTubeCooldownSec = 60;

        readonly Action<Platform, string> send;   // cevabı o platformun chat'ine yazar (hesap bağlı değilse atlar)
        readonly Func<ValoState> state;
        readonly Func<LiveInfo> live;
        readonly RiotLocal riot = new RiotLocal();
        readonly Dictionary<string, DateTime> lastUse = new Dictionary<string, DateTime>();

        public ChatBot(Action<Platform, string> send, Func<ValoState> state, Func<LiveInfo> live) { this.send = send; this.state = state; this.live = live; }

        // Türkçe ve İngilizce adlar her dilde çalışır; listede arayüz diline uygun olanlar görünür.
        public static string[] Commands => L.Code == "tr"
            ? new[] { "!rank", "!bugün", "!son", "!skor", "!mağaza", "!skin", "!komutlar" }
            : new[] { "!rank", "!today", "!last", "!score", "!store", "!skin", "!commands" };

        public void Handle(ChatMessage m)
        {
            var text = (m.Text ?? "").Trim();
            if (!text.StartsWith("!")) return;
            string cmd = Normalize(text.Split(' ')[0]);
            Func<string> answer;
            switch (cmd)
            {
                case "!rank": answer = Rank; break;
                case "!bugun": case "!today": answer = Today; break;
                case "!son": case "!last": answer = Last; break;
                case "!skor": case "!score": answer = Score; break;
                case "!magaza": case "!store": case "!shop": answer = Store; break;
                case "!skin": answer = Skin; break;
                case "!komutlar": case "!komut": case "!commands": answer = () => L.T("Komutlar: ") + string.Join(" ", Commands); break;
                default: return;
            }
            var from = m.From ?? Platform.Kick;
            string useKey = from.Id + cmd;
            lock (lastUse)
            {
                if (lastUse.TryGetValue(useKey, out var t) && (DateTime.Now - t).TotalSeconds < (from == Platform.YouTube ? YouTubeCooldownSec : CooldownSec)) return;
                lastUse[useKey] = DateTime.Now;
            }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var reply = answer();
                    if (!string.IsNullOrEmpty(reply)) send(from, "🤖 " + reply);
                }
                catch (Exception e) { Diag.Event("CHAT", $"komut {cmd} cevaplanamadı: {e.Message}"); }
            });
        }

        // "!Mağaza", "!magaza", "!MAĞAZA" hepsi aynı komut.
        static string Normalize(string s)
        {
            s = s.ToLower(Tr);
            return new string(s.Select(ch => ch == 'ğ' ? 'g' : ch == 'ü' ? 'u' : ch == 'ş' ? 's' : ch == 'ı' ? 'i' : ch == 'ö' ? 'o' : ch == 'ç' ? 'c' : ch).ToArray());
        }

        static string Signed(int v) => (v > 0 ? "+" : v < 0 ? "−" : "±") + Math.Abs(v);

        string Rank()
        {
            var st = state();
            if (st == null || st.TierId <= 0) return L.T("Rank bilgisi henüz yok.");
            return L.F("{0} · {1} RR", st.TierName, st.Rr);
        }

        string Today()
        {
            var st = state();
            if (st == null || st.Wins + st.Losses == 0) return L.T("Bugün henüz rekabetçi maç yok.");
            return L.F("Bugün {0}G {1}M · {2} RR", st.Wins, st.Losses, Signed(st.NetRr)) + (st.Streak >= 2 ? L.F(" · {0} maçlık galibiyet serisi", st.Streak) : "");
        }

        string Last()
        {
            var m = state()?.Last;
            if (m == null) return L.T("Son maç bilgisi yok.");
            string r = m.Result == "W" ? L.T("ZAFER") : m.Result == "L" ? L.T("YENİLGİ") : L.T("BERABERE");
            return L.F("Son maç: {0} {1} · {2} · {3} · {4} · HS {5}", r, m.Score, m.Map, m.Agent, m.Kda, L.Pct(m.Hs)) + (m.RrChange != 0 ? $" · {Signed(m.RrChange)} RR" : "");
        }

        string Score()
        {
            var s = live()?.Snap;
            if (s == null || !s.InGame) return L.T("Şu an maçta değil.");
            return L.F("Canlı skor: {0}-{1} · {2}", s.Ally, s.Enemy, s.Map);
        }

        string Store()
        {
            var offers = riot.DailyStore(out int left);
            if (offers.Count == 0) return L.T("Mağaza bilgisi alınamadı.");
            var items = offers.Select(o => $"{ValApi.SkinLevel(o.Key).Key} ({o.Value} VP)");
            return L.F("Günün mağazası: {0} · yenilenmesine {1} sa {2} dk", string.Join(" · ", items), left / 3600, left % 3600 / 60);
        }

        string Skin()
        {
            var guns = riot.MyGuns();
            string Name(string gun, string plain)
            {
                string n = guns.TryGetValue(gun, out var chroma) ? ValApi.Chroma(chroma).Key : "";
                return n == "" || string.Equals(n, plain, StringComparison.OrdinalIgnoreCase) ? L.T("standart") : n;
            }
            return $"Vandal: {Name("9c82e19d-4575-0200-1a81-3eacf00cf872", "Vandal")} · Phantom: {Name("ee8e8d15-496b-07ac-e5f6-8fae5d4c7b1a", "Phantom")} · Operator: {Name("a03b24d3-4319-996d-0f8c-94bbfba1dfc7", "Operator")}";
        }
    }
}
