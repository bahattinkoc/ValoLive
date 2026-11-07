using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;
using static ValoLive.ValorantTracker;

namespace ValoLive
{
    /// <summary>
    /// valorant-api.com: oyunun sabit içerik verisi (harita görselleri ve koordinat dönüşümü, görünüm adları/ikonları,
    /// ajan adları, oyuncu kartları). Herkese açık, anahtarsız. Her sorgu bir kez yapılıp bellekte tutulur.
    /// </summary>
    static class ValApi
    {
        const string Base = "https://valorant-api.com/v1";
        static readonly Dictionary<string, object> cache = new Dictionary<string, object>();

        static object Get(string path)
        {
            lock (cache) if (cache.TryGetValue(path, out var hit)) return hit;
            object data = null;
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (var wc = new WebClient { Encoding = Encoding.UTF8 })
                    data = J(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(wc.DownloadString(Base + path)), "data");
            }
            catch { } // bulunamadı / bağlantı yok: boş kalır, yeniden sorulmaz
            lock (cache) cache[path] = data;
            return data;
        }

        public sealed class Map
        {
            public string Name, Minimap;
            public double XMul, YMul, XAdd, YAdd;

            /// <summary>Oyun koordinatı → minimap görselinde 0..1 konum (valorant-api'nin dönüşüm katsayılarıyla; x ve y yer değiştirir).</summary>
            public System.Drawing.PointF ToMinimap(double gameX, double gameY) =>
                new System.Drawing.PointF((float)(gameY * XMul + XAdd), (float)(gameX * YMul + YAdd));
        }

        /// <summary>Haritanın oyun içi yolu (/Game/Maps/Ascent/Ascent) → ad, minimap görseli ve koordinat dönüşümü.</summary>
        public static Map MapOf(string mapUrl)
        {
            var m = ((Get("/maps?language=" + L.Api) as object[]) ?? new object[0])
                .FirstOrDefault(x => string.Equals(S(J(x, "mapUrl")), mapUrl, StringComparison.OrdinalIgnoreCase));
            if (m == null) return null;
            return new Map
            {
                Name = RiotLocal.Title(S(J(m, "displayName"))), Minimap = S(J(m, "displayIcon")),
                XMul = D(J(m, "xMultiplier")), YMul = D(J(m, "yMultiplier")), XAdd = D(J(m, "xScalarToAdd")), YAdd = D(J(m, "yScalarToAdd")),
            };
        }

        /// <summary>Silah görünümünün renk varyantı → (ad, görsel). Görsel: tam görünüm, yoksa ikon.</summary>
        public static KeyValuePair<string, string> Chroma(string chromaId)
        {
            if (string.IsNullOrEmpty(chromaId)) return default(KeyValuePair<string, string>);
            var c = Get("/weapons/skinchromas/" + chromaId + "?language=" + L.Api);
            string img = S(J(c, "fullRender"));
            if (img == "") img = S(J(c, "displayIcon"));
            return new KeyValuePair<string, string>(Clean(S(J(c, "displayName"))), img);
        }

        /// <summary>Görünüm seviyesi (mağaza teklifi) → (ad, ikon).</summary>
        public static KeyValuePair<string, string> SkinLevel(string levelId)
        {
            var c = Get("/weapons/skinlevels/" + levelId + "?language=" + L.Api);
            return new KeyValuePair<string, string>(Clean(S(J(c, "displayName"))), S(J(c, "displayIcon")));
        }

        public static string AgentName(string agentId) => S(J(Get("/agents/" + agentId + "?language=" + L.Api), "displayName"));

        /// <summary>Oyuncu kartının geniş görseli (satır arka planı için).</summary>
        public static string CardWide(string cardId) => string.IsNullOrEmpty(cardId) ? null : $"https://media.valorant-api.com/playercards/{cardId}/wideart.png";

        // Görünüm adlarında seviye/renk eki satır sonu ile gelebiliyor ("Prime Vandal\nSeviye 4").
        static string Clean(string s) => s.Split('\n')[0].Trim();

        static double D(object o) { try { return o == null ? 0 : Convert.ToDouble(o); } catch { return 0; } }
    }
}
