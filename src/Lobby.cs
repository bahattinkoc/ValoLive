using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using static ValoLive.ValorantTracker;

namespace ValoLive
{
    sealed class LobbyPlayer
    {
        public string Puuid, TeamId, AgentId, Name, Tag, CardId;
        public string Selection;   // ajan seçiminde: "" bakıyor, "selected" seçti, "locked" kilitledi
        public bool Incognito, IsSelf, Ally;
        public int Level;          // hesap seviyesi; gizliyse -1
        public int AgentActLevel;  // seçtiği ajandaki bu act'in ustalık seviyesi (maçın kendi verisinden)
    }

    sealed class LobbyState
    {
        public string Phase = "";   // "", "pregame", "ingame"
        public string MatchId = "", Map = "", MapUrl = "";
        public List<LobbyPlayer> Players = new List<LobbyPlayer>();
    }

    /// <summary>
    /// Riot istemcisinin bilgisayardaki yerel API'si: yalnızca o anki maçın oyuncu listesi (ajan seçimi / maç içi).
    /// Oyuna ve belleğine dokunmaz; istemcinin kendi arayüzünün kullandığı HTTP uçlarını okur.
    /// Adını gizleyen (incognito) oyuncuların adı sorulmaz ve geçmişi hiç aranmaz.
    /// </summary>
    sealed class RiotLocal
    {
        // Riot'un yerel sunucu sertifikalarını imzalayan kök (Riot geliştirici sitesinde yayımlanan riotgames.pem).
        // Yerel bağlantıda doğrulama yalnızca bu köke sabitlenir; başka hiçbir sertifika kabul edilmez.
        static readonly X509Certificate2 RiotRoot = new X509Certificate2(Encoding.ASCII.GetBytes(
@"-----BEGIN CERTIFICATE-----
MIIEIDCCAwgCCQDJC+QAdVx4UDANBgkqhkiG9w0BAQUFADCB0TELMAkGA1UEBhMC
VVMxEzARBgNVBAgTCkNhbGlmb3JuaWExFTATBgNVBAcTDFNhbnRhIE1vbmljYTET
MBEGA1UEChMKUmlvdCBHYW1lczEdMBsGA1UECxMUTG9MIEdhbWUgRW5naW5lZXJp
bmcxMzAxBgNVBAMTKkxvTCBHYW1lIEVuZ2luZWVyaW5nIENlcnRpZmljYXRlIEF1
dGhvcml0eTEtMCsGCSqGSIb3DQEJARYeZ2FtZXRlY2hub2xvZ2llc0ByaW90Z2Ft
ZXMuY29tMB4XDTEzMTIwNDAwNDgzOVoXDTQzMTEyNzAwNDgzOVowgdExCzAJBgNV
BAYTAlVTMRMwEQYDVQQIEwpDYWxpZm9ybmlhMRUwEwYDVQQHEwxTYW50YSBNb25p
Y2ExEzARBgNVBAoTClJpb3QgR2FtZXMxHTAbBgNVBAsTFExvTCBHYW1lIEVuZ2lu
ZWVyaW5nMTMwMQYDVQQDEypMb0wgR2FtZSBFbmdpbmVlcmluZyBDZXJ0aWZpY2F0
ZSBBdXRob3JpdHkxLTArBgkqhkiG9w0BCQEWHmdhbWV0ZWNobm9sb2dpZXNAcmlv
dGdhbWVzLmNvbTCCASIwDQYJKoZIhvcNAQEBBQADggEPADCCAQoCggEBAKoJemF/
6PNG3GRJGbjzImTdOo1OJRDI7noRwJgDqkaJFkwv0X8aPUGbZSUzUO23cQcCgpYj
21ygzKu5dtCN2EcQVVpNtyPuM2V4eEGr1woodzALtufL3Nlyh6g5jKKuDIfeUBHv
JNyQf2h3Uha16lnrXmz9o9wsX/jf+jUAljBJqsMeACOpXfuZy+YKUCxSPOZaYTLC
y+0GQfiT431pJHBQlrXAUwzOmaJPQ7M6mLfsnpHibSkxUfMfHROaYCZ/sbWKl3lr
ZA9DbwaKKfS1Iw0ucAeDudyuqb4JntGU/W0aboKA0c3YB02mxAM4oDnqseuKV/CX
8SQAiaXnYotuNXMCAwEAATANBgkqhkiG9w0BAQUFAAOCAQEAf3KPmddqEqqC8iLs
lcd0euC4F5+USp9YsrZ3WuOzHqVxTtX3hR1scdlDXNvrsebQZUqwGdZGMS16ln3k
WObw7BbhU89tDNCN7Lt/IjT4MGRYRE+TmRc5EeIXxHkQ78bQqbmAI3GsW+7kJsoO
q3DdeE+M+BUJrhWorsAQCgUyZO166SAtKXKLIcxa+ddC49NvMQPJyzm3V+2b1roP
SvD2WV8gRYUnGmy/N0+u6ANq5EsbhZ548zZc+BI4upsWChTLyxt2RxR7+uGlS1+5
EcGfKZ+g024k/J32XP4hdho7WYAS2xMiV83CfLR/MNi8oSMaVQTdKD8cpgiWJk3L
XWehWA==
-----END CERTIFICATE-----"));

        const string Platform = "eyJwbGF0Zm9ybVR5cGUiOiJQQyIsInBsYXRmb3JtT1MiOiJXaW5kb3dzIiwicGxhdGZvcm1PU1ZlcnNpb24iOiIxMC4wLjE5MDQyLjEuMjU2LjY0Yml0IiwicGxhdGZvcm1DaGlwc2V0IjoiVW5rbm93biJ9";
        static readonly string LockFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Riot Games\Riot Client\Config\lockfile");
        static readonly string GameLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"VALORANT\Saved\Logs\ShooterGame.log");

        string version, region, shard;
        Dictionary<string, string> maps;
        readonly Dictionary<string, string[]> names = new Dictionary<string, string[]>();
        public string SelfPuuid { get; private set; }

        static bool LocalCert(object sender, X509Certificate cert, X509Chain _, SslPolicyErrors err)
        {
            if (cert == null) return false;
            using (var chain = new X509Chain())
            {
                chain.ChainPolicy.ExtraStore.Add(RiotRoot);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
                if (!chain.Build(new X509Certificate2(cert))) return false;
                return chain.ChainElements[chain.ChainElements.Count - 1].Certificate.Thumbprint == RiotRoot.Thumbprint;
            }
        }

        static object Http(string method, string url, IDictionary<string, string> headers, string body = null, bool local = false)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Timeout = 8000;
            if (local) req.ServerCertificateValidationCallback = LocalCert;
            foreach (var h in headers) req.Headers[h.Key] = h.Value;
            if (body != null)
            {
                req.ContentType = "application/json";
                var b = Encoding.UTF8.GetBytes(body);
                using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length);
            }
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(r.ReadToEnd());
        }

        static int Status(WebException e) => (int)((e.Response as HttpWebResponse)?.StatusCode ?? 0);

