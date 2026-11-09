using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using static ValoLive.ValorantTracker;

namespace ValoLive
{
    /// <summary>
    /// YouTube Data API (OAuth, masaüstü istemcisi + PKCE): yayının başlığı/etiketleri ve bot cevapları.
    /// Kullanıcı Google Cloud'da bir proje açar, Client ID/Secret burada kullanılır. Tokenlar DPAPI ile şifreli.
    /// Kota: günlük 10.000 birim; başlık 51, chat cevabı 50, yayın arama 1 birim. Chat okuma API'den geçmez (YouTubeChat).
    /// </summary>
    sealed class YouTubeApi
    {
        public const int RedirectPort = 53683;
        static string RedirectUri => $"http://127.0.0.1:{RedirectPort}/";
        const string Scope = "https://www.googleapis.com/auth/youtube";
        const string AuthUrl = "https://accounts.google.com/o/oauth2/v2/auth", TokenUrl = "https://oauth2.googleapis.com/token", Api = "https://www.googleapis.com/youtube/v3";

        readonly Settings cfg;
        readonly object tokenLock = new object();   // başlık ve chat iş parçacıkları aynı anda yenilemesin
        string access;
        DateTime accessExp;

        public YouTubeApi(Settings cfg) { this.cfg = cfg; ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }

        public bool HasApp => Settings.Unprotect(cfg.YtClientIdEnc) != "" && Settings.Unprotect(cfg.YtSecretEnc) != "";
        public bool Connected => Settings.Unprotect(cfg.YtRefreshEnc) != "";

        /// <summary>Bir canlı yayın (video kimliği = yayın kimliği) ve chat'inin kimliği.</summary>
        public sealed class Broadcast { public string Id, Title, ChatId; }

        // ---------- Giriş ----------

        /// <summary>Tarayıcıda Google onayı (en fazla 3 dk); tokenları kaydeder, kanal adını döner.</summary>
        public string Login()
        {
            string clientId = Settings.Unprotect(cfg.YtClientIdEnc);
            string verifier = OAuth.Verifier(), state = OAuth.State();
            var listeners = OAuth.Listen(RedirectPort, false); // adres 127.0.0.1 olarak verildi
            try
            {
                Process.Start($"{AuthUrl}?response_type=code&client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                              $"&scope={Uri.EscapeDataString(Scope)}&code_challenge={OAuth.Challenge(verifier)}&code_challenge_method=S256" +
                              $"&state={state}&access_type=offline&prompt=consent");
                string code = OAuth.WaitForCode(listeners, state, TimeSpan.FromMinutes(3), L.T("<h2>ValoLive YouTube'a bağlandı ✓</h2><p>Bu sekmeyi kapatabilirsin.</p>"),
                                                L.T("YouTube onayı reddedildi: "), L.T("YouTube onayı 3 dakika içinde gelmedi."));
                SaveTokens(PostForm(TokenUrl, new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code", ["code"] = code, ["client_id"] = clientId,
                    ["client_secret"] = Settings.Unprotect(cfg.YtSecretEnc), ["redirect_uri"] = RedirectUri, ["code_verifier"] = verifier,
                }));
            }
            finally { OAuth.Stop(listeners); }
            return Channel();
        }

        public void Logout()
        {
            cfg.YtRefreshEnc = ""; cfg.YtChannel = ""; access = null;
            cfg.Save();
        }

        void SaveTokens(object tok)
        {
            access = S(J(tok, "access_token"));
            accessExp = DateTime.Now.AddSeconds(Math.Max(60, I(J(tok, "expires_in")) - 60));
            var refresh = S(J(tok, "refresh_token"));
            if (refresh != "") cfg.YtRefreshEnc = Settings.Protect(refresh);
            cfg.Save();
        }

