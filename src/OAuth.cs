using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ValoLive
{
    /// <summary>
    /// Masaüstü OAuth girişinin ortak parçaları (Kick, YouTube): PKCE, tarayıcının localhost'a dönüşünü bekleyen
    /// küçük dinleyici. Tokenlar burada tutulmaz; her API kendi ayarına DPAPI ile şifreli yazar.
    /// </summary>
    static class OAuth
    {
        public static string Verifier() => B64(RandomBytes(48));

        public static string Challenge(string verifier)
        {
            using (var sha = SHA256.Create()) return B64(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
        }

        public static string State() => B64(RandomBytes(16));

        /// <summary>Verilen portu dinlemeye başlar (localhost tarayıcıda ::1 ya da 127.0.0.1'e gidebilir).</summary>
        public static List<TcpListener> Listen(int port, bool ipv6)
        {
            var listeners = new List<TcpListener>();
            foreach (var ip in ipv6 ? new[] { IPAddress.Loopback, IPAddress.IPv6Loopback } : new[] { IPAddress.Loopback })
            {
                try { var l = new TcpListener(ip, port); l.Start(); listeners.Add(l); } catch { }
            }
            if (listeners.Count == 0) throw new Exception(L.F("{0} portu kullanımda; ValoLive'ı yeniden başlatıp tekrar dene.", port));
            return listeners;
        }

        /// <summary>Onay sayfasının dönüşünü bekler; kodu döner. okHtml tarayıcıda gösterilir.</summary>
        public static string WaitForCode(List<TcpListener> listeners, string state, TimeSpan timeout, string okHtml, string deniedPrefix, string timeoutMsg)
        {
            var end = DateTime.Now + timeout;
            while (DateTime.Now < end)
            {
                var l = listeners.FirstOrDefault(x => x.Pending());
                if (l == null) { Thread.Sleep(100); continue; }
                using (var c = l.AcceptTcpClient())
                using (var ns = c.GetStream())
                {
                    ns.ReadTimeout = 5000;
                    var buf = new byte[8192];
                    int n = ns.Read(buf, 0, buf.Length);
                    string req = Encoding.ASCII.GetString(buf, 0, n);
                    string target = req.Split(' ').ElementAtOrDefault(1) ?? "";
                    var q = ParseQuery(target);
                    bool ok = q.TryGetValue("code", out var code) && q.TryGetValue("state", out var st) && st == state;
                    string err = q.TryGetValue("error", out var e) ? e : null;
                    if (!ok && err == null && !target.Contains("?")) { Respond(ns, 404, ""); continue; } // favicon vb.
                    string html = ok ? okHtml : $"<h2>{L.T("Bağlanılamadı")}</h2><p>{WebUtility.HtmlEncode(err ?? L.T("geçersiz yanıt"))}</p>";
                    Respond(ns, 200, $"<!doctype html><meta charset=utf-8><body style=\"font-family:Segoe UI;background:#0b0d12;color:#e8eaf0;padding:40px\">{html}</body>");
                    if (ok) return code;
                    if (err != null) throw new Exception(deniedPrefix + err);
                }
            }
            throw new Exception(timeoutMsg);
        }

        static void Respond(NetworkStream ns, int status, string html)
        {
            var body = Encoding.UTF8.GetBytes(html);
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            ns.Write(head, 0, head.Length); ns.Write(body, 0, body.Length);
        }

        public static void Stop(List<TcpListener> listeners) { foreach (var l in listeners) try { l.Stop(); } catch { } }

        static Dictionary<string, string> ParseQuery(string target)
        {
            var d = new Dictionary<string, string>();
            int i = target.IndexOf('?');
            if (i < 0) return d;
            foreach (var kv in target.Substring(i + 1).Split('&'))
            {
                int eq = kv.IndexOf('=');
                if (eq > 0) d[Uri.UnescapeDataString(kv.Substring(0, eq))] = Uri.UnescapeDataString(kv.Substring(eq + 1).Replace('+', ' '));
            }
            return d;
        }

        static byte[] RandomBytes(int n) { var b = new byte[n]; using (var r = RandomNumberGenerator.Create()) r.GetBytes(b); return b; }
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
