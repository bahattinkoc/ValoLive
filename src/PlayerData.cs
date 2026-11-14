using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using static ValoLive.ValorantTracker;

namespace ValoLive
{
    // ---------- Oyuncu Ara: veri ----------
    // HenrikDev API'si (kullanıcının anahtarı) ile herhangi bir Riot ID'nin herkese açık verisi: hesap, rütbe ve sezonlar,
    // yüzlerce maçlık özet (stored-matches) ve 10'arlı ayrıntılı maçlar (10 oyuncu, parti kimliği). tracker.gg'nin gösterdiği veri.

    sealed class PAccount { public string Puuid, Name, Tag, Region, Card; public int Level; }

    sealed class PSeason { public string Short; public int EndTier, Wins, Games; }

    sealed class PRank
    {
        public int Tier, Rr, PeakTier;
        public string PeakSeason;
        public List<PSeason> Seasons = new List<PSeason>();
    }

    /// <summary>Özet maç (yalnızca aranan oyuncunun kendi sayıları).</summary>
    sealed class PStored
    {
        public string Id, Map, Mode, Agent, AgentId;
        public DateTime Start;
        public int K, D, A, Score, Head, Body, Leg, Dmg, Tier, Mine, Theirs;
        public int Rounds => Mine + Theirs;
        public bool Won => Mine > Theirs;
    }

    sealed class PPlayer
    {
        public string Puuid, Name, Tag, Team, Party, AgentId, Agent;
        public int Tier, K, D, A, Score, Head, Body, Leg, Dmg;
    }

    /// <summary>Ayrıntılı maç: 10 oyuncu.</summary>
    sealed class PDetail
    {
        public string Id, Map, Mode, QueueId;
        public DateTime Start;
        public Dictionary<string, int> TeamRounds = new Dictionary<string, int>(); // "Red"/"Blue" → kazanılan round
        public List<PPlayer> Players = new List<PPlayer>();
        public int Rounds => TeamRounds.Values.Sum();
    }

    static class PlayerData
    {
        static readonly object lk = new object();
        static readonly Dictionary<string, KeyValuePair<DateTime, object>> cache = new Dictionary<string, KeyValuePair<DateTime, object>>();

        /// <summary>İstek durumu ("sınır: bekleniyor…") arayüze.</summary>
        public static event Action<string> Waiting;

        public sealed class NotFound : Exception { }