        string Token()
        {
            lock (tokenLock)
            {
                if (access != null && DateTime.Now < accessExp) return access;
                string refresh = Settings.Unprotect(cfg.YtRefreshEnc);
                if (refresh == "") throw new Exception(L.T("YouTube hesabı bağlı değil."));
                try
                {
                    SaveTokens(PostForm(TokenUrl, new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token", ["refresh_token"] = refresh,
                        ["client_id"] = Settings.Unprotect(cfg.YtClientIdEnc), ["client_secret"] = Settings.Unprotect(cfg.YtSecretEnc),
                    }));
                }
                catch (GoogleError e) when (e.Status == 400 || e.Status == 401)
                {
                    // Google, "test" durumundaki uygulamaların oturumunu 7 günde düşürür (bağlantı penceresinde anlatılıyor).
                    Logout();
                    throw new Exception(L.T("YouTube oturumu sona ermiş, yeniden bağlan."));
                }
                return access;
            }
        }

        // ---------- Kanal ve yayın ----------

        /// <summary>Bağlı kanalın adı (@kullanıcı adı, yoksa kanal adı); ayara yazılır.</summary>
        public string Channel()
        {
            var ch = First(J(Call("GET", "/channels?part=snippet&mine=true", null), "items"));
            if (ch == null) throw new Exception(L.T("Bu Google hesabında YouTube kanalı yok."));
            string name = S(J(ch, "snippet", "customUrl"));
            if (name == "") name = S(J(ch, "snippet", "title"));
            cfg.YtChannel = name; cfg.Save();
            return name;
        }

        /// <summary>Kanalın şu an canlı ("active") ya da sıradaki ("upcoming") yayını; yoksa null.</summary>
        public Broadcast Find(string status)
        {
            // broadcastType=all: "Şimdi yayınla" (kalıcı yayın anahtarı) yayınları da gelsin.
            var b = First(J(Call("GET", $"/liveBroadcasts?part=id,snippet&broadcastStatus={status}&broadcastType=all&maxResults=5", null), "items"));
            return b == null ? null : new Broadcast { Id = S(J(b, "id")), Title = S(J(b, "snippet", "title")), ChatId = S(J(b, "snippet", "liveChatId")) };
        }

        /// <summary>Yayının başlığı ve etiketleri; kategori Oyun (20). Açıklama ve dil ayarları korunur.</summary>
        public void SetInfo(string videoId, string title, string[] tags)
        {
            var v = First(J(Call("GET", "/videos?part=snippet&id=" + Uri.EscapeDataString(videoId), null), "items"));
            if (v == null) throw new Exception(L.T("YouTube yayını bulunamadı."));
            var old = J(v, "snippet");
            // Gönderilmeyen snippet alanları YouTube'da silinir: korunacakları geri yaz.
            var sn = new Dictionary<string, object> { ["title"] = CleanTitle(title, S(J(old, "title"))), ["description"] = S(J(old, "description")), ["categoryId"] = "20" };
            foreach (var k in new[] { "defaultLanguage", "defaultAudioLanguage" }) if (S(J(old, k)) != "") sn[k] = S(J(old, k));
            if (tags != null && tags.Length > 0) sn["tags"] = tags;
            else if (J(old, "tags") is object[] t) sn["tags"] = t;
            Call("PUT", "/videos?part=snippet", new Dictionary<string, object> { ["id"] = videoId, ["snippet"] = sn });
        }

        /// <summary>YouTube başlığı en fazla 100 karakter, "&lt;" ve "&gt;" yasak.</summary>
        static string CleanTitle(string title, string fallback)
        {
            var t = (title ?? "").Replace("<", "").Replace(">", "").Trim();
            if (t == "") return fallback;
            return t.Length > 100 ? t.Substring(0, 99) + "…" : t;
        }

        /// <summary>Yayının chat'ine kanal hesabından mesaj (YouTube sınırı 200 karakter).</summary>
        public void SendChat(string chatId, string text)
        {
            if (text.Length > 200) text = text.Substring(0, 197) + "…";
            Call("POST", "/liveChatMessages?part=snippet", new Dictionary<string, object>
            {
                ["snippet"] = new Dictionary<string, object>
                {
                    ["liveChatId"] = chatId, ["type"] = "textMessageEvent",
                    ["textMessageDetails"] = new Dictionary<string, object> { ["messageText"] = text },
                },
            });
        }

        // ---------- HTTP ----------

        sealed class GoogleError : Exception
        {
            public readonly int Status;
            public GoogleError(int status, string msg) : base(msg) { Status = status; }
        }

        object Call(string method, string path, object body, bool retried = false)
        {
            var rq = (HttpWebRequest)WebRequest.Create(Api + path);
            rq.Method = method; rq.Accept = "application/json"; rq.Timeout = 15000;
            rq.Headers["Authorization"] = "Bearer " + Token();
            if (body != null)
            {
                var b = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(body));
                rq.ContentType = "application/json";
                using (var s = rq.GetRequestStream()) s.Write(b, 0, b.Length);
            }
            try { return Read(rq); }
            catch (GoogleError e) when (e.Status == 401 && !retried) { access = null; return Call(method, path, body, true); }
        }

        static object PostForm(string url, Dictionary<string, string> form)
        {
            var rq = (HttpWebRequest)WebRequest.Create(url);
            rq.Method = "POST"; rq.ContentType = "application/x-www-form-urlencoded"; rq.Accept = "application/json"; rq.Timeout = 15000;
            var b = Encoding.ASCII.GetBytes(string.Join("&", form.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value ?? ""))));
            using (var s = rq.GetRequestStream()) s.Write(b, 0, b.Length);
            return Read(rq);
        }

        static object Read(HttpWebRequest rq)
        {
            try
            {
                using (var r = (HttpWebResponse)rq.GetResponse())
                {
                    var txt = new StreamReader(r.GetResponseStream()).ReadToEnd();
                    return txt.Trim() == "" ? null : new JavaScriptSerializer().DeserializeObject(txt);
                }
            }
            catch (WebException e) when (e.Response is HttpWebResponse r)
            {
                string txt = "";
                try { txt = new StreamReader(r.GetResponseStream()).ReadToEnd(); } catch { }
                string msg = "";
                try
                {
                    // API: {"error":{"message":…}}; token: {"error":"invalid_grant","error_description":…}
                    var j = new JavaScriptSerializer().DeserializeObject(txt);
                    msg = S(J(j, "error", "message"));
                    if (msg == "") msg = S(J(j, "error_description"));
                    if (msg == "") msg = S(J(j, "error"));
                }
                catch { }
                throw new GoogleError((int)r.StatusCode, $"YouTube {(int)r.StatusCode}: {(msg != "" ? msg : r.StatusDescription)}");
            }
        }

        static object First(object arr) => (arr as object[])?.FirstOrDefault();
    }

    /// <summary>
    /// YouTube chat'ini salt okunur dinler: YouTube'un kendi açılır chat penceresinin kullandığı yol (giriş ve kota
    /// gerektirmez; resmi API ile okumak günlük kotayı birkaç saatte bitirirdi). Resmi değil, YouTube değiştirirse
    /// bozulabilir: o durumda yalnızca YouTube mesajları gelmez. Açık yayının video kimliği findVideo'dan gelir;
    /// yayın yokken 15 sn'de bir yeniden bakılır, yayın bitince yenisi beklenir.
    /// </summary>
    sealed class YouTubeChat : IDisposable
    {
        const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";
        readonly Func<string> findVideo;
        readonly CancellationTokenSource cts = new CancellationTokenSource();
        readonly HashSet<string> seen = new HashSet<string>();
        readonly Queue<string> seenOrder = new Queue<string>();
        public event Action<ChatMessage> Message;
        public event Action<string> Status;

        public YouTubeChat(Func<string> findVideo)
        {
            this.findVideo = findVideo;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            new Thread(Run) { IsBackground = true, Name = "youtube-chat" }.Start();
        }

        void Run()
        {
            int fails = 0;
            while (!cts.IsCancellationRequested)
            {
                int wait = 5000;
                try
                {
                    string video = findVideo();
                    if (string.IsNullOrEmpty(video))
                    {
                        Status?.Invoke(L.T("yayın bekleniyor"));
                        wait = 15000;
                    }
                    else
                    {
                        Status?.Invoke(L.T("bağlanıyor"));
                        Listen(video, () => { fails = 0; Status?.Invoke(L.T("bağlı")); });
                        Diag.Event("YT", "chat bitti (yayın kapandı)");
                    }
                }
                catch (Exception e)
                {
                    if (cts.IsCancellationRequested) return;
                    fails++;
                    Status?.Invoke(L.T("koptu: ") + e.Message);
                    Diag.Event("YT", "chat bağlantısı: " + e.Message);
                    wait = Math.Min(30000, 1000 * (1 << Math.Min(5, fails)));
                }
                cts.Token.WaitHandle.WaitOne(wait);
            }
        }

        void Listen(string video, Action connected)
        {
            string html = Get($"https://www.youtube.com/live_chat?is_popout=1&v={Uri.EscapeDataString(video)}");
            string key = Between(html, "\"INNERTUBE_API_KEY\":\"", "\"");
            string ver = Between(html, "\"INNERTUBE_CONTEXT_CLIENT_VERSION\":\"", "\"");
            if (ver == "") ver = "2.20261105.05.01";
            var data = Json(ExtractObject(html, "ytInitialData"));
            var lcr = J(data, "contents", "liveChatRenderer");
            if (lcr == null)
            {
                // Chat kapalı ya da yayın henüz chat açmadı: YouTube'un kendi açıklaması.
                string why = Runs(J(data, "contents", "messageRenderer", "text", "runs"));
                throw new Exception(why != "" ? why : L.T("chat bulunamadı"));
            }
            // "Canlı sohbet" (tüm mesajlar); yoksa varsayılan "Öne çıkan sohbet".
            var menu = J(lcr, "header", "liveChatHeaderRenderer", "viewSelector", "sortFilterSubMenuRenderer", "subMenuItems") as object[];
            string cont = S(J(menu?.LastOrDefault(), "continuation", "reloadContinuationData", "continuation"));
            if (cont == "") cont = NextContinuation(lcr, out _);
            if (cont == "") throw new Exception(L.T("chat bulunamadı"));

            string url = "https://www.youtube.com/youtubei/v1/live_chat/get_live_chat?prettyPrint=false" + (key != "" ? "&key=" + key : "");
            bool first = true;
            while (!cts.IsCancellationRequested)
            {
                var body = new JavaScriptSerializer().Serialize(new Dictionary<string, object>
                {
                    ["context"] = new Dictionary<string, object> { ["client"] = new Dictionary<string, object> { ["clientName"] = "WEB", ["clientVersion"] = ver, ["hl"] = "en" } },
                    ["continuation"] = cont,
                });
                var lcc = J(Json(Post(url, body)), "continuationContents", "liveChatContinuation");
                if (lcc == null) return; // yayın bitti
                // İlk cevap son mesajların geçmişi: ekrana basılmaz (eski komutlar yeniden çalışmasın).
                foreach (var a in (J(lcc, "actions") as object[]) ?? new object[0]) Parse(J(a, "addChatItemAction", "item"), !first);
                if (first) { first = false; connected(); }
                cont = NextContinuation(lcc, out int timeout);
                if (cont == "") return;
                // Sunucu 10 sn dese de 2,5 sn'de bir sormak kabul ediliyor; chat eğlencesi gecikmesin.
                cts.Token.WaitHandle.WaitOne(Math.Max(1000, Math.Min(2500, timeout)));
            }
        }

        static string NextContinuation(object o, out int timeoutMs)
        {
            timeoutMs = 2500;
            var c = (J(o, "continuations") as object[])?.FirstOrDefault() as Dictionary<string, object>;
            if (c == null) return "";
            foreach (var k in new[] { "invalidationContinuationData", "timedContinuationData", "reloadContinuationData" })
            {
                var d = J(c, k);
                if (d == null) continue;
                if (J(d, "timeoutMs") != null) timeoutMs = I(J(d, "timeoutMs"));
                return S(J(d, "continuation"));
            }
            return "";
        }

        void Parse(object item, bool emit)
        {
            try
            {
                var r = J(item, "liveChatTextMessageRenderer") ?? J(item, "liveChatPaidMessageRenderer");
                if (r == null) return;
                string id = S(J(r, "id"));
                if (id == "" || seen.Contains(id)) return;
                seen.Add(id); seenOrder.Enqueue(id);
                while (seenOrder.Count > 2000) seen.Remove(seenOrder.Dequeue());
                if (!emit) return;

                string text = Runs(J(r, "message", "runs"));
                string paid = S(J(r, "purchaseAmountText", "simpleText"));
                if (paid != "") text = ("💰 " + paid + " " + text).Trim(); // Super Chat
                var badges = ((J(r, "authorBadges") as object[]) ?? new object[0]).Select(b => J(b, "liveChatAuthorBadgeRenderer")).ToList();
                string user = S(J(r, "authorName", "simpleText")).TrimStart('@');
                var m = new ChatMessage
                {
                    User = user, Color = NameColor(user), Text = text, From = Platform.YouTube,
                    Broadcaster = badges.Any(b => S(J(b, "icon", "iconType")) == "OWNER"),
                    Mod = badges.Any(b => S(J(b, "icon", "iconType")) == "MODERATOR"),
                    Sub = badges.Any(b => J(b, "customThumbnail") != null), // kanal üyeliği rozeti
                };
                if (m.User != "" && m.Text != "") Message?.Invoke(m);
            }
            catch { }
        }

        /// <summary>Mesaj parçaları: yazı ve emoji (standart emoji karakter olarak, kanal emojisi kısa adıyla).</summary>
        static string Runs(object runs)
        {
            var sb = new StringBuilder();
            foreach (var run in (runs as object[]) ?? new object[0])
            {
                if (J(run, "text") is string t) { sb.Append(t); continue; }
                var e = J(run, "emoji");
                if (e == null) continue;
                if (J(e, "isCustomEmoji") is bool custom && custom) sb.Append(" " + S((J(e, "shortcuts") as object[])?.FirstOrDefault()) + " ");
                else sb.Append(S(J(e, "emojiId")));
            }
            return sb.ToString().Trim();
        }

        /// <summary>YouTube'da kullanıcı adı rengi yok: her ada sabit bir renk.</summary>
        static string NameColor(string user)
        {
            int h = 17;
            foreach (char ch in user) h = unchecked(h * 31 + ch);
            var p = FunLayer.Palette;
            return ColorTranslator.ToHtml(p[(h & 0x7fffffff) % p.Length]);
        }

        static object Json(string s) => new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 512 }.DeserializeObject(s);

        static string Between(string s, string a, string b)
        {
            int i = s.IndexOf(a, StringComparison.Ordinal);
            if (i < 0) return "";
            i += a.Length;
            int j = s.IndexOf(b, i, StringComparison.Ordinal);
            return j < 0 ? "" : s.Substring(i, j - i);
        }

        /// <summary>Sayfadaki "ytInitialData = {…}" nesnesi (dizelerdeki parantezleri sayma).</summary>
        static string ExtractObject(string html, string marker)
        {
            int i = html.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) throw new Exception(L.T("chat bulunamadı"));
            int start = html.IndexOf('{', i);
            int depth = 0; bool str = false, esc = false;
            for (int k = start; k >= 0 && k < html.Length; k++)
            {
                char c = html[k];
                if (str) { if (esc) esc = false; else if (c == '\\') esc = true; else if (c == '"') str = false; continue; }
                if (c == '"') str = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0) return html.Substring(start, k - start + 1);
            }
            throw new Exception(L.T("chat bulunamadı"));
        }

        static HttpWebRequest Request(string url)
        {
            var rq = (HttpWebRequest)WebRequest.Create(url);
            rq.UserAgent = UA; rq.Timeout = 15000;
            rq.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            rq.Headers["Accept-Language"] = "en-US,en;q=0.9";
            rq.Headers["Cookie"] = "CONSENT=YES+1; SOCS=CAI"; // AB çerez onayı sayfasına yönlenmesin
            return rq;
        }

        static string Get(string url)
        {
            var rq = Request(url);
            using (var r = rq.GetResponse()) return new StreamReader(r.GetResponseStream()).ReadToEnd();
        }

        static string Post(string url, string json)
        {
            var rq = Request(url);
            rq.Method = "POST"; rq.ContentType = "application/json";
            var b = Encoding.UTF8.GetBytes(json);
            using (var s = rq.GetRequestStream()) s.Write(b, 0, b.Length);
            using (var r = rq.GetResponse()) return new StreamReader(r.GetResponseStream()).ReadToEnd();
        }

        public void Dispose() => cts.Cancel();
    }
}
