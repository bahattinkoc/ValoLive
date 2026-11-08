using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace ValoLive
{
    public sealed class Settings
    {
        public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ValoLive");
        static string FilePath => Path.Combine(Dir, "settings.json");

        public string KickUrl = "";
        public string KickKeyEnc = "";
        public int Quality = 1;
        public int Version;              // 2: 7500 seçeneği kaldırıldı, Quality bir aşağı kaydı
        public int Capture = 1;          // CaptureMode: 0 tam ekran, 1 sadece Valorant, 2 seçili uygulama
        public string CaptureApp = "";   // seçili uygulamanın süreç adı
        public string Mic;   // null = henüz seçilmedi, "" = mikrofonsuz
        public int MicVolume = 100;
        public bool Denoise = true;
        public string OutputId = "";
        public int GameVolume = 100;
        public string RiotName = "", RiotTag = "", Region = "eu";
        public string Language = "auto";  // arayüz dili: "auto" = Valorant'ın dili, yoksa dil kodu (Loc.cs)
        public string HdevKeyEnc = "";
        // Kick hesabı (resmi API): geliştirici uygulaması + yenileme tokenı, hepsi DPAPI ile şifreli
        public string KickClientIdEnc = "", KickSecretEnc = "", KickRefreshEnc = "", KickSlug = "";
        public string StreamTitle = "", CategoryName = "VALORANT", Tags = "";
        public int CategoryId;
        // Yayın katmanları
        public bool ChatBox = true, MapCover = true, ChatBot = true, PostMatchCard = true;
        public bool WindowCapture;                                   // hafif yakalama: yalnızca Valorant penceresi (deneysel, varsayılan kapalı)
        public bool BreakScreen = true, AutoLobby = true, LobbyAlerts = true, AutoCover = true, TrayHintShown;
        public bool EnemyMemory;                                     // rakip hafızası: minimap'ten son görülen yerler (deneysel, varsayılan kapalı)
        public bool EnemyMemoryMap;                                  // rakip hafızası: son yerler harita üstünde ikonla (deneysel, varsayılan kapalı)
        public bool EnemyMemoryDiag;                                 // rakip hafızası tanı kaydı: ekran kareleri logs\hafiza-kayit (arayüzde yok, elle açılır)
        public int MapCoverX = 10, MapCoverY = 10, MapCoverD; // rozetin ekran pikselinde konumu/çapı (D=0: varsayılan)

        public static Settings Load()
        {
            Settings s;
            try { s = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings(); }
            catch { s = new Settings(); }
            if (s.Version < 2) { s.Quality = Math.Max(0, s.Quality - 1); s.Version = 2; }
            return s;
        }

        public void Save()
        {
            try { Directory.CreateDirectory(Dir); File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(this)); } catch { }
        }

        public static string Protect(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(s), null, DataProtectionScope.CurrentUser));
        }

        public static string Unprotect(string s)
        {
            try { return string.IsNullOrEmpty(s) ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s), null, DataProtectionScope.CurrentUser)); }
            catch { return ""; }
        }
    }

    sealed class MatchInfo
    {
        public string Id, Result, Map, AgentId, Agent, Score, Queue, Tier;
        public int K, D, A, Hs, RrChange, TierId;
        public string Kda => $"{K}/{D}/{A}";
    }

    sealed class ValoState
    {
        public int TierId, Rr, Elo;
        public string TierName = L.T("Derecesiz");
        public int Wins, Losses, NetRr, Streak;
        public MatchInfo Last;
        public List<MatchInfo> SessionMatches = new List<MatchInfo>();
    }

    static class Tiers
    {
        static readonly Dictionary<string, string> Tr = new Dictionary<string, string>
        {
            ["Iron"] = L.T("Demir"), ["Bronze"] = L.T("Bronz"), ["Silver"] = L.T("Gümüş"), ["Gold"] = L.T("Altın"),
            ["Platinum"] = L.T("Platin"), ["Diamond"] = L.T("Elmas"), ["Ascendant"] = L.T("Yücelik"),
            ["Immortal"] = L.T("Ölümsüzlük"), ["Radiant"] = L.T("Radyant"), ["Unrated"] = L.T("Derecesiz"), ["Unranked"] = L.T("Derecesiz"),
        };

        public static string Turkish(string name)
        {
            if (string.IsNullOrEmpty(name)) return L.T("Derecesiz");
            var parts = name.Split(' ');
            return (Tr.TryGetValue(parts[0], out var t) ? t : parts[0]) + (parts.Length > 1 ? " " + parts[1] : "");
        }

        /// <summary>Riot tier numarasından Türkçe ad (3 = Demir 1 … 27 = Radyant).</summary>
        public static string Name(int id)
        {
            if (id >= 27) return Turkish("Radiant");
            if (id < 3) return Turkish("Unrated");
            string[] names = { "Iron", "Bronze", "Silver", "Gold", "Platinum", "Diamond", "Ascendant", "Immortal" };
            return Turkish($"{names[(id - 3) / 3]} {(id - 3) % 3 + 1}");
        }

        public static System.Drawing.Color Color(int id)
        {
            int[] min = { 3, 6, 9, 12, 15, 18, 21, 24, 27 };
            string[] col = { "#9a9a9a", "#c08552", "#d6dee3", "#ecc04f", "#45c3d1", "#c890f5", "#35d985", "#ef5a75", "#ffe8a3" };
            string c = "#9aa3b2";
            for (int i = 0; i < min.Length; i++) if (id >= min[i]) c = col[i];
            return System.Drawing.ColorTranslator.FromHtml(c);
        }
    }

    /// <summary>
    /// HenrikDev API'sinden rank ve maç verisini dakikada bir çeker (oyuna dokunmaz),
    /// yayın oturumunu tutar ve yeni maç / RR değişimi / rank atlama olaylarını bildirir.
    /// </summary>
    sealed class ValorantTracker : IDisposable
    {
        readonly string name, tag, region, key;
        Timer timer;
        int busy;
        bool first = true;
        int prevElo, prevTier;
        readonly HashSet<string> known = new HashSet<string>();
        readonly Dictionary<string, MatchInfo> details = new Dictionary<string, MatchInfo>();
        // Oturum = "oyun günü": sabah 06:00'dan beri oynananlar (gece yarısını geçen yayınlar bölünmesin).
        readonly DateTime sessionStartUtc;

        public ValoState State { get; private set; } = new ValoState();
        public string Status { get; private set; } = L.T("Bağlanıyor…");

        public event Action<ValoState> Updated;
        public event Action<string> StatusChanged;
        public event Action<MatchInfo> NewMatch;
        public event Action<int> RrChanged;
        public event Action<ValoState> RankUp;

        public ValorantTracker(string name, string tag, string region, string key)
        {
            this.name = name; this.tag = tag; this.region = region; this.key = key;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var now = DateTime.Now;
            var day = now.Date.AddHours(6);
            sessionStartUtc = (now < day ? day.AddDays(-1) : day).ToUniversalTime();
        }

        public void Start() { timer = new Timer(_ => Poll(), null, 0, 60000); }

        object Get(string path) => Api(key, path);

        /// <summary>HenrikDev API çağrısı; yanıtın "data" kısmını döner.</summary>
        internal static object Api(string key, string path)
        {
            using (var wc = new WebClient { Encoding = Encoding.UTF8 })
            {
                wc.Headers["Authorization"] = key;
                var json = wc.DownloadString("https://api.henrikdev.xyz" + path);
                return J(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json), "data");
            }
        }

        void Poll()
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            try
            {
                string b = $"{region}/pc/{Uri.EscapeDataString(name)}/{Uri.EscapeDataString(tag)}";
                var mmr = Get("/valorant/v3/mmr/" + b);
                var history = (J(Get("/valorant/v2/mmr-history/" + b), "history") as object[]) ?? new object[0];
                // Maç ayrıntıları (ajan, KDA) ağır bir uç: ilk seferde günün maçları için 10, sonra son 3.
                var matches = (Get($"/valorant/v4/matches/{b}?mode=competitive&size={(first ? 10 : 3)}") as object[]) ?? new object[0];
                Apply(mmr, matches, history);
                SetStatus($"✓ {State.TierName} · {State.Rr} RR");
            }
            catch (WebException e)
            {
                int code = (int)((e.Response as HttpWebResponse)?.StatusCode ?? 0);
                SetStatus(code == 401 || code == 403 ? L.T("✗ HenrikDev anahtarı geçersiz")
                        : code == 404 ? L.T("✗ Oyuncu bulunamadı (isim/etiket/bölge)")
                        : code == 429 ? L.T("… API limiti, bekleniyor")
                        : L.T("✗ Bağlantı hatası"));
            }
            catch (Exception e) { SetStatus("✗ " + e.Message); }
            finally { busy = 0; }
        }

        void SetStatus(string s) { Status = s; StatusChanged?.Invoke(s); }

        void Apply(object mmr, object[] matches, object[] history)
        {
            string puuid = S(J(mmr, "account", "puuid"));
            int tierId = I(J(mmr, "current", "tier", "id"));
            int elo = I(J(mmr, "current", "elo"));
            var infos = matches.Select(m => Info(m, puuid)).Where(m => m != null).ToList();
            MatchInfo fresh = null;

            lock (known)
            {
                foreach (var m in infos) details[m.Id] = m;
                State.TierId = tierId;
                State.TierName = Tiers.Turkish(S(J(mmr, "current", "tier", "name")));
                State.Rr = I(J(mmr, "current", "rr"));
                State.Elo = elo;

                // Günün maçları RR geçmişinden (hafif, maç başına RR değişimi var); ayrıntı varsa onunla zenginleşir.
                var session = new List<MatchInfo>();
                foreach (var h in history)
                {
                    if (!DateTime.TryParse(S(J(h, "date")), CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date) || date < sessionStartUtc) continue;
                    string id = S(J(h, "match_id"));
                    int change = I(J(h, "last_change"));
                    var m = details.TryGetValue(id, out var d) ? d
                          : new MatchInfo { Id = id, Map = S(J(h, "map", "name")), Result = change > 0 ? "W" : change < 0 ? "L" : "D" };
                    m.RrChange = change;
                    session.Add(m);
                }
                State.SessionMatches = session;
                foreach (var m in Enumerable.Reverse(session))
                {
                    // Yeni maç bandı ayrıntı (ajan, KDA) gelince gösterilir; RR geçmişi maç listesinden önce düşebilir.
                    if (known.Contains(m.Id) || (!first && m.Agent == null)) continue;
                    known.Add(m.Id);
                    if (!first) fresh = m;
                }
                if (infos.Count > 0) State.Last = infos[0];
                Recount();
            }

            if (!first)
            {
                if (fresh != null) NewMatch?.Invoke(fresh);
                if (elo != prevElo) RrChanged?.Invoke(elo - prevElo);
                if (prevTier > 0 && tierId > prevTier) RankUp?.Invoke(State);
            }
            first = false;
            prevElo = elo;
            prevTier = tierId;
            Updated?.Invoke(State);
        }

        void Recount()
        {
            var g = State.SessionMatches;
            State.Wins = g.Count(m => m.Result == "W");
            State.Losses = g.Count(m => m.Result == "L");
            State.NetRr = g.Sum(m => m.RrChange);
            State.Streak = g.TakeWhile(m => m.Result == "W").Count();
        }

        internal static MatchInfo Info(object m, string puuid)
        {
            var players = J(m, "players") as object[];
            var teams = J(m, "teams") as object[];
            var me = players?.FirstOrDefault(p => S(J(p, "puuid")) == puuid);
            if (me == null) return null;
            string team = S(J(me, "team_id"));
            var mine = teams?.FirstOrDefault(t => S(J(t, "team_id")) == team);
            var other = teams?.FirstOrDefault(t => S(J(t, "team_id")) != team);
            int hs = I(J(me, "stats", "headshots")), bs = I(J(me, "stats", "bodyshots")), ls = I(J(me, "stats", "legshots"));
            return new MatchInfo
            {
                Id = S(J(m, "metadata", "match_id")),
                Result = J(mine, "won") is bool w && w ? "W" : J(other, "won") is bool l && l ? "L" : "D",
                Map = S(J(m, "metadata", "map", "name")),
                Queue = S(J(m, "metadata", "queue", "id")),
                TierId = I(J(me, "tier", "id")),
                Tier = Tiers.Turkish(S(J(me, "tier", "name"))),
                AgentId = S(J(me, "agent", "id")),
                Agent = S(J(me, "agent", "name")),
                Score = mine == null ? "" : $"{I(J(mine, "rounds", "won"))}-{I(J(mine, "rounds", "lost"))}",
                K = I(J(me, "stats", "kills")), D = I(J(me, "stats", "deaths")), A = I(J(me, "stats", "assists")),
                Hs = hs + bs + ls > 0 ? 100 * hs / (hs + bs + ls) : 0,
            };
        }

        internal static object J(object o, params string[] path)
        {
            foreach (var p in path)
            {
                if (!(o is Dictionary<string, object> d) || !d.TryGetValue(p, out o)) return null;
            }
            return o;
        }
        internal static string S(object o) => o as string ?? "";
        internal static int I(object o) { try { return o == null ? 0 : Convert.ToInt32(o); } catch { return 0; } }

        public void Dispose() { timer?.Dispose(); }
    }
}