        /// <summary>Riot sunucuları için oturum başlıkları (istemcinin kendi tokenı); istemci kapalıysa açıklamalı istisna.</summary>
        Dictionary<string, string> Auth()
        {
            var ent = Local("/entitlements/v1/token");
            SelfPuuid = S(J(ent, "subject"));
            if (SelfPuuid == "") throw new InvalidOperationException(L.T("Riot hesabına giriş yapılmamış"));

            EnsureRegion();
            return new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer " + S(J(ent, "accessToken")),
                ["X-Riot-Entitlements-JWT"] = S(J(ent, "token")),
                ["X-Riot-ClientPlatform"] = Platform,
                ["X-Riot-ClientVersion"] = version,
            };
        }

        /// <summary>Riot istemcisinin bilgisayardaki yerel sunucusuna GET (lockfile'daki port ve şifreyle).</summary>
        static object Local(string path)
        {
            if (!File.Exists(LockFile)) throw new InvalidOperationException(L.T("Riot istemcisi kapalı"));
            string[] lf;
            using (var fs = new FileStream(LockFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs)) lf = sr.ReadToEnd().Split(':');
            if (lf.Length < 5) throw new InvalidOperationException(L.T("Riot istemcisi açılıyor…"));
            var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes("riot:" + lf[3]));
            return Http("GET", $"https://127.0.0.1:{lf[2]}{path}", new Dictionary<string, string> { ["Authorization"] = "Basic " + basic }, local: true);
        }

        /// <summary>
        /// Kendi durumun (istemcinin arkadaşlarına yayınladığı presence): maçtaysan harita, anlık skor, parti boyutu.
        /// Yalnızca kendi kaydın okunur; listedeki arkadaşlarının kayıtlarına bakılmaz.
        /// </summary>
        public object Presence()
        {
            if (SelfPuuid == null) Auth();
            var mine = ((J(Local("/chat/v4/presences"), "presences") as object[]) ?? new object[0])
                .FirstOrDefault(p => S(J(p, "puuid")) == SelfPuuid && S(J(p, "product")) == "valorant");
            string priv = S(J(mine, "private"));
            if (priv == "") return null;
            return new JavaScriptSerializer().DeserializeObject(Encoding.UTF8.GetString(Convert.FromBase64String(priv)));
        }

        /// <summary>Kendi rekabetçi maç listen (yeniden eskiye): (maç kimliği, başlangıç zamanı ms), toplam.</summary>
        public List<KeyValuePair<string, long>> CompetitiveHistory(int start, int end, out int total)
        {
            var h = Auth();
            var r = Http("GET", $"https://pd.{shard}.a.pvp.net/match-history/v1/history/{SelfPuuid}?startIndex={start}&endIndex={end}&queue=competitive", h);
            total = I(J(r, "Total"));
            return ((J(r, "History") as object[]) ?? new object[0])
                .Select(m => new KeyValuePair<string, long>(S(J(m, "MatchID")), Convert.ToInt64(J(m, "GameStartTime") ?? 0L))).ToList();
        }

        /// <summary>Bir oyuncunun rekabetçi rank bilgisi (Riot'un kariyer ucu).</summary>
        public object Mmr(string puuid) => Pd($"/mmr/v1/players/{puuid}");

        string currentAct;

        /// <summary>Şu anki act'in kimliği (rank bu act'e göre okunur).</summary>
        readonly object actLock = new object();

        string CurrentAct()
        {
            lock (actLock) return CurrentActLocked();
        }

        string CurrentActLocked()
        {
            if (currentAct != null) return currentAct;
            try
            {
                var h = Auth();
                var c = Http("GET", $"https://shared.{shard}.a.pvp.net/content-service/v3/content", h);
                currentAct = ((J(c, "Seasons") as object[]) ?? new object[0])
                    .Where(s => S(J(s, "Type")) == "act" && J(s, "IsActive") is bool a && a).Select(s => S(J(s, "ID"))).FirstOrDefault();
            }
            catch { }
            if (string.IsNullOrEmpty(currentAct)) // yedek: kendi son rekabetçi maçının act'i
                currentAct = S(J(Mmr(SelfPuuid ?? ""), "LatestCompetitiveUpdate", "SeasonID"));
            return currentAct;
        }

        /// <summary>Güncel rank (bu act), RR ve tüm zamanların en yükseği; act'te oynamamışsa tier 0.</summary>
        public void Rank(string puuid, out int tier, out int rr, out int peak) => Rank(puuid, out tier, out rr, out peak, out _, out _, out _);

        /// <summary>Rank + bu act'teki galibiyet/maç sayısı (yerleştirme dahil) ve liderlik sırası (Ölümsüzlük+; yoksa 0).</summary>
        public void Rank(string puuid, out int tier, out int rr, out int peak, out int wins, out int games, out int leaderboard)
        {
            var mmr = Mmr(puuid);
            string act = CurrentAct();
            var seasons = J(mmr, "QueueSkills", "competitive", "SeasonalInfoBySeasonID") as Dictionary<string, object>;
            tier = 0; rr = 0; peak = 0; wins = 0; games = 0; leaderboard = 0;
            if (seasons != null && act != null && seasons.TryGetValue(act, out var cur))
            {
                tier = I(J(cur, "CompetitiveTier")); rr = I(J(cur, "RankedRating"));
                wins = I(J(cur, "NumberOfWinsWithPlacements")); games = I(J(cur, "NumberOfGames")); leaderboard = I(J(cur, "LeaderboardRank"));
            }
            var latest = J(mmr, "LatestCompetitiveUpdate");
            if (tier == 0 && S(J(latest, "SeasonID")) == act) { tier = I(J(latest, "TierAfterUpdate")); rr = I(J(latest, "RankedRatingAfterUpdate")); }
            foreach (var s in seasons?.Values ?? Enumerable.Empty<object>())
            {
                peak = Math.Max(peak, I(J(s, "Rank")));
                if (J(s, "WinsByTier") is Dictionary<string, object> w)
                    foreach (var k in w.Keys) if (int.TryParse(k, out int t)) peak = Math.Max(peak, t);
            }
            peak = Math.Max(peak, tier);
        }

        /// <summary>Riot maç detayından bir oyuncunun özeti (HenrikDev'deki Info ile aynı biçim).</summary>
        public MatchInfo ToMatchInfo(object d, string puuid)
        {
            var players = (J(d, "players") as object[]) ?? new object[0];
            var me = players.FirstOrDefault(p => S(J(p, "subject")) == puuid);
            if (me == null) return null;
            string team = S(J(me, "teamId"));
            var teams = (J(d, "teams") as object[]) ?? new object[0];
            var mine = teams.FirstOrDefault(t => S(J(t, "teamId")) == team);
            var other = teams.FirstOrDefault(t => S(J(t, "teamId")) != team);
            int hs = 0, bs = 0, ls = 0;
            foreach (var r in (J(d, "roundResults") as object[]) ?? new object[0])
                foreach (var ps in (J(r, "playerStats") as object[]) ?? new object[0])
                {
                    if (S(J(ps, "subject")) != puuid) continue;
                    foreach (var dmg in (J(ps, "damage") as object[]) ?? new object[0])
                    { hs += I(J(dmg, "headshots")); bs += I(J(dmg, "bodyshots")); ls += I(J(dmg, "legshots")); }
                }
            int tier = I(J(me, "competitiveTier"));
            bool twoTeams = teams.Length == 2;
            return new MatchInfo
            {
                Id = S(J(d, "matchInfo", "matchId")),
                Result = J(mine, "won") is bool w && w ? "W" : twoTeams && J(other, "won") is bool l && l ? "L" : twoTeams ? "D" : "",
                Map = MapName(S(J(d, "matchInfo", "mapId"))),
                Queue = S(J(d, "matchInfo", "queueID")),
                TierId = tier, Tier = tier > 0 ? Tiers.Name(tier) : "",
                AgentId = S(J(me, "characterId")),
                Score = twoTeams && mine != null ? $"{I(J(mine, "roundsWon"))}-{I(J(other, "roundsWon"))}" : "",
                K = I(J(me, "stats", "kills")), D = I(J(me, "stats", "deaths")), A = I(J(me, "stats", "assists")),
                Hs = hs + bs + ls > 0 ? 100 * hs / (hs + bs + ls) : 0,
            };
        }

        /// <summary>Herhangi bir oyuncunun son maç listesi (kimlik, başlangıç zamanı, mod).</summary>
        public object History(string puuid, int count, string queue = null) =>
            Pd($"/match-history/v1/history/{puuid}?startIndex=0&endIndex={count}" + (queue != null ? "&queue=" + queue : ""));

        /// <summary>Bir maçın tam detayı (10 oyuncu, takımlar, skor).</summary>
        public object MatchDetails(string matchId) => Pd($"/match-details/v1/matches/{matchId}");

        /// <summary>Ajan Ustalığı (iç adı "bloomline"): ajan kimliği → [ömür boyu seviye, bu act'teki seviye].</summary>
        public Dictionary<string, int[]> Mastery(string puuid)
        {
            var d = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in (J(Pd($"/bloomline/v0/players/{puuid}"), "Bonsais") as object[]) ?? new object[0])
                d[S(J(b, "BonsaiID"))] = new[] { I(J(b, "Flourish", "LongLevel")), I(J(b, "Flourish", "ShortLevel")) };
            return d;
        }

        public sealed class RrUpdate { public string MatchId, MapId; public long Start; public int Earned, TierAfter, RrAfter; }

        /// <summary>Son rekabetçi maçlarda kazanılan/kaybedilen RR (yeniden eskiye).</summary>
        public List<RrUpdate> RrUpdates(string puuid, int count) =>
            ((J(Pd($"/mmr/v1/players/{puuid}/competitiveupdates?startIndex=0&endIndex={count}&queue=competitive"), "Matches") as object[]) ?? new object[0])
            .Select(m => new RrUpdate
            {
                MatchId = S(J(m, "MatchID")), MapId = S(J(m, "MapID")), Start = Convert.ToInt64(J(m, "MatchStartTime") ?? 0L),
                Earned = I(J(m, "RankedRatingEarned")), TierAfter = I(J(m, "TierAfterUpdate")), RrAfter = I(J(m, "RankedRatingAfterUpdate")),
            }).ToList();

        const string Vandal = "9c82e19d-4575-0200-1a81-3eacf00cf872", Phantom = "ee8e8d15-496b-07ac-e5f6-8fae5d4c7b1a";
        const string SocketChroma = "3ad1b2b2-acdb-4524-852f-954a76ddae0a";

        /// <summary>Maçtaki herkesin Vandal ve Phantom görünümü (renk varyantı kimliği): puuid → [vandal, phantom].</summary>
        public Dictionary<string, string[]> Skins(string matchId)
        {
            var h = Auth();
            var r = Http("GET", $"https://glz-{region}-1.{shard}.a.pvp.net/core-game/v1/matches/{matchId}/loadouts", h);
            var d = new Dictionary<string, string[]>();
            foreach (var l in (J(r, "Loadouts") as object[]) ?? new object[0])
            {
                var items = J(l, "Loadout", "Items") as Dictionary<string, object>;
                string Chroma(string gun) => items != null && items.TryGetValue(gun, out var it) ? S(J(it, "Sockets", SocketChroma, "Item", "ID")) : "";
                d[S(J(l, "Loadout", "Subject"))] = new[] { Chroma(Vandal), Chroma(Phantom) };
            }
            return d;
        }

        /// <summary>Kendi silah görünümlerin: silah kimliği → renk varyantı kimliği.</summary>
        public Dictionary<string, string> MyGuns()
        {
            if (SelfPuuid == null) Auth();
            return ((J(Pd($"/personalization/v3/players/{SelfPuuid}/playerloadout"), "Guns") as object[]) ?? new object[0])
                .GroupBy(g => S(J(g, "ID")).ToLowerInvariant()).ToDictionary(g => g.Key, g => S(J(g.First(), "ChromaID")));
        }

        /// <summary>Günlük mağazan: (görünüm seviyesi kimliği, VP fiyatı), kalan süre (sn).</summary>
        public List<KeyValuePair<string, int>> DailyStore(out int secondsLeft)
        {
            var h = Auth();
            var r = Http("POST", $"https://pd.{shard}.a.pvp.net/store/v3/storefront/{SelfPuuid}", h, "{}");
            secondsLeft = I(J(r, "SkinsPanelLayout", "SingleItemOffersRemainingDurationInSeconds"));
            return ((J(r, "SkinsPanelLayout", "SingleItemStoreOffers") as object[]) ?? new object[0])
                .Select(o => new KeyValuePair<string, int>(S(J(o, "OfferID")), (J(o, "Cost") as Dictionary<string, object>)?.Values.Select(I).FirstOrDefault() ?? 0))
                .ToList();
        }

        /// <summary>pd sunucusuna GET. Önce Auth: bölge/shard ilk çağrıda onun içinde öğrenilir, adres ondan sonra kurulmalı.</summary>
        object Pd(string path)
        {
            var h = Auth();
            return Http("GET", $"https://pd.{shard}.a.pvp.net{path}", h);
        }

        public string MapNameOf(string mapUrl) => MapName(mapUrl);

        /// <summary>O anki maç durumunu okur; Riot istemcisi kapalıysa ya da hata olursa açıklamalı istisna atar.</summary>
        public LobbyState Read()
        {
            var h = Auth();
            string glz = $"https://glz-{region}-1.{shard}.a.pvp.net";

            var st = new LobbyState();
            object match = null;
            try
            {
                st.MatchId = S(J(Http("GET", $"{glz}/core-game/v1/players/{SelfPuuid}", h), "MatchID"));
                match = Http("GET", $"{glz}/core-game/v1/matches/{st.MatchId}", h);
                st.Phase = "ingame";
            }
            catch (WebException e) when (Status(e) == 404) { }
            if (match == null)
            {
                try
                {
                    st.MatchId = S(J(Http("GET", $"{glz}/pregame/v1/players/{SelfPuuid}", h), "MatchID"));
                    match = Http("GET", $"{glz}/pregame/v1/matches/{st.MatchId}", h);
                    st.Phase = "pregame";
                }
                catch (WebException e) when (Status(e) == 404) { return st; } // maçta değil
            }

            st.MapUrl = S(J(match, "MapID"));
            st.Map = MapName(st.MapUrl);
            if (st.Phase == "ingame")
            {
                foreach (var p in (J(match, "Players") as object[]) ?? new object[0]) st.Players.Add(Player(p, S(J(p, "TeamID"))));
                string myTeam = st.Players.FirstOrDefault(p => p.IsSelf)?.TeamId;
                foreach (var p in st.Players) p.Ally = p.TeamId == myTeam;
            }
            else
            {
                // Ajan seçiminde yalnızca kendi takımın görünür (rakipler maç başlayınca gelir).
                var ally = J(match, "AllyTeam");
                foreach (var p in (J(ally, "Players") as object[]) ?? new object[0]) { var lp = Player(p, S(J(ally, "TeamID"))); lp.Ally = true; st.Players.Add(lp); }
            }

            // Ad servisi: yalnızca adını gizlemeyenler sorulur.
            var ask = st.Players.Where(p => !p.Incognito && !names.ContainsKey(p.Puuid)).Select(p => p.Puuid).ToList();
            if (ask.Count > 0)
            {
                var body = new JavaScriptSerializer().Serialize(ask);
                foreach (var n in (Http("PUT", $"https://pd.{shard}.a.pvp.net/name-service/v2/players", h, body) as object[]) ?? new object[0])
                    names[S(J(n, "Subject"))] = new[] { S(J(n, "GameName")), S(J(n, "TagLine")) };
            }
            foreach (var p in st.Players)
                if (!p.Incognito && names.TryGetValue(p.Puuid, out var nm)) { p.Name = nm[0]; p.Tag = nm[1]; }
            return st;
        }

        LobbyPlayer Player(object p, string team) => new LobbyPlayer
        {
            Puuid = S(J(p, "Subject")),
            TeamId = team,
            AgentId = S(J(p, "CharacterID")),
            Incognito = J(p, "PlayerIdentity", "Incognito") is bool b && b,
            IsSelf = S(J(p, "Subject")) == SelfPuuid,
            Level = J(p, "PlayerIdentity", "HideAccountLevel") is bool hl && hl ? -1 : I(J(p, "PlayerIdentity", "AccountLevel")),
            CardId = S(J(p, "PlayerIdentity", "PlayerCardID")),
            AgentActLevel = I(J(p, "bonsaiBloomline", "bloomlineShortLevel")),
            Selection = S(J(p, "CharacterSelectionState")),
        };

        readonly object regionLock = new object();

        void EnsureRegion()
        {
            lock (regionLock) EnsureRegionLocked();
        }

        void EnsureRegionLocked()
        {
            if (region != null && version != null) return;
            string log = "";
            try
            {
                using (var fs = new FileStream(GameLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs)) log = sr.ReadToEnd();
            }
            catch { }
            var m = Regex.Match(log, @"https://glz-([a-z0-9-]+?)-1\.([a-z0-9]+)\.a\.pvp\.net");
            if (!m.Success) throw new InvalidOperationException(L.T("Bölge bulunamadı: Valorant'ı bir kez açman gerekiyor"));
            region = m.Groups[1].Value; shard = m.Groups[2].Value;
            try { version = S(J(Http("GET", "https://valorant-api.com/v1/version", new Dictionary<string, string>()), "data", "riotClientVersion")); } catch { }
            if (string.IsNullOrEmpty(version))
            {
                var v = Regex.Match(log, @"CI server version: (\S+)");
                version = v.Success ? v.Groups[1].Value : "";
            }
        }

        readonly object mapLock = new object();

        string MapName(string mapUrl)
        {
            lock (mapLock) // paralel geçmiş yükleyicileri aynı anda ilk kez sorabilir
            {
                if (maps == null)
                {
                    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        foreach (var m in (J(Http("GET", "https://valorant-api.com/v1/maps?language=" + L.Api, new Dictionary<string, string>()), "data") as object[]) ?? new object[0])
                            d[S(J(m, "mapUrl"))] = Title(S(J(m, "displayName")));
                    }
                    catch { }
                    maps = d;
                }
                return maps.TryGetValue(mapUrl, out var n) ? n : mapUrl.Split('/').LastOrDefault() ?? "";
            }
        }

        static readonly System.Globalization.CultureInfo Tr = new System.Globalization.CultureInfo("tr-TR");

        /// <summary>"BREEZE" → "Breeze" (harita adları API'den büyük harfle geliyor).</summary>
        public static string Title(string s) => string.IsNullOrEmpty(s) ? s : Inv(s).TextInfo.ToTitleCase(s.ToLower(Inv(s)));

        // İngilizce adlar (BIND, SUMMIT) Türkçe kuralla küçültülünce "bınd" olur; Türkçe harf yoksa değişmez kültür.
        static System.Globalization.CultureInfo Inv(string s) => s.IndexOfAny("ÇĞİÖŞÜçğıöşü".ToCharArray()) >= 0 ? Tr : System.Globalization.CultureInfo.InvariantCulture;
    }

    /// <summary>Bir oyuncunun güncel rankı ve son 3 maçı (HenrikDev, herkese açık veriler).</summary>
    sealed class PlayerHistory
    {
        public List<MatchInfo> Matches;
        public string Error;
        // Güncel rank son maçlardan bağımsız sorulur: son 3 maç derecesiz/Swiftplay ise maçlarda rank yazmaz.
        public bool RankLoaded;
        public int RankId, Rr, PeakId;
        public string Rank, Peak;
        public int ActWins, ActGames, Leaderboard;
        public Dictionary<string, int[]> Mastery;          // ajan → [ömür boyu, bu act]
        public List<RiotLocal.RrUpdate> Form;               // son rekabetçi maçlardaki RR değişimleri
        public int Kills => Matches?.Sum(m => m.K) ?? 0;
        public int Deaths => Matches?.Sum(m => m.D) ?? 0;
        public int HsAvg => Matches == null || !Matches.Any(m => m.Hs > 0) ? 0 : (int)Matches.Where(m => m.Hs > 0).Average(m => m.Hs);

        public PlayerHistory Copy() => (PlayerHistory)MemberwiseClone();
    }

    /// <summary>
    /// "Maç odası": ajan seçiminde takım arkadaşlarını, maç başlayınca rakipleri de son 3 maçlarıyla listeler.
    /// Pencere ekran yakalamadan dışlanır (WDA_EXCLUDEFROMCAPTURE): yayına, kayda, ekran paylaşımına girmez.
    /// </summary>
    sealed partial class LobbyForm : Form
    {
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);

        // Ana pencereyle aynı tema (Theme.cs); kartlar zeminden bir ton açık.
        static readonly Color Bg = Theme.Bg;
        static readonly Color Field = Theme.Card;
        static readonly Color Card = Theme.Line;
        static readonly Color Txt = Theme.Txt;
        static readonly Color Mut = Theme.Mut;
        static readonly Color Red = Theme.Red;
        static readonly Color Win = ColorTranslator.FromHtml("#3ddc97");
        static readonly Color Loss = ColorTranslator.FromHtml("#ff5a6e");
        static readonly Color Warn = ColorTranslator.FromHtml("#ffb547");
        // Parti renkleri: aynı partideki oyuncuların satırında soldaki şerit.
        static readonly Color[] PartyColors = { ColorTranslator.FromHtml("#ffb547"), ColorTranslator.FromHtml("#7aa2ff"), ColorTranslator.FromHtml("#c890f5"), ColorTranslator.FromHtml("#35d985") };

        readonly string hdevKey, region;
        readonly RiotLocal riot = new RiotLocal();
        readonly Dictionary<string, PlayerHistory> history = new Dictionary<string, PlayerHistory>();
        readonly Queue<string> pending = new Queue<string>();
        readonly Queue<string> rankPending = new Queue<string>(); // önce herkesin rankı (hızlı), sonra geçmişler
        readonly Dictionary<string, Image> icons = new Dictionary<string, Image>();
        readonly System.Threading.Timer poll;
        readonly Thread worker;
        readonly CheckBox chkTop;
        volatile bool closed;
        LobbyState state = new LobbyState();
        string status = L.T("Bağlanıyor…");
        int polling;
        Dictionary<string, string[]> skins = new Dictionary<string, string[]>(); // puuid → [vandal, phantom] renk varyantı
        string skinsMatch;

        public LobbyForm(string hdevKey, string region)
        {
            this.hdevKey = hdevKey; this.region = region;
            Text = L.T("ValoLive — Maç Odası");
            BackColor = Bg; ForeColor = Txt;
            Font = new Font(L.Sans, 9.5f);
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
            ClientSize = new Size(ViewW, ViewH);
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            chkTop = new VToggle { Text = L.T("Üstte tut"), ForeColor = Mut, AutoSize = true };
            chkTop.Location = new Point(ClientSize.Width - 16 - VToggle.WidthFor(chkTop.Text, Font), 15);
            chkTop.CheckedChanged += (s, e) => TopMost = chkTop.Checked;
            Controls.Add(chkTop);

            worker = new Thread(Work) { IsBackground = true, Name = "lobby-history" };
            worker.Start();
            Encounters.Start();
            Encounters.Changed += Repaint;
            poll = new System.Threading.Timer(_ => Poll(), null, 0, 3000);
            alertTimer.Tick += (s, e) => CheckAlerts();
            alertTimer.Start();
        }

        /// <summary>Kendiliğinden açıldığında odak çalmasın (oyun alta düşmesin).</summary>
        public bool Quiet;
        protected override bool ShowWithoutActivation => Quiet;

        // ---------- Bildirim: tanıdık oyuncu, smurf, parti ----------

        public bool Alerts;
        readonly System.Windows.Forms.Timer alertTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        readonly HashSet<string> alerted = new HashSet<string>();
        string alertPhase = "";
        DateTime alertPhaseAt;

        /// <summary>
        /// Her 2 sn (UI): ilgili oyuncuların geçmişi yüklenince (en geç 25 sn sonra) dikkat çeken durumlar tek bildirimde
        /// toplanır. Ajan seçiminde takımın, maç başlayınca rakipler; her şey maç başına bir kez bildirilir.
        /// </summary>
        void CheckAlerts()
        {
            if (!Alerts) return;
            string phase = lastPhase;
            LobbyState st;
            lock (history) st = state;
            if (phase != alertPhase)
            {
                if (alertPhase == "" && phase != "") alerted.Clear(); // yeni maç
                alertPhase = phase; alertPhaseAt = DateTime.Now;
            }
            if (phase == "" || st.Players.Count == 0) return;
            var relevant = st.Players.Where(p => !p.IsSelf && !p.Incognito && p.Puuid != null && (phase == "pregame" ? p.Ally : true)).ToList();
            bool ready;
            lock (history) ready = relevant.All(p => history.TryGetValue(p.Puuid, out var h) && (h.Matches != null || h.Error != null));
            if (!ready && (DateTime.Now - alertPhaseAt).TotalSeconds < 25) return;

            var lines = new List<KeyValuePair<string, Color>>();
            foreach (var p in relevant)
            {
                string side = p.Ally ? L.T("takımında") : L.T("rakipte");
                var enc = EncountersOf(p, st.MatchId);
                if (enc.Count > 0 && alerted.Add("enc:" + p.Puuid))
                {
                    var tag = EncTag(enc);
                    lines.Add(new KeyValuePair<string, Color>(L.F("{0} · {1} {2} · daha önce {3} kez", tag.Key, p.Name, side, enc.Count), tag.Value));
                }
                PlayerHistory h;
                lock (history) h = history.TryGetValue(p.Puuid, out var hh) ? hh.Copy() : null;
                if (Smurf(p, h) && alerted.Add("smurf:" + p.Puuid))
                    lines.Add(new KeyValuePair<string, Color>($"SMURF? · {p.Name} {side}", Warn));
            }
            var groups = PartyGroups(st.Players);
            foreach (var grp in groups.GroupBy(kv => kv.Value))
            {
                var members = st.Players.Where(p => grp.Any(kv => kv.Key == p.Puuid)).ToList();
                if (members.Any(p => p.IsSelf) || members.Count < 2) continue; // kendi partin zaten biliniyor
                if (phase == "pregame" && !members.All(p => p.Ally)) continue;
                if (alerted.Add("party:" + string.Join(",", members.Select(p => p.Puuid).OrderBy(x => x))))
                    lines.Add(new KeyValuePair<string, Color>(members[0].Ally ? L.F("Takımında {0} kişilik parti", members.Count) : L.F("Rakipte {0} kişilik parti", members.Count), PartyColors[grp.Key % PartyColors.Length]));
            }
            if (lines.Count > 0) Toast.Show(L.T("MAÇ ODASI") + " · " + (phase == "pregame" ? L.T("AJAN SEÇİMİ") : L.T("MAÇ")), lines.Take(5).ToList());
        }

        // ---------- Karşılaşma defteri: tanıdık oyuncular ----------

        static readonly Color Known = ColorTranslator.FromHtml("#7aa2ff");
        string lastPhase = "";

        /// <summary>Son karşılaşmaya göre kısa etiket: rakipken yendiyse rövanş, takımken kazandıysanız eski dost.</summary>
        static KeyValuePair<string, Color> EncTag(List<Encounter> e)
        {
            var last = e[0];
            string r = last.Match.Outcome();
            if (!last.Teammate && r == "L") return new KeyValuePair<string, Color>(L.T("RÖVANŞ"), Loss);
            if (last.Teammate && r == "W") return new KeyValuePair<string, Color>(L.T("ESKİ DOST"), Win);
            if (e.Count >= 3) return new KeyValuePair<string, Color>(L.T("SIK GÖRÜLEN"), Warn);
            return new KeyValuePair<string, Color>(L.T("TANIDIK"), Known);
        }

        static string Ago(long ms)
        {
            var d = (DateTime.Now.Date - DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.Date).TotalDays;
            return d < 1 ? L.T("bugün") : d < 2 ? L.T("dün") : d < 7 ? L.F("{0:0} gün önce", d) : d < 31 ? L.F("{0:0} hafta önce", d / 7) : L.F("{0:0} ay önce", d / 30);
        }

        List<Encounter> EncountersOf(LobbyPlayer p, string matchId) =>
            p.Incognito || p.IsSelf ? new List<Encounter>() : Encounters.Find(p.Puuid, matchId); // gizli oyuncu aranmaz

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetWindowDisplayAffinity(Handle, 0x11); // WDA_EXCLUDEFROMCAPTURE: yakalamada görünmez
            Theme.DarkTitle(this);
        }

        void Poll()
        {
            if (closed || Interlocked.Exchange(ref polling, 1) == 1) return;
            try
            {
                var st = riot.Read();
                string s = st.Phase == "pregame" ? L.T("AJAN SEÇİMİ") + " · " + st.Map : st.Phase == "ingame" ? L.T("MAÇTA") + " · " + st.Map : null;
                if (st.Phase == "" && lastPhase == "ingame") Encounters.Poke(); // maç bitti: deftere eklensin
                if (lastPhase == "" && st.Phase != "") ClearMatchCaches(); // yeni maç: önceki lobinin tam maç verileri bellekte kalmasın
                if (st.Phase != "" && st.Phase != lastPhase) PhaseChanged(st.Phase);
                lastPhase = st.Phase;
                lock (history)
                {
                    if (st.Phase == "")
                    {
                        // Maç bitince son listeyi göstermeye devam et (maç sonu sohbeti için).
                        status = state.Players.Count > 0 ? L.T("SON MAÇ · maç bekleniyor") : L.T("Maç bekleniyor…");
                    }
                    else
                    {
                        state = st;
                        status = s;
                        foreach (var p in st.Players)
                            if (!p.Incognito && !history.ContainsKey(p.Puuid))
                            {
                                history[p.Puuid] = new PlayerHistory();
                                rankPending.Enqueue(p.Puuid);
                                pending.Enqueue(p.Puuid);
                                Monitor.PulseAll(history);
                            }
                    }
                }
                // Silah görünümleri maç başına bir kez (yalnızca maç içinde; ajan seçiminde Riot vermiyor).
                if (st.Phase == "ingame" && st.MatchId != skinsMatch)
                {
                    try { var sk = riot.Skins(st.MatchId); lock (history) { skins = sk; skinsMatch = st.MatchId; } }
                    catch (Exception e) { Diag.Event("ODA", "görünümler alınamadı: " + e.Message); skinsMatch = st.MatchId; }
                }
                RefreshFacts();
            }
            catch (WebException e) { status = L.T("Riot bağlantı hatası: ") + e.Message; }
            catch (Exception e) { status = e.Message; }
            finally { polling = 0; }
            Repaint();
        }

        /// <summary>
        /// Rank ve geçmişleri yükler. Önce Riot istemcisinin kendi bağlantısı: dakika sınırı yok, paralel
        /// (9 rank ~0,5 sn, 9 geçmiş birkaç sn). Riot hata verirse o oyuncu için HenrikDev'e düşülür
        /// (dakikada 30 istek: aralıklı ve sıralı). Önce herkesin rankı, sonra geçmişler.
        /// </summary>
        void Work()
        {
            while (!closed)
            {
                List<string> ranks, hists; string scout;
                lock (history)
                {
                    while (pending.Count == 0 && rankPending.Count == 0 && scoutPending.Count == 0 && !closed) Monitor.Wait(history, 1000);
                    if (closed) return;
                    ranks = rankPending.ToList(); rankPending.Clear();
                    hists = ranks.Count > 0 ? new List<string>() : pending.ToList();
                    if (ranks.Count == 0) pending.Clear();
                    // Oyun stili incelemesi en son ve tek tek: yeni gelen oyuncuların rank/geçmişi hep önce gelir.
                    scout = ranks.Count == 0 && hists.Count == 0 && scoutPending.Count > 0 ? scoutPending.Dequeue() : null;
                }
                if (ranks.Count > 0) Parallel(ranks, 5, LoadRank);
                else if (hists.Count > 0)
                {
                    Parallel(hists, 3, LoadHistory); // daha fazlası Riot'tan 429 (60 sn ceza) getiriyor
                    ScheduleExtrasRetry(hists);
                    EnqueueScout(hists);
                }
                else if (scout != null) ScoutOne(scout);
            }
        }

        static void Parallel(List<string> items, int degree, Action<string> work) =>
            System.Threading.Tasks.Parallel.ForEach(items, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = degree }, work);

        PlayerHistory Entry(string puuid) { lock (history) return history.TryGetValue(puuid, out var h) ? h : null; }

        void LoadRank(string puuid)
        {
            var h = Entry(puuid);
            if (h == null || closed) return;
            int tier = 0, rr = 0, peak = 0, wins = 0, games = 0, lb = 0;
            try { riot.Rank(puuid, out tier, out rr, out peak, out wins, out games, out lb); }
            catch (Exception e)
            {
                Diag.Event("ODA", "Riot rank alınamadı, HenrikDev'e düşülüyor: " + e.Message);
                try
                {
                    var mmr = HdevCall($"/valorant/v3/by-puuid/mmr/{region}/pc/{puuid}");
                    tier = I(J(mmr, "current", "tier", "id")); rr = I(J(mmr, "current", "rr")); peak = I(J(mmr, "peak", "tier", "id"));
                }
                catch { }
            }
            lock (history)
            {
                h.RankId = tier; h.Rr = rr;
                h.Rank = tier > 0 ? Tiers.Name(tier) : L.T("Derecesiz");
                h.PeakId = peak; h.Peak = peak > 0 ? Tiers.Name(peak) : null;
                h.ActWins = wins; h.ActGames = games; h.Leaderboard = lb;
                h.RankLoaded = true;
            }
            Repaint();
        }

        // Aynı lobide oyuncular aynı maçları paylaşabilir (parti arkadaşları): detaylar bir kez çekilir.
        readonly Dictionary<string, object> details = new Dictionary<string, object>();

        /// <summary>Maç detayları (oyuncu başına yüzlerce KB) her lobide birikmesin: yeni maçta bir öncekinin önbellekleri boşaltılır.</summary>
        void ClearMatchCaches()
        {
            lock (details) details.Clear();
            lock (scoutCache) scoutCache.Clear();
            lock (scoutParties) scoutParties.Clear();
        }

        void LoadHistory(string puuid)
        {
            var h = Entry(puuid);
            if (h == null || closed) return;
            List<MatchInfo> list = null; string error = null;
            try
            {
                var ids = ((J(riot.History(puuid, 3), "History") as object[]) ?? new object[0]).Select(m => S(J(m, "MatchID"))).Where(id => id != "").ToList();
                list = new List<MatchInfo>();
                foreach (var id in ids)
                {
                    object d;
                    lock (details) details.TryGetValue(id, out d);
                    if (d == null)
                    {
                        d = WithRetry(() => riot.MatchDetails(id));
                        lock (details) details[id] = d;
                    }
                    var mi = riot.ToMatchInfo(d, puuid);
                    if (mi != null) list.Add(mi);
                }
            }
            catch (Exception e)
            {
                Diag.Event("ODA", "Riot geçmişi alınamadı, HenrikDev'e düşülüyor: " + e.Message);
                try
                {
                    var data = (HdevCall($"/valorant/v4/by-puuid/matches/{region}/pc/{puuid}?size=3") as object[]) ?? new object[0];
                    list = data.Select(m => Info(m, puuid)).Where(m => m != null).ToList();
                }
                catch (Exception e2) { list = null; error = e2 is WebException || e2 is InvalidOperationException ? L.T("geçmiş alınamadı") : e2.Message; }
            }
            // Ek bilgiler (ajan ustalığı, RR gidişatı): biri gelmezse yalnızca o bilgi eksik kalır.
            Dictionary<string, int[]> mastery = null; List<RiotLocal.RrUpdate> form = null;
            try { mastery = (Dictionary<string, int[]>)WithRetry(() => riot.Mastery(puuid)); } catch { }
            try { form = (List<RiotLocal.RrUpdate>)WithRetry(() => riot.RrUpdates(puuid, 5)); } catch { }
            lock (history) { h.Matches = list; h.Error = error; h.Mastery = mastery; h.Form = form; }
            Repaint();
        }

        // ---------- Oyun stili incelemesi (oyuncu başına son 8 normal maç) ----------

        readonly Queue<string> scoutPending = new Queue<string>();
        readonly HashSet<string> scoutBusy = new HashSet<string>();                         // sırada ya da inceleniyor
        readonly Dictionary<string, List<PMatch>> scouted = new Dictionary<string, List<PMatch>>();
        readonly Dictionary<string, Dictionary<string, PMatch>> scoutCache = new Dictionary<string, Dictionary<string, PMatch>>(); // maç → lobideki oyuncular
        readonly List<List<string>> scoutParties = new List<List<string>>();             // incelenen maçlarda aynı partide görülenler
        ScoutForm scoutForm;

        List<PMatch> ScoutedOf(string puuid) { if (puuid == null) return null; lock (history) return scouted.TryGetValue(puuid, out var l) ? l : null; }

        // ---------- İstihbarat (Intel.cs): satırda iki etiket, üstte brifing, oyuncu penceresinde hepsi ----------

        readonly Dictionary<string, List<long>> starts = new Dictionary<string, List<long>>();
        Dictionary<string, List<Intel.Fact>> facts = new Dictionary<string, List<Intel.Fact>>();

        List<Intel.Fact> FactsOf(string puuid) { if (puuid == null) return new List<Intel.Fact>(); lock (history) return facts.TryGetValue(puuid, out var f) ? f : new List<Intel.Fact>(); }

        /// <summary>Arka planda (Poll): bulgular çizimde değil burada hesaplanır; ajan seçimi değişince de güncellenir.</summary>
        void RefreshFacts()
        {
            List<LobbyPlayer> players; string map;
            lock (history) { players = state.Players.ToList(); map = state.MapUrl; }
            List<List<string>> parts;
            lock (scoutParties) parts = scoutParties.Select(l => l.ToList()).ToList();
            var res = new Dictionary<string, List<Intel.Fact>>();
            foreach (var p in players)
            {
                if (p.Puuid == null || p.Incognito || p.IsSelf) continue;
                PlayerHistory h; List<long> st;
                lock (history) { h = history.TryGetValue(p.Puuid, out var hh) ? hh.Copy() : null; starts.TryGetValue(p.Puuid, out st); }
                var duos = players.Where(o => o != p && o.Puuid != null && !o.Incognito && !o.IsSelf)
                    .Select(o => new KeyValuePair<LobbyPlayer, int>(o, parts.Count(gr => gr.Contains(p.Puuid) && gr.Contains(o.Puuid))))
                    .Where(kv => kv.Value > 0).ToList();
                try
                {
                    var list = Intel.For(new Intel.Input { P = p, H = h, Ms = ScoutedOf(p.Puuid), MapUrl = map, Starts = st, Duos = duos, AgentName = ValApi.AgentName });
                    if (Smurf(p, h))
                        list.Insert(0, new Intel.Fact { Puuid = p.Puuid, Pill = "SMURF?", Score = 88, Color = Warn, Kind = Intel.Kinds.Danger,
                            Text = L.F("Hesap seviyesine göre çok güçlü (Sv {0}, {1}): yeni hesap olabilir, hafife alma.", p.Level, h.Rank ?? "?") });
                    res[p.Puuid] = list;
                }
                catch (Exception e) { Diag.Event("ODA", "istihbarat çıkarılamadı: " + e.Message); }
            }
            try { Intel.Warm(map); } catch { } // Harita sekmesinin bölgeleri
            lock (history) facts = res;
        }

        /// <summary>Geçmişi yüklenen oyuncuları incelemeye sıraya koyar: önce rakipler, kendin hariç.</summary>
        void EnqueueScout(List<string> batch)
        {
            lock (history)
            {
                var order = batch.Select(id => state.Players.FirstOrDefault(p => p.Puuid == id)).Where(p => p != null && !p.IsSelf && !p.Incognito).OrderBy(p => p.Ally ? 1 : 0);
                foreach (var p in order)
                    if (!scouted.ContainsKey(p.Puuid) && scoutBusy.Add(p.Puuid)) scoutPending.Enqueue(p.Puuid);
                Monitor.PulseAll(history);
            }
        }

        void ScoutOne(string puuid)
        {
            HashSet<string> lobby;
            lock (history) lobby = new HashSet<string>(state.Players.Where(p => !p.Incognito && p.Puuid != null).Select(p => p.Puuid));
            var list = new List<PMatch>();
            try
            {
                var hist = (J(WithRetry(() => riot.History(puuid, 15)), "History") as object[]) ?? new object[0];
                // Maç saatleri (tüm modlar): bugünkü oturumun uzunluğu için.
                lock (history) starts[puuid] = hist.Select(m => Convert.ToInt64(J(m, "GameStartTime") ?? 0L)).Where(t => t > 0).ToList();
                var ids = hist.Where(m => Scout.Queues.Contains(S(J(m, "QueueID")))).Select(m => S(J(m, "MatchID"))).Take(8).ToList();
                foreach (var id in ids)
                {
                    if (closed) return;
                    PMatch pm = null;
                    lock (scoutCache) if (scoutCache.TryGetValue(id, out var known)) known.TryGetValue(puuid, out pm);
                    if (pm == null)
                    {
                        object d;
                        lock (details) details.TryGetValue(id, out d);
                        if (d == null)
                        {
                            try { d = WithRetry(() => riot.MatchDetails(id)); }
                            catch (WebException e) when (Status(e) == 429) { Thread.Sleep(20000); continue; } // Riot "yavaşla": bekle, bu maçı atla
                            catch (WebException) { continue; }
                        }
                        // Aynı maçta lobideki başka oyuncular da olabilir (parti): hepsi bir kerede çözümlenir.
                        var players = (J(d, "players") as object[]) ?? new object[0];
                        var per = new Dictionary<string, PMatch>();
                        foreach (var sub in players.Select(p => S(J(p, "subject"))).Where(lobby.Contains))
                        {
                            var a = Scout.Analyze(d, sub);
                            if (a != null) per[sub] = a;
                        }
                        lock (scoutCache) scoutCache[id] = per;
                        var groups = players.Where(p => lobby.Contains(S(J(p, "subject"))) && S(J(p, "partyId")) != "")
                            .GroupBy(p => S(J(p, "partyId"))).Where(gr => gr.Count() > 1).Select(gr => gr.Select(p => S(J(p, "subject"))).ToList());
                        lock (scoutParties) scoutParties.AddRange(groups);
                        per.TryGetValue(puuid, out pm);
                    }
                    if (pm != null) { list.Add(pm); lock (history) scouted[puuid] = list.ToList(); Repaint(); }
                }
            }
            catch (Exception e) { Diag.Event("ODA", "oyun stili incelenemedi: " + e.Message); }
            lock (history) { scouted[puuid] = list; scoutBusy.Remove(puuid); }
            Repaint();
        }

        static int Status(WebException e) => (int)((e.Response as HttpWebResponse)?.StatusCode ?? 0);

        System.Threading.Timer extrasRetry;

        /// <summary>Ustalık/form isteği "yavaşla" (429) ile boş kaldıysa 15 sn sonra tek tek bir kez daha sorulur.</summary>
        void ScheduleExtrasRetry(List<string> batch)
        {
            List<string> missing;
            lock (history) missing = batch.Where(p => history.TryGetValue(p, out var h) && (h.Mastery == null || h.Form == null)).ToList();
            if (missing.Count == 0) return;
            extrasRetry?.Dispose();
            extrasRetry = new System.Threading.Timer(_ =>
            {
                foreach (var p in missing)
                {
                    if (closed) return;
                    var h = Entry(p);
                    if (h == null) continue;
                    try { if (h.Mastery == null) { var m = riot.Mastery(p); lock (history) h.Mastery = m; } } catch { }
                    try { if (h.Form == null) { var f = riot.RrUpdates(p, 5); lock (history) h.Form = f; } } catch { }
                    Repaint();
                }
            }, null, 15000, Timeout.Infinite);
        }

        /// <summary>Riot "yavaşla" derse (429) kısa bekleyip bir kez daha dener.</summary>
        static object WithRetry(Func<object> call)
        {
            try { return call(); }
            catch (WebException e) when ((int)((e.Response as HttpWebResponse)?.StatusCode ?? 0) == 429)
            {
                Thread.Sleep(3000);
                return call();
            }
        }

        // HenrikDev yedeği: dakikada 30 istek sınırı → istekler arası en az 2,2 sn, tek tek.
        readonly object hdevLock = new object();
        DateTime hdevLast = DateTime.MinValue;

        object HdevCall(string path)
        {
            if (hdevKey == "") throw new InvalidOperationException(L.T("HenrikDev anahtarı yok")); // yedek isteğe bağlı: Riot istemcisi yeterli
            lock (hdevLock)
            {
                var wait = hdevLast.AddMilliseconds(2200) - DateTime.Now;
                if (wait > TimeSpan.Zero) Thread.Sleep(wait);
                try { return Api(hdevKey, path); }
                finally { hdevLast = DateTime.Now; }
            }
        }

        void Repaint() { if (!closed && IsHandleCreated) try { BeginInvoke((Action)Invalidate); } catch { } }

        Dictionary<string, int> parties = new Dictionary<string, int>();

        /// <summary>
        /// Parti tespiti: çekilen geçmiş maç detaylarında iki oyuncu aynı partyId ile görünüyorsa birlikte sıraya
        /// girmişlerdir. Bu lobideki oyuncular bu bağlarla gruplanır (2+ kişilik gruplar renk alır).
        /// </summary>
        Dictionary<string, int> PartyGroups(List<LobbyPlayer> players)
        {
            var ids = new HashSet<string>(players.Where(p => !p.Incognito).Select(p => p.Puuid));
            var parent = ids.ToDictionary(x => x, x => x);
            string Find(string x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            List<object> ds;
            lock (details) ds = details.Values.ToList();
            lock (scoutParties)
                foreach (var grp in scoutParties)
                    for (int i = 1; i < grp.Count; i++) if (ids.Contains(grp[0]) && ids.Contains(grp[i])) parent[Find(grp[i])] = Find(grp[0]);
            foreach (var d in ds)
            {
                var byParty = ((J(d, "players") as object[]) ?? new object[0])
                    .Where(p => ids.Contains(S(J(p, "subject"))) && S(J(p, "partyId")) != "")
                    .GroupBy(p => S(J(p, "partyId")));
                foreach (var grp in byParty)
                {
                    var m = grp.Select(p => S(J(p, "subject"))).ToList();
                    for (int i = 1; i < m.Count; i++) parent[Find(m[i])] = Find(m[0]);
                }
            }
            var groups = ids.GroupBy(Find).Where(gr => gr.Count() > 1).ToList();
            var result = new Dictionary<string, int>();
            for (int i = 0; i < groups.Count; i++) foreach (var x in groups[i]) result[x] = i;
            return result;
        }

        /// <summary>Takımın bilinen ranklarının ortalaması ×100 (0 = bilinmiyor).</summary>
        int AvgTier(List<LobbyPlayer> team)
        {
            lock (history)
            {
                var t = team.Select(p => p.Puuid != null && history.TryGetValue(p.Puuid, out var h) ? h.RankId : 0).Where(x => x > 0).ToList();
                return t.Count == 0 ? 0 : (int)(t.Average() * 100);
            }
        }

        static string AvgText(int avg) => avg > 0 ? "  ·  " + L.T("ort.") + " " + Tiers.Name((int)Math.Round(avg / 100.0)) : "";

        /// <summary>Smurf belirtisi: hesap seviyesine göre fazla yüksek rank ya da çok yüksek isabet.</summary>
        static bool Smurf(LobbyPlayer p, PlayerHistory h)
        {
            if (p.Level <= 0 || h == null) return false;
            int tier = h.RankId;
            return (p.Level < 30 && tier >= 15) || (p.Level < 60 && tier >= 18) || (p.Level < 60 && h.HsAvg >= 38 && (h.Matches?.Count ?? 0) >= 3);
        }

        readonly Dictionary<string, string> agentNames = new Dictionary<string, string>();
        string AgentNameCached(string id)
        {
            lock (agentNames)
            {
                if (agentNames.TryGetValue(id, out var n)) return n == "" ? L.T("Ajan") : n;
                agentNames[id] = "";
            }
            ThreadPool.QueueUserWorkItem(_ => { var n = ValApi.AgentName(id); lock (agentNames) agentNames[id] = n; Repaint(); });
            return L.T("Ajan");
        }

        // Görünüm adı/görseli çizim sırasında ağdan beklenmesin: arka planda sorulur, gelince yeniden çizilir.
        readonly Dictionary<string, KeyValuePair<string, string>> chromaCache = new Dictionary<string, KeyValuePair<string, string>>();
        KeyValuePair<string, string> ValApiCached(string chroma)
        {
            if (string.IsNullOrEmpty(chroma)) return default(KeyValuePair<string, string>);
            lock (chromaCache)
            {
                if (chromaCache.TryGetValue(chroma, out var c)) return c;
                chromaCache[chroma] = default(KeyValuePair<string, string>);
            }
            ThreadPool.QueueUserWorkItem(_ => { var c = ValApi.Chroma(chroma); lock (chromaCache) chromaCache[chroma] = c; Repaint(); });
            return default(KeyValuePair<string, string>);
        }

        static string Queue(string q)
        {
            switch (q)
            {
                case "competitive": return L.T("Rekabetçi");
                case "unrated": return L.T("Derecesiz");
                case "swiftplay": return L.T("Hızlı");
                case "deathmatch": return "DM";
                case "hurm": case "teamdeathmatch": return "TDM";
                case "spikerush": return "Spike Rush";
                case "premier": return "Premier";
                default: return q;
            }
        }

        static void Draw(Graphics g, string s, Font f, Color c, float x, float y) { using (var b = new SolidBrush(c)) g.DrawString(s, f, b, x, y); }

        /// <summary>Ajan ikonu: önbellekten, yoksa arka planda indirip sonra yeniden çizer.</summary>
        Image AgentIcon(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (icons)
            {
                if (icons.TryGetValue(id, out var img)) return img;
                icons[id] = null;
            }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Image img = null;
                try
                {
                    var file = Path.Combine(Settings.Dir, "cache", "agent_" + id + ".png");
                    if (!File.Exists(file)) using (var wc = new WebClient()) wc.DownloadFile(OverlayRenderer.AgentIcon(id), file);
                    using (var src = Image.FromFile(file)) img = new Bitmap(src);
                }
                catch { }
                lock (icons) icons[id] = img;
                if (img != null) Repaint();
            });
            return null;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            closed = true;
            Encounters.Changed -= Repaint;
            poll.Dispose();
            extrasRetry?.Dispose();
            if (scoutForm != null && !scoutForm.IsDisposed) scoutForm.Close();
            alertTimer.Dispose();
            lock (history) Monitor.PulseAll(history);
            base.OnFormClosed(e);
        }
    }
}
