using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Web.Script.Serialization;
using static ValoLive.ValorantTracker;

namespace ValoLive
{
    /// <summary>Defterde bir maç: yalnızca rekabetçi; oyuncular kimlikle (puuid) tutulur, Riot adları vermez.</summary>
    sealed class EncMatch
    {
        public string Id, Map, MyTeam;
        public long Start;              // unix ms
        public int MyRounds, TheirRounds;
        public List<EncPlayer> Players = new List<EncPlayer>();
        public string Outcome() => MyRounds > TheirRounds ? "W" : MyRounds < TheirRounds ? "L" : "D"; // metot: JSON'a yazılmasın
    }

    sealed class EncPlayer
    {
        public string Puuid, Team, Agent;
        public int K, D, A;
        public int KilledMe = -1, KilledByMe = -1; // o maçta seni kaç kez öldürdü / sen onu kaç kez; -1 = eski kayıt, bilinmiyor
    }

    /// <summary>Şu anki maçtaki bir oyuncuyla geçmiş bir karşılaşma.</summary>
    sealed class Encounter
    {
        public EncMatch Match;
        public EncPlayer Them;
        public bool Teammate;
    }

    /// <summary>
    /// Karşılaşma defteri: kendi rekabetçi maç geçmişini (Riot istemcisinin yerel oturumuyla) tarar, her maçtaki
    /// 10 oyuncuyu bilgisayara kaydeder. Maç Odası açılan maçtaki oyuncuları bu defterde arar. Riot geriye
    /// ~90 maç veriyor; defter bundan sonra her yeni maçla büyür (eski kayıtlar silinmez).
    /// </summary>
    static class Encounters
    {
        static readonly string FilePath = Path.Combine(Settings.Dir, "encounters.json");
        static readonly object lk = new object();
        static Dictionary<string, EncMatch> matches = new Dictionary<string, EncMatch>();
        static readonly Dictionary<string, List<EncMatch>> byPlayer = new Dictionary<string, List<EncMatch>>();
        static readonly HashSet<string> skipped = new HashSet<string>(); // bu oturumda detayı alınamayanlar
        static string self;
        static Thread worker;
        static readonly AutoResetEvent wake = new AutoResetEvent(false);

        /// <summary>Kullanıcıya gösterilecek durum ("87 maç", "taranıyor 12/87"…).</summary>
        public static string Status { get; private set; } = "";
        public static event Action Changed;

        public static void Start()
        {
            if (worker != null) return;
            Load();
            worker = new Thread(Loop) { IsBackground = true, Name = "encounters" };
            worker.Start();
        }

        /// <summary>Bir maç bitmiş olabilir: hemen yeni maç var mı bak.</summary>
        public static void Poke() => wake.Set();

        public static int Count { get { lock (lk) return matches.Count; } }

        /// <summary>Bu oyuncuyla geçmiş karşılaşmalar (yeniden eskiye); şu anki maç hariç.</summary>
        public static List<Encounter> Find(string puuid, string currentMatch)
        {
            var list = new List<Encounter>();
            lock (lk)
            {
                if (string.IsNullOrEmpty(puuid) || !byPlayer.TryGetValue(puuid, out var ms)) return list;
                foreach (var m in ms)
                {
                    if (m.Id == currentMatch) continue;
                    var them = m.Players.FirstOrDefault(p => p.Puuid == puuid);
                    if (them == null) continue;
                    list.Add(new Encounter { Match = m, Them = them, Teammate = them.Team == m.MyTeam });
                }
            }
            return list.OrderByDescending(e => e.Match.Start).ToList();
        }

        // ---------- tarama ----------

        static void Loop()
        {
            var riot = new RiotLocal();
            while (true)
            {
                try { Sync(riot); }
                catch (InvalidOperationException e) { SetStatus(matches.Count > 0 ? L.F("{0} maç · {1}", matches.Count, e.Message) : e.Message); }
                catch (Exception e) { SetStatus(L.F("{0} maç · tarama hatası: {1}", Count, e.Message)); Diag.Event("DEFTER", e.Message); }
                wake.WaitOne(TimeSpan.FromMinutes(2)); // yeni maç kontrolü tek istek; sık bakmak ucuz
            }
        }

