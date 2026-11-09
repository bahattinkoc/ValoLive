using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace ValoLive
{
    /// <summary>
    /// Maç Odası istihbaratı: bir oyuncunun son maçlarından çıkan, maçta işe yarayan bulgular (konum, spike, düo,
    /// clutch, ajan geçmişi, oturum). Her bulgunun bir önemi var: satırda en önemli ikisi kısa etiket olarak,
    /// brifingde lobinin en önemlileri tek cümle olarak, oyuncu penceresinde hepsi görünür. Az veriden bulgu çıkmaz.
    /// </summary>
    static class Intel
    {
        public sealed class Fact
        {
            public string Puuid, Pill, Text;   // Pill null: satırda etiket olmaz, yalnızca cümle
            public Color Color;
            public int Score;                  // önem (0-100); rakip bulguları brifingde öne çıkar
            public bool Style;                 // oyun stili etiketi (oyuncu penceresi bunları zaten ayrı gösterir)
        }

        public sealed class Input
        {
            public LobbyPlayer P;
            public PlayerHistory H;
            public List<PMatch> Ms;                       // incelenen son maçları (yoksa boş)
            public string MapUrl;
            public List<long> Starts;                     // son maçlarının başlangıçları (tüm modlar, ms)
            public List<KeyValuePair<LobbyPlayer, int>> Duos; // lobide aynı partide görüldüğü oyuncular → kaç maçta
            public Func<string, string> AgentName;
        }

        static readonly Color Orange = ColorTranslator.FromHtml("#ffb547"), Blue = ColorTranslator.FromHtml("#7aa2ff"),
            Pink = ColorTranslator.FromHtml("#ff5a6e"), Green = ColorTranslator.FromHtml("#3ddc97"), Purple = ColorTranslator.FromHtml("#c890f5"),
            Teal = ColorTranslator.FromHtml("#4fd1c5");

        static int Pct(int a, int b) => b > 0 ? (int)Math.Round(100.0 * a / b) : 0;

        public static List<Fact> For(Input x)
        {
            var facts = new List<Fact>();
            var p = x.P;
            if (p == null || p.IsSelf || p.Incognito || p.Puuid == null) return facts; // gizli oyuncu incelenmez
            var ms = x.Ms ?? new List<PMatch>();
            void Add(int score, string pill, string text, Color c, bool style = false) =>
                facts.Add(new Fact { Puuid = p.Puuid, Pill = pill, Text = text, Color = c, Score = score, Style = style });

            // Oyun stili (Scout.Tags): Operator en tehlikelisi, sonra ilk temas profili.
            foreach (var t in Scout.Tags(ms))
            {
                int s = t.Text == L.T("OP'ÇU") ? 90 : t.Text == L.T("GİRİŞÇİ") ? 62 : t.Text == L.T("KAFACI") ? 56 : t.Text == L.T("AGRESİF") ? 52
                      : t.Text == "SHERIFF" ? 46 : t.Text == L.T("SABIRLI") ? 42 : 36;
                Add(s, t.Text, t.Note, t.Color, true);
            }

            Agent(x, ms, Add);
            Position(x, ms, Add);
            Clutch(ms, Add);
            Duo(x, Add);
            Session(x, ms, Add);
            return facts.OrderByDescending(f => f.Score).ToList();
        }

        /// <summary>Şu anki ajanı: hiç oynamadığı ajan mı, tek ajan mı, asıl ajanı başka mı.</summary>
        static void Agent(Input x, List<PMatch> ms, Action<int, string, string, Color, bool> add)
        {
            string cur = x.P.AgentId;
            if (string.IsNullOrEmpty(cur) || ms.Count < 5) return;
            string name = x.AgentName(cur);
            int same = ms.Count(m => string.Equals(m.Agent, cur, StringComparison.OrdinalIgnoreCase));
            int[] lv = null;
            x.H?.Mastery?.TryGetValue(cur, out lv);
            if (same == 0 && (lv == null || lv[0] <= 2))
                add(75, L.T("İLK KEZ?"), L.F("{0} ile son {1} maçında hiç oynamamış: bu ajana alışık değil, faydasını geç ve hatalı kullanabilir.", name, ms.Count), Orange, false);
            else if (same >= ms.Count - 1 && ms.Count >= 6)
                add(68, L.T("TEK AJAN"), L.F("Son {0} maçta {1} kez {2} oynamış: bu ajanda çok deneyimli, ezberindeki oyunu oynar.", ms.Count, same, name), Purple, false);
            else
            {
                var main = ms.GroupBy(m => m.Agent).OrderByDescending(gr => gr.Count()).First();
                if (main.Count() * 2 > ms.Count && !string.Equals(main.Key, cur, StringComparison.OrdinalIgnoreCase))
                    add(48, null, L.F("Asıl ajanı {0} (son {1} maçta {2} kez), bu maçta {3} oynuyor.", x.AgentName(main.Key), ms.Count, main.Count(), name), Mut, false);
            }
        }

        static readonly Color Mut = Theme.Mut;
        static readonly Dictionary<string, ValApi.Map> maps = new Dictionary<string, ValApi.Map>(StringComparer.OrdinalIgnoreCase);

        static ValApi.Map MapOf(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            lock (maps)
            {
                if (maps.TryGetValue(url, out var m)) return m;
            }
            var map = ValApi.MapOf(url); // arka plan iş parçacığında: ağdan bir kez
            lock (maps) maps[url] = map;
            return map;
        }

        /// <summary>Oyun koordinatı → en yakın bölge ("B", "Site") ya da null.</summary>
        static string[] Region(ValApi.Map mp, int gx, int gy)
        {
            var u = mp.ToMinimap(gx, gy);
            string best = null; float bd = float.MaxValue;
            foreach (var c in mp.Callouts)
            {
                float dx = c.Value.X - u.X, dy = c.Value.Y - u.Y, d = dx * dx + dy * dy;
                if (d < bd) { bd = d; best = c.Key; }
            }
            if (best == null || bd > 0.12f * 0.12f) return null; // haritanın dışında / bölgesiz alan
            int i = best.IndexOf('/');
            return i < 0 ? new[] { best, "" } : new[] { best.Substring(0, i), best.Substring(i + 1) };
        }

        /// <summary>Bu haritada atak ve defansta en çok nerede çatışıyor (öldürdüğü ve öldüğü yerler) ve spike'ı nereye kuruyor.</summary>
        static void Position(Input x, List<PMatch> ms, Action<int, string, string, Color, bool> add)
        {
            var here = ms.Where(m => string.Equals(m.MapUrl, x.MapUrl, StringComparison.OrdinalIgnoreCase)).ToList();
            if (here.Count < 2) return; // tek maç o maçın takım planıdır, alışkanlık değil
            var mp = MapOf(x.MapUrl);
            if (mp == null || mp.Callouts.Count == 0) return;
            foreach (int side in new[] { 1, 0 })
            {
                var spots = here.SelectMany(m => m.KillSpots.Concat(m.DeathSpots)).Where(s => s[2] == side)
                    .Select(s => Region(mp, s[0], s[1])).Where(r => r != null).ToList();
                if (spots.Count < 6) continue;
                var top = spots.GroupBy(r => r[0]).OrderByDescending(gr => gr.Count()).First();
                if (top.Count() * 100 < spots.Count * 55) continue; // belirgin bir tercih yok
                var sub = top.GroupBy(r => r[1]).OrderByDescending(gr => gr.Count()).First();
                string where = sub.Key != "" && sub.Count() * 2 >= top.Count() ? top.Key + " " + sub.Key : top.Key;
                string sideName = side == 1 ? L.T("Atakta") : L.T("Defansta");
                add(side == 0 ? 86 : 80, null, L.F("{0} genelde {1} tarafında: bu haritadaki {2} çatışmadan {3} tanesi orada (en çok {4}).", sideName, top.Key, spots.Count, top.Count(), where), Teal, false);
            }
            var plants = here.SelectMany(m => m.Plants).GroupBy(kv => kv.Key).Select(gr => new { Site = gr.Key, N = gr.Sum(kv => kv.Value) }).OrderByDescending(s => s.N).ToList();
            int total = plants.Sum(s => s.N);
            if (total >= 3 && plants[0].N * 100 >= total * 70)
                add(70, null, L.F("Spike'ı bu haritada genelde {0} bölgesine kuruyor ({1} kurulumdan {2} tanesi).", plants[0].Site, total, plants[0].N), Orange, false);
        }

        static void Clutch(List<PMatch> ms, Action<int, string, string, Color, bool> add)
        {
            // Gerçek veride tek kalınan roundların (1vX) ~%30'u kazanılıyor: %45 üstü belirgin iyi, %10 altı belirgin kötü.
            int n = ms.Sum(m => m.ClutchN), w = ms.Sum(m => m.ClutchW);
            if (n >= 8 && w * 100 >= n * 45)
                add(78, "CLUTCH", L.F("Takımında tek kaldığı {0} roundun {1} tanesini kazanmış: son kişi kalınca kalabalık gir, onu sona bırakma.", n, w), Green, false);
            else if (n >= 10 && w * 10 <= n)
                add(44, null, L.F("Tek kaldığı {0} roundun yalnızca {1} tanesini kazanmış: yalnız kalınca baskı kur, panikler.", n, w), Mut, false);
        }

        static void Duo(Input x, Action<int, string, string, Color, bool> add)
        {
            foreach (var d in x.Duos ?? new List<KeyValuePair<LobbyPlayer, int>>())
            {
                if (d.Value < 2) continue;
                var o = d.Key;
                string who = string.IsNullOrEmpty(o.AgentId) ? (o.Name ?? "?") : x.AgentName(o.AgentId) + (o.Name != null ? " (" + o.Name + ")" : "");
                add(64, null, L.F("{0} ile düo: son maçlarından {1} tanesinde aynı partideydiler, birbirlerini kollarlar.", who, d.Value), Blue, false);
            }
        }

        /// <summary>
        /// Şu anki oturum: aralarında 90 dakikadan az boşluk olan son maçları (gece yarısını aşsa da) ve üst üste
        /// kayıp/galibiyet. Son maçı 3 saatten eskiyse oturum yeni başlamıştır.
        /// </summary>
        static void Session(Input x, List<PMatch> ms, Action<int, string, string, Color, bool> add)
        {
            var st = (x.Starts ?? new List<long>()).OrderByDescending(s => s).ToList();
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), gap = 90 * 60000L;
            int played = 0;
            if (st.Count > 0 && now - st[0] < 3 * 3600000L)
                for (played = 1; played < st.Count && st[played - 1] - st[played] < gap; played++) { }
            var recent = ms.OrderByDescending(m => m.Start).ToList();
            int losses = recent.TakeWhile(m => m.Result == "L").Count(), wins = recent.TakeWhile(m => m.Result == "W").Count();
            if (losses >= 3)
                add(58, null, played >= 2 ? L.F("Bugün {0} maç oynamış, son {1} maçını üst üste kaybetmiş: tilt'te olabilir, erken baskı kur.", played, losses)
                                          : L.F("Son {0} maçını üst üste kaybetmiş: tilt'te olabilir, erken baskı kur.", losses), Pink, false);
            else if (wins >= 4)
                add(54, null, L.F("Son {0} maçını üst üste kazanmış: özgüvenli, agresif oynar.", wins), Green, false);
            else if (played >= 6)
                add(40, null, L.F("Bugün {0} maç oynamış: uzun oturumun sonunda dikkati düşer.", played), Mut, false);
        }

        /// <summary>
        /// Lobinin brifingi: en önemli bulgular, rakipler önde, oyuncu başına en fazla iki. Stil etiketleri satırda
        /// zaten göründüğü için brifinge yalnızca en güçlüleri (Operator, clutch) girer.
        /// </summary>
        public static List<Fact> Briefing(IEnumerable<KeyValuePair<LobbyPlayer, List<Fact>>> all, int count)
        {
            return all.SelectMany(kv => kv.Value.Where(f => !f.Style || f.Score >= 80).Take(2).Select(f => new { f, s = f.Score + (kv.Key.Ally ? 0 : 12) }))
                .Where(a => a.f.Score >= 50).OrderByDescending(a => a.s).Take(count).Select(a => a.f).ToList();
        }
    }
}
