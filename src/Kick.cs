using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using static ValoLive.ValorantTracker;

namespace ValoLive
{
    /// <summary>
    /// Kick resmi API'si (OAuth 2.1 + PKCE): yayın başlığı, kategori, etiketler.
    /// Kullanıcı kick.com/settings/developer'da bir uygulama açar; Client ID/Secret burada kullanılır.
    /// Tokenlar DPAPI ile şifreli saklanır (Settings), hiçbir yerde yazdırılmaz.
    /// </summary>
    sealed class KickApi
    {
        public const int RedirectPort = 53682;
        public static string RedirectUri => $"http://localhost:{RedirectPort}/callback";
        const string Scopes = "user:read channel:read channel:write chat:write";
        const string Id = "https://id.kick.com", Api = "https://api.kick.com/public";

        readonly Settings cfg;
        string access;
        DateTime accessExp;

        public KickApi(Settings cfg) { this.cfg = cfg; ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }

        public bool HasApp => Settings.Unprotect(cfg.KickClientIdEnc) != "" && Settings.Unprotect(cfg.KickSecretEnc) != "";
        public bool Connected => Settings.Unprotect(cfg.KickRefreshEnc) != "";

        public sealed class Channel { public string Slug, Title, CategoryName; public int CategoryId, BroadcasterId; public string[] Tags = new string[0]; public bool Live; }
        public sealed class Category { public int Id; public string Name; public override string ToString() => Name; }

        // ---------- Giriş (tarayıcıda onay → localhost'a dönüş) ----------

        /// <summary>Tarayıcıyı açar, kullanıcının onayını bekler (en fazla 3 dk), tokenları kaydeder.</summary>
        public Channel Login()
        {
            string clientId = Settings.Unprotect(cfg.KickClientIdEnc);
            string verifier = OAuth.Verifier(), state = OAuth.State();
            var listeners = OAuth.Listen(RedirectPort, true);
            try
            {
                Process.Start($"{Id}/oauth/authorize?response_type=code&client_id={Uri.EscapeDataString(clientId)}" +
                              $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope={Uri.EscapeDataString(Scopes)}" +
                              $"&code_challenge={OAuth.Challenge(verifier)}&code_challenge_method=S256&state={state}");
                string code = OAuth.WaitForCode(listeners, state, TimeSpan.FromMinutes(3), L.T("<h2>ValoLive Kick'e bağlandı ✓</h2><p>Bu sekmeyi kapatabilirsin.</p>"),
                                                L.T("Kick onayı reddedildi: "), L.T("Kick onayı 3 dakika içinde gelmedi."));
                var tok = PostForm($"{Id}/oauth/token", new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code", ["code"] = code, ["client_id"] = clientId,
                    ["client_secret"] = Settings.Unprotect(cfg.KickSecretEnc), ["redirect_uri"] = RedirectUri, ["code_verifier"] = verifier,
                });
                SaveTokens(tok);
            }
            finally { OAuth.Stop(listeners); }
            return GetChannel();
        }

        public void Logout()
        {
            cfg.KickRefreshEnc = ""; cfg.KickSlug = ""; access = null;
            cfg.Save();
        }

        void SaveTokens(object tok)
        {
            access = S(J(tok, "access_token"));
            accessExp = DateTime.Now.AddSeconds(Math.Max(60, I(J(tok, "expires_in")) - 60));
            var refresh = S(J(tok, "refresh_token"));
            if (refresh != "") cfg.KickRefreshEnc = Settings.Protect(refresh);
            cfg.Save();
        }