        static void Sync(RiotLocal riot)
        {
            // Yeniden eskiye sayfa sayfa: tamamı zaten defterde olan bir sayfaya gelince dur.
            var todo = new List<string>();
            int total = 0;
            for (int start = 0; ; start += 20)
            {
                var page = riot.CompetitiveHistory(start, start + 20, out total);
                self = riot.SelfPuuid;
                var fresh = page.Where(m => !Known(m.Key)).Select(m => m.Key).ToList();
                todo.AddRange(fresh);
                if (page.Count < 20 || fresh.Count == 0 || start + 20 >= total) break;
                Thread.Sleep(800);
            }
            // Eski kayıtlarda düello sayısı yok: yeni maçlardan sonra onlar da bir kez yeniden okunur.
            int fresh0 = todo.Count;
            lock (lk) todo.AddRange(matches.Values.Where(m => m.Players.Any(p => p.KilledMe < 0) && !skipped.Contains(m.Id)).OrderByDescending(m => m.Start).Select(m => m.Id));
            if (todo.Count == 0) { SetStatus(L.F("{0} rekabetçi maç", Count)); return; }

            int done = 0;
            foreach (var id in todo)
            {
                SetStatus(done < fresh0 ? L.F("defter dolduruluyor {0}/{1}", done, fresh0) : L.F("{0} maç · düellolar ekleniyor {1}/{2}", Count, done - fresh0, todo.Count - fresh0));
                try
                {
                    var m = Parse(riot.MatchDetails(id), riot);
                    lock (lk)
                    {
                        if (m != null) { matches[m.Id] = m; Index(m); }
                        else skipped.Add(id);
                    }
                }
                catch (WebException e) when ((int)((e.Response as HttpWebResponse)?.StatusCode ?? 0) == 429)
                {
                    SetStatus(L.F("Riot yavaşlattı, bekleniyor… {0}/{1}", done, todo.Count));
                    Thread.Sleep(30000);
                    continue;
                }
                catch (WebException) { lock (lk) skipped.Add(id); }
                done++;
                if (done % 10 == 0) Save();
                Changed?.Invoke();
                Thread.Sleep(1300); // Riot'un maç detayı ucu hassas: yavaş ve nazik
            }
            Save();
            SetStatus(L.F("{0} rekabetçi maç", Count));
            Changed?.Invoke();
        }

        static bool Known(string id) { lock (lk) return matches.ContainsKey(id) || skipped.Contains(id); }

        static EncMatch Parse(object d, RiotLocal riot)
        {
            if (S(J(d, "matchInfo", "queueID")) != "competitive" || !(J(d, "matchInfo", "isCompleted") is bool c && c)) return null;
            var players = ((J(d, "players") as object[]) ?? new object[0]).Where(p => !(J(p, "isObserver") is bool o && o)).ToList();
            var me = players.FirstOrDefault(p => S(J(p, "subject")) == self);
            if (me == null) return null;
            var m = new EncMatch
            {
                Id = S(J(d, "matchInfo", "matchId")),
                Map = riot.MapNameOf(S(J(d, "matchInfo", "mapId"))),
                Start = Convert.ToInt64(J(d, "matchInfo", "gameStartMillis") ?? 0L),
                MyTeam = S(J(me, "teamId")),
            };
            foreach (var t in (J(d, "teams") as object[]) ?? new object[0])
            {
                if (S(J(t, "teamId")) == m.MyTeam) m.MyRounds = I(J(t, "roundsWon"));
                else m.TheirRounds = I(J(t, "roundsWon"));
            }
            var kills = (J(d, "kills") as object[]) ?? new object[0];
            foreach (var p in players)
            {
                string id = S(J(p, "subject"));
                m.Players.Add(new EncPlayer
                {
                    Puuid = id, Team = S(J(p, "teamId")), Agent = S(J(p, "characterId")),
                    K = I(J(p, "stats", "kills")), D = I(J(p, "stats", "deaths")), A = I(J(p, "stats", "assists")),
                    KilledMe = kills.Count(k => S(J(k, "killer")) == id && S(J(k, "victim")) == self),
                    KilledByMe = kills.Count(k => S(J(k, "killer")) == self && S(J(k, "victim")) == id),
                });
            }
            return m;
        }

        static void Index(EncMatch m)
        {
            foreach (var p in m.Players)
            {
                if (p.Puuid == self) continue;
                if (!byPlayer.TryGetValue(p.Puuid, out var l)) byPlayer[p.Puuid] = l = new List<EncMatch>();
                if (!l.Contains(m)) l.Add(m);
            }
        }

        static void SetStatus(string s) { Status = s; Changed?.Invoke(); }

        // ---------- dosya ----------

        sealed class FileModel { public string Self; public List<EncMatch> Matches = new List<EncMatch>(); }

        static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var f = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<FileModel>(File.ReadAllText(FilePath));
                lock (lk)
                {
                    self = f.Self;
                    matches = f.Matches.Where(m => m.Id != null).ToDictionary(m => m.Id);
                    foreach (var m in matches.Values) Index(m);
                }
                Status = L.F("{0} rekabetçi maç", matches.Count);
            }
            catch (Exception e) { Diag.Event("DEFTER", "defter okunamadı: " + e.Message); }
        }

        static void Save()
        {
            try
            {
                string json;
                lock (lk) json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(new FileModel { Self = self, Matches = matches.Values.OrderByDescending(m => m.Start).ToList() });
                Directory.CreateDirectory(Settings.Dir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath);
            }
            catch (Exception e) { Diag.Event("DEFTER", "defter yazılamadı: " + e.Message); }
        }
    }
}