        /// <summary>HenrikDev çağrısı ("data" kısmı); 5 dk önbellek, 429'da bekleyip yeniden dener, 404'te NotFound.</summary>
        static object Get(string key, string path)
        {
            lock (lk) if (cache.TryGetValue(path, out var c) && DateTime.UtcNow - c.Key < TimeSpan.FromMinutes(5)) return c.Value;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    var data = Api(key, path);
                    lock (lk) cache[path] = new KeyValuePair<DateTime, object>(DateTime.UtcNow, data);
                    return data;
                }
                catch (WebException e)
                {
                    var r = e.Response as HttpWebResponse;
                    int code = r == null ? 0 : (int)r.StatusCode;
                    if (code == 404) throw new NotFound();
                    if (code != 429 || attempt >= 4) throw;
                    int wait = 10;
                    if (int.TryParse(r.Headers["Retry-After"], out int ra)) wait = Math.Max(2, Math.Min(60, ra));
                    Waiting?.Invoke(L.F("HenrikDev sınırı: {0} sn bekleniyor…", wait));
                    Thread.Sleep(wait * 1000);
                }
            }
        }

        static string E(string s) => Uri.EscapeDataString(s);

        public static PAccount Account(string key, string name, string tag) => ToAccount(Get(key, $"/valorant/v2/account/{E(name)}/{E(tag)}"));
        public static PAccount AccountByPuuid(string key, string puuid) => ToAccount(Get(key, $"/valorant/v2/by-puuid/account/{E(puuid)}"));

        static PAccount ToAccount(object d)
        {
            var a = new PAccount { Puuid = S(J(d, "puuid")), Name = S(J(d, "name")), Tag = S(J(d, "tag")), Region = S(J(d, "region")), Card = S(J(d, "card")), Level = I(J(d, "account_level")) };
            if (string.IsNullOrEmpty(a.Region)) a.Region = "eu";
            Seen.Add(a.Puuid, a.Name, a.Tag, a.Region);
            return a;
        }

        public static PRank Rank(string key, PAccount a)
        {
            object d;
            try { d = Get(key, $"/valorant/v3/by-puuid/mmr/{a.Region}/pc/{E(a.Puuid)}"); }
            catch (NotFound) { return new PRank(); } // hiç dereceli oynamamış
            var r = new PRank
            {
                Tier = I(J(d, "current", "tier", "id")), Rr = I(J(d, "current", "rr")),
                PeakTier = I(J(d, "peak", "tier", "id")), PeakSeason = S(J(d, "peak", "season", "short")),
            };
            foreach (var s in (J(d, "seasonal") as object[]) ?? new object[0])
                r.Seasons.Add(new PSeason { Short = S(J(s, "season", "short")), EndTier = I(J(s, "end_tier", "id")), Wins = I(J(s, "wins")), Games = I(J(s, "games")) });
            return r;
        }

        static DateTime Time(object o) =>
            DateTime.TryParse(S(o), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t.ToLocalTime() : DateTime.MinValue;

        /// <summary>Özet maçlar; page 1'den başlar, sayfa başına 25.</summary>
        public static List<PStored> Stored(string key, PAccount a, int page, out int total)
        {
            var root = Get(key, $"/valorant/v1/by-puuid/stored-matches/{a.Region}/{E(a.Puuid)}?size=25&page={page}");
            total = 0;
            // Api yalnızca "data"yı döner; toplam sayıyı bilmediğimiz için boş sayfa = son.
            var list = new List<PStored>();
            foreach (var m in (root as object[]) ?? new object[0])
            {
                string team = S(J(m, "stats", "team"));
                int red = I(J(m, "teams", "red")), blue = I(J(m, "teams", "blue"));
                bool isRed = team.Equals("Red", StringComparison.OrdinalIgnoreCase);
                list.Add(new PStored
                {
                    Id = S(J(m, "meta", "id")), Map = S(J(m, "meta", "map", "name")), Mode = S(J(m, "meta", "mode")),
                    Start = Time(J(m, "meta", "started_at")),
                    Agent = S(J(m, "stats", "character", "name")), AgentId = S(J(m, "stats", "character", "id")),
                    K = I(J(m, "stats", "kills")), D = I(J(m, "stats", "deaths")), A = I(J(m, "stats", "assists")), Score = I(J(m, "stats", "score")),
                    Head = I(J(m, "stats", "shots", "head")), Body = I(J(m, "stats", "shots", "body")), Leg = I(J(m, "stats", "shots", "leg")),
                    Dmg = I(J(m, "stats", "damage", "made")), Tier = I(J(m, "stats", "tier")),
                    Mine = isRed ? red : blue, Theirs = isRed ? blue : red,
                });
            }
            total = list.Count;
            return list;
        }

        /// <summary>Ayrıntılı maçlar, start'tan itibaren 10 tane.</summary>
        public static List<PDetail> Details(string key, PAccount a, int start)
        {
            var list = new List<PDetail>();
            foreach (var m in (Get(key, $"/valorant/v4/by-puuid/matches/{a.Region}/pc/{E(a.Puuid)}?size=10&start={start}") as object[]) ?? new object[0])
            {
                var d = new PDetail
                {
                    Id = S(J(m, "metadata", "match_id")), Map = S(J(m, "metadata", "map", "name")),
                    Mode = S(J(m, "metadata", "queue", "name")), QueueId = S(J(m, "metadata", "queue", "id")),
                    Start = Time(J(m, "metadata", "started_at")),
                };
                foreach (var t in (J(m, "teams") as object[]) ?? new object[0])
                    d.TeamRounds[S(J(t, "team_id"))] = I(J(t, "rounds", "won"));
                foreach (var p in (J(m, "players") as object[]) ?? new object[0])
                {
                    var pl = new PPlayer
                    {
                        Puuid = S(J(p, "puuid")), Name = S(J(p, "name")), Tag = S(J(p, "tag")), Team = S(J(p, "team_id")), Party = S(J(p, "party_id")),
                        AgentId = S(J(p, "agent", "id")), Agent = S(J(p, "agent", "name")), Tier = I(J(p, "tier", "id")),
                        K = I(J(p, "stats", "kills")), D = I(J(p, "stats", "deaths")), A = I(J(p, "stats", "assists")), Score = I(J(p, "stats", "score")),
                        Head = I(J(p, "stats", "headshots")), Body = I(J(p, "stats", "bodyshots")), Leg = I(J(p, "stats", "legshots")),
                        Dmg = I(J(p, "stats", "damage", "dealt")),
                    };
                    d.Players.Add(pl);
                    Seen.Add(pl.Puuid, pl.Name, pl.Tag, a.Region);
                }
                list.Add(d);
            }
            Seen.Save();
            return list;
        }
    }

    /// <summary>
    /// Görülen oyuncular (ad, etiket, bölge): yalnızca adla arama için öneri listesi. HenrikDev adla arama yapmıyor;
    /// arattığın ve onların maçlarında gördüğün oyuncular burada birikir.
    /// </summary>
    static class Seen
    {
        static readonly string FilePath = Path.Combine(Settings.Dir, "players-seen.json");
        static Dictionary<string, string[]> map;
        static readonly object lk = new object();
        static bool dirty;

        static void EnsureLoaded()
        {
            if (map != null) return;
            try { map = File.Exists(FilePath) ? new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(FilePath)) : null; } catch { }
            if (map == null) map = new Dictionary<string, string[]>();
        }

        public static void Add(string puuid, string name, string tag, string region)
        {
            if (string.IsNullOrEmpty(puuid) || string.IsNullOrEmpty(name)) return;
            lock (lk)
            {
                EnsureLoaded();
                if (map.TryGetValue(puuid, out var v) && v[0] == name && v[1] == tag) return;
                map[puuid] = new[] { name, tag, region };
                dirty = true;
            }
        }

        public static void Save()
        {
            lock (lk)
            {
                if (!dirty) return;
                try { Directory.CreateDirectory(Settings.Dir); File.WriteAllText(FilePath, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(map)); dirty = false; } catch { }
            }
        }

        /// <summary>Adı eşleşen (tam eşleşme önce, sonra başlayan) en çok max oyuncu: puuid, ad, etiket.</summary>
        public static List<string[]> Find(string name, int max = 12)
        {
            var q = name.Trim().ToLower(new CultureInfo("tr-TR"));
            lock (lk)
            {
                EnsureLoaded();
                return map.Where(kv => kv.Value[0].ToLower(new CultureInfo("tr-TR")).StartsWith(q))
                    .OrderBy(kv => kv.Value[0].Length == q.Length ? 0 : 1).ThenBy(kv => kv.Value[0], StringComparer.CurrentCultureIgnoreCase)
                    .Take(max).Select(kv => new[] { kv.Key, kv.Value[0], kv.Value[1] }).ToList();
            }
        }
    }
}
