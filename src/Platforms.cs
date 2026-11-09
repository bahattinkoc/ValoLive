using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace ValoLive
{
    /// <summary>
    /// Yayın gidebilecek platformlar. Görüntü bir kez kodlanır; her açık platforma ayrı bir yayıncı süreç aynı akışı
    /// iletir (biri koparsa diğerleri etkilenmez). Yeni platform = Platform.All'a bir satır.
    /// </summary>
    sealed class Platform
    {
        public string Id, Name, DefaultUrl, KeyPage, KeyHint;
        public Color Brand;
        /// <summary>Panelden kopyalanan URL + anahtar → yayıncının bağlanacağı tam adres.</summary>
        public Func<string, string, string> Ingest = (url, key) => url.Trim().TrimEnd('/') + "/" + key.Trim();

        public static readonly Platform Kick = new Platform
        {
            Id = "kick", Name = "Kick", Brand = ColorTranslator.FromHtml("#53fc18"),
            DefaultUrl = "", KeyPage = "https://dashboard.kick.com/stream", KeyHint = L.T("Kick'ten URL ve key al"),
            Ingest = (url, key) => KickIngest(url) + "/" + key.Trim(),
        };

        public static readonly Platform YouTube = new Platform
        {
            Id = "youtube", Name = "YouTube", Brand = ColorTranslator.FromHtml("#ff0033"),
            DefaultUrl = "rtmps://a.rtmps.youtube.com/live2", KeyPage = "https://studio.youtube.com/channel/UC/livestreaming",
            KeyHint = L.T("YouTube Studio'dan yayın anahtarı al"),
        };

        public static readonly Platform[] All = { Kick, YouTube };

        public static Platform ById(string id) => All.FirstOrDefault(p => p.Id == id);

        /// <summary>Kick panelindeki URL'de uygulama yolu yok; o olmadan anahtar "app" sanılıp yayın reddediliyor.</summary>
        static string KickIngest(string url)
        {
            url = url.Trim().TrimEnd('/');
            int host = url.IndexOf("://", StringComparison.Ordinal);
            return host >= 0 && url.IndexOf('/', host + 3) < 0 ? url + "/app" : url;
        }
    }

    /// <summary>Bir platformun kayıtlı ayarı (settings.json). Anahtar DPAPI ile şifreli.</summary>
    public sealed class DestCfg
    {
        public string Id = "";
        public bool On;
        public string Url = "", KeyEnc = "";
    }

    /// <summary>Çalışan yayının bir hedefi: platform adı ve tam ingest adresi (anahtar dahil; günlüğe maskeli düşer).</summary>
    sealed class Target
    {
        public string Id, Name, Url;
    }
}
