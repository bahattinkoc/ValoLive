using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ValoLive
{
    /// <summary>
    /// Arayüz dili. Metinlerin kaynağı koddaki Türkçe metindir: L.T("…") onu seçili dile çevirir. Çeviriler exe'ye
    /// gömülü lang\xx.txt dosyalarında "Türkçe&lt;TAB&gt;çeviri" satırları olarak durur; çevirisi olmayan metin Türkçe kalır.
    /// Diller Valorant sunucularının oyuncu kitlesine göre seçildi (EU/TR/ME, NA, LATAM, BR, AP, KR).
    /// Dil açılışta bir kez belirlenir; değişince uygulama yeniden başlar.
    /// </summary>
    static class L
    {
        public static readonly (string Code, string Name, string Api)[] Languages =
        {
            ("tr", "Türkçe", "tr-TR"), ("en", "English", "en-US"), ("de", "Deutsch", "de-DE"), ("fr", "Français", "fr-FR"),
            ("es", "Español", "es-ES"), ("it", "Italiano", "it-IT"), ("pl", "Polski", "pl-PL"), ("ru", "Русский", "ru-RU"),
            ("pt", "Português (BR)", "pt-BR"), ("ar", "العربية", "ar-AE"), ("ja", "日本語", "ja-JP"), ("ko", "한국어", "ko-KR"),
            ("zh", "繁體中文", "zh-TW"), ("th", "ไทย", "th-TH"), ("vi", "Tiếng Việt", "vi-VN"), ("id", "Bahasa Indonesia", "id-ID"),
        };

        public static string Code { get; private set; } = "tr";
        /// <summary>valorant-api.com dil kodu (ajan, harita, skin adları).</summary>
        public static string Api { get; private set; } = "tr-TR";
        public static CultureInfo Culture { get; private set; } = new CultureInfo("tr-TR");
        static Dictionary<string, string> map = new Dictionary<string, string>();

        /// <summary>setting: "auto" (Valorant'ın dili) ya da dil kodu.</summary>
        public static void Init(string setting)
        {
            string code = string.IsNullOrEmpty(setting) || setting == "auto" ? Detect() : setting;
            int i = Array.FindIndex(Languages, l => l.Code == code);
            if (i < 0) i = 1; // desteklenmeyen dil: İngilizce
            Code = Languages[i].Code; Api = Languages[i].Api;
            try { Culture = new CultureInfo(Api); } catch { Culture = CultureInfo.InvariantCulture; }
            map = Load(Code);
        }

        /// <summary>Riot istemcisinin Valorant için seçili dili; bulunamazsa Windows'un dili.</summary>
        public static string Detect()
        {
            try
            {
                var yaml = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Riot Games", "Metadata", "valorant.live", "valorant.live.product_settings.yaml");
                var m = Regex.Match(File.ReadAllText(yaml), @"^\s*locale:\s*""?([a-zA-Z]{2})_([a-zA-Z]{2})", RegexOptions.Multiline);
                if (m.Success) return Supported(m.Groups[1].Value.ToLowerInvariant());
            }
            catch { }
            return Supported(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
        }

        static string Supported(string code) => Languages.Any(l => l.Code == code) ? code : "en";

        static Dictionary<string, string> Load(string code)
        {
            var d = new Dictionary<string, string>();
            if (code == "tr") return d;
            using (var s = typeof(L).Assembly.GetManifestResourceStream("lang." + code + ".txt"))
            {
                if (s == null) return d;
                using (var r = new StreamReader(s, Encoding.UTF8))
                    for (string line; (line = r.ReadLine()) != null;)
                    {
                        int tab = line.IndexOf('\t');
                        if (tab <= 0 || line.StartsWith("#")) continue;
                        d[Unescape(line.Substring(0, tab))] = Unescape(line.Substring(tab + 1));
                    }
            }
            return d;
        }

        static string Unescape(string s) => s.IndexOf('\\') < 0 ? s : s.Replace("\\n", "\n").Replace("\\t", "\t");

        /// <summary>Metni seçili dile çevirir.</summary>
        public static string T(string tr) => tr != null && map.TryGetValue(tr, out var t) ? t : tr;

        /// <summary>Biçimli metin: {0}, {1}… yer tutucuları çevirilerde de aynen kalır.</summary>
        public static string F(string tr, params object[] args) => string.Format(Culture, T(tr), args);

        /// <summary>Yüzde: Türkçede %45, İngilizcede 45%, Fransızcada 45 % (sıra çeviriden gelir).</summary>
        public static string Pct(object n) => L.F("%{0}", n);

        // Marka adı Türkçe büyütmede "KİCK" olmasın.
        public static string Upper(string s) => (s ?? "").ToUpper(Culture).Replace("KİCK", "KICK");

        // ---------- Yazı tipleri ----------

        /// <summary>Chakra Petch Latin, Vietnam ve Tay harflerini içerir; Kiril, Arap ve Doğu Asya dilleri sistem yazı tipine geçer.</summary>
        public static bool DisplayFontFits => !(Code == "ru" || Code == "ar" || Code == "ja" || Code == "ko" || Code == "zh");

        /// <summary>Düz metin yazı tipi ("Segoe UI" yerine): Doğu Asya dillerinde kendi yazı tipleri.</summary>
        public static string Sans => Code == "ja" ? "Yu Gothic UI" : Code == "ko" ? "Malgun Gothic" : Code == "zh" ? "Microsoft JhengHei UI" : "Segoe UI";
        public static string SansBold => Code == "ja" ? "Yu Gothic UI Semibold" : Code == "ko" ? "Malgun Gothic" : Code == "zh" ? "Microsoft JhengHei UI" : "Segoe UI Semibold";

        /// <summary>Başlık yazı tipi ailesi: Chakra Petch (uygulamayla gelir) ya da dilin harflerini içeren kalın sistem yazı tipi.</summary>
        public static FontFamily DisplayFamily(out FontStyle style)
        {
            FontFamily family = null;
            if (DisplayFontFits)
            {
                var pfc = new PrivateFontCollection();
                var ttf = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fonts", "ChakraPetch-Bold.ttf");
                if (File.Exists(ttf)) pfc.AddFontFile(ttf);
                if (pfc.Families.Length > 0) family = pfc.Families[0];
            }
            if (family == null) family = new FontFamily(Sans);
            style = family.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold : FontStyle.Regular;
            return family;
        }
    }
}