        string Token()
        {
            if (access != null && DateTime.Now < accessExp) return access;
            string refresh = Settings.Unprotect(cfg.KickRefreshEnc);
            if (refresh == "") throw new Exception(L.T("Kick hesabı bağlı değil."));
            try
            {
                SaveTokens(PostForm($"{Id}/oauth/token", new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token", ["refresh_token"] = refresh,
                    ["client_id"] = Settings.Unprotect(cfg.KickClientIdEnc), ["client_secret"] = Settings.Unprotect(cfg.KickSecretEnc),
                }));
            }
            catch (KickError e) when (e.Status == 400 || e.Status == 401)
            {
                Logout();
                throw new Exception(L.T("Kick oturumu sona ermiş, yeniden bağlan."));
            }
            return access;
        }

        // ---------- Kanal ----------

        public Channel GetChannel()
        {
            var d = (J(Call("GET", "/v1/channels", null), "data") as object[])?.FirstOrDefault();
            if (d == null) throw new Exception(L.T("Kick kanal bilgisi alınamadı."));
            var c = new Channel
            {
                Slug = S(J(d, "slug")), Title = S(J(d, "stream_title")),
                CategoryId = I(J(d, "category", "id")), CategoryName = S(J(d, "category", "name")),
                Tags = (J(d, "stream", "custom_tags") as object[])?.Select(S).Where(t => t != "").ToArray() ?? new string[0],
                Live = J(d, "stream", "is_live") is bool b && b,
                BroadcasterId = I(J(d, "broadcaster_user_id")),
            };
            if (c.Slug != "" && c.Slug != cfg.KickSlug) { cfg.KickSlug = c.Slug; cfg.Save(); }
            return c;
        }

        public void UpdateChannel(string title, int categoryId, string[] tags)
        {
            var body = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(title)) body["stream_title"] = title.Trim();
            if (categoryId > 0) body["category_id"] = categoryId;
            if (tags != null) body["custom_tags"] = tags.Take(10).ToArray();
            if (body.Count > 0) Call("PATCH", "/v1/channels", body);
        }

        int broadcasterId;

        /// <summary>Kendi kanalının chat'ine mesaj (yayıncı hesabınla). Kick sınırı 500 karakter.</summary>
        public void SendChat(string text)
        {
            if (broadcasterId == 0) broadcasterId = GetChannel().BroadcasterId;
            if (text.Length > 480) text = text.Substring(0, 477) + "…";
            Call("POST", "/v1/chat", new Dictionary<string, object> { ["broadcaster_user_id"] = broadcasterId, ["content"] = text, ["type"] = "user" });
        }

        public List<Category> SearchCategories(string name)
        {
            var r = Call("GET", "/v2/categories?limit=25&name=" + Uri.EscapeDataString(name.Trim()), null);
            return ((J(r, "data") as object[]) ?? new object[0])
                .Select(x => new Category { Id = I(J(x, "id")), Name = S(J(x, "name")) })
                .Where(c => c.Id > 0).ToList();
        }

        // ---------- HTTP ----------

        sealed class KickError : Exception
        {
            public readonly int Status;
            public KickError(int status, string msg) : base(msg) { Status = status; }
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
            catch (KickError e) when (e.Status == 401 && !retried) { access = null; return Call(method, path, body, true); }
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
                try { var j = new JavaScriptSerializer().DeserializeObject(txt); msg = S(J(j, "message")); if (msg == "") msg = S(J(j, "error_description")); if (msg == "") msg = S(J(j, "error")); } catch { }
                throw new KickError((int)r.StatusCode, $"Kick {(int)r.StatusCode}: {(msg != "" ? msg : r.StatusDescription)}");
            }
        }
    }

    sealed class ChatMessage
    {
        public string User, Color, Text;
        public bool Sub, Mod, Broadcaster;
        public Platform From = Platform.Kick;   // mesajın geldiği chat (bot cevabı oraya gider)
        public DateTime At = DateTime.Now;
    }

    /// <summary>
    /// Kick chat'ini salt okunur dinler (Kick sitesinin kullandığı misafir gerçek zamanlı bağlantı,
    /// Centrifugo). Resmi API'de chat okuma yalnızca internetten erişilebilen webhook ile mümkün; bu yol
    /// resmi değil, Kick değiştirirse bozulabilir: o durumda yalnızca chat kutusu boş kalır.
    /// </summary>
    sealed class KickChat : IDisposable
    {
        const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";
        readonly string slug;
        readonly CancellationTokenSource cts = new CancellationTokenSource();
        public event Action<ChatMessage> Message;
        public event Action<string> Status;

        public KickChat(string slug)
        {
            this.slug = slug;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            new Thread(Run) { IsBackground = true, Name = "kick-chat" }.Start();
        }

        void Run()
        {
            int fails = 0;
            long chatroom = 0;
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    if (chatroom == 0) chatroom = ChatroomId();
                    Status?.Invoke(L.T("bağlanıyor"));
                    Listen(chatroom, () => { fails = 0; Status?.Invoke(L.T("bağlı")); });
                }
                catch (Exception e)
                {
                    if (cts.IsCancellationRequested) return;
                    fails++;
                    Status?.Invoke(L.T("koptu: ") + e.Message);
                    Diag.Event("CHAT", "chat bağlantısı: " + e.Message);
                }
                // Bağlantı süresi (~45 dk) dolunca da buraya gelinir: hemen yeniden bağlan; hatada kademeli bekle.
                cts.Token.WaitHandle.WaitOne(fails == 0 ? 200 : Math.Min(30000, 1000 * (1 << Math.Min(5, fails))));
            }
        }

        long ChatroomId()
        {
            var j = new JavaScriptSerializer().DeserializeObject(Get($"https://kick.com/api/v2/channels/{Uri.EscapeDataString(slug)}"));
            long id = Convert.ToInt64(J(j, "chatroom", "id") ?? 0);
            if (id == 0) throw new Exception(L.T("chat odası bulunamadı"));
            return id;
        }

        void Listen(long chatroom, Action connected)
        {
            string cid = "valolive-" + Guid.NewGuid().ToString("N").Substring(0, 16);
            var conn = new JavaScriptSerializer().DeserializeObject(Post($"https://web.kick.com/api/v1/realtime/channels/{chatroom}/chat/connection",
                "{\"client\":{\"id\":\"" + cid + "\",\"type\":\"web\"},\"capabilities\":{\"accepted_providers\":[{\"provider\":\"centrifugo\"}]}}"));
            string url = ((J(conn, "data", "connections") as object[]) ?? new object[0])
                .Where(c => S(J(c, "provider")) == "centrifugo").Select(c => S(J(c, "credentials", "url"))).FirstOrDefault();
            if (string.IsNullOrEmpty(url)) throw new Exception(L.T("gerçek zamanlı sunucu bilgisi yok"));
            string token = S(J(new JavaScriptSerializer().DeserializeObject(Post("https://web.kick.com/api/v1/realtime/auth/connection", "{\"client_id\":\"" + cid + "\"}")), "data", "token"));
            if (token == "") throw new Exception(L.T("misafir erişimi alınamadı"));

            using (var ws = new ClientWebSocket())
            {
                ws.Options.SetRequestHeader("Origin", "https://kick.com");
                ws.ConnectAsync(new Uri(url), cts.Token).Wait();
                Send(ws, "{\"id\":1,\"connect\":{\"token\":\"" + token + "\",\"name\":\"js\"}}");
                Send(ws, "{\"id\":2,\"subscribe\":{\"channel\":\"chatrooms." + chatroom + ".v2\"}}");
                var buf = new byte[1 << 16];
                var msg = new MemoryStream();
                bool announced = false;
                while (!cts.IsCancellationRequested)
                {
                    // Sunucu ~25 sn'de bir ping atar; 60 sn ses yoksa bağlantı ölmüştür.
                    var t = ws.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token);
                    if (!t.Wait(60000)) throw new Exception(L.T("sunucu sessiz kaldı"));
                    var r = t.Result;
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    msg.Write(buf, 0, r.Count);
                    if (!r.EndOfMessage) continue;
                    string text = Encoding.UTF8.GetString(msg.ToArray());
                    msg.SetLength(0);
                    foreach (var line in text.Split('\n'))
                    {
                        if (line == "{}") { Send(ws, "{}"); continue; } // ping → pong
                        if (!announced && line.Contains("\"id\":2")) { announced = true; connected(); }
                        if (line.Contains("ChatMessageEvent")) Parse(line);
                    }
                }
            }
        }

        void Parse(string line)
        {
            try
            {
                var js = new JavaScriptSerializer();
                var data = J(js.DeserializeObject(line), "push", "pub", "data");
                var inner = J(data, "data");
                if (inner is string s) inner = js.DeserializeObject(s);
                var badges = (J(inner, "sender", "identity", "badges") as object[]) ?? new object[0];
                bool Has(string type) => badges.Any(b => S(J(b, "type")) == type);
                var m = new ChatMessage
                {
                    User = S(J(inner, "sender", "username")),
                    Color = S(J(inner, "sender", "identity", "color")),
                    Text = S(J(inner, "content")),
                    Sub = Has("subscriber") || Has("founder"), Mod = Has("moderator"), Broadcaster = Has("broadcaster"),
                };
                if (m.User != "" && m.Text != "") Message?.Invoke(m);
            }
            catch { }
        }

        void Send(ClientWebSocket ws, string s) =>
            ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(s)), WebSocketMessageType.Text, true, cts.Token).Wait();

        static string Get(string url)
        {
            var rq = (HttpWebRequest)WebRequest.Create(url);
            rq.UserAgent = UA; rq.Accept = "application/json"; rq.Timeout = 15000;
            using (var r = rq.GetResponse()) return new StreamReader(r.GetResponseStream()).ReadToEnd();
        }

        static string Post(string url, string json)
        {
            var rq = (HttpWebRequest)WebRequest.Create(url);
            rq.Method = "POST"; rq.UserAgent = UA; rq.Accept = "application/json"; rq.ContentType = "application/json"; rq.Timeout = 15000;
            var b = Encoding.UTF8.GetBytes(json);
            using (var s = rq.GetRequestStream()) s.Write(b, 0, b.Length);
            using (var r = rq.GetResponse()) return new StreamReader(r.GetResponseStream()).ReadToEnd();
        }

        public void Dispose() => cts.Cancel();
    }
}
