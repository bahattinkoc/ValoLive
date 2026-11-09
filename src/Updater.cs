using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using static ValoLive.ValorantTracker;

namespace ValoLive
{
    /// <summary>
    /// GitHub'daki son sürümü denetler ve uygulamayı yerinde günceller. Windows çalışan exe'nin silinmesine değil
    /// adının değişmesine izin verir: değişen dosyalar .valolive-old olarak kenara alınır, yenileri yerine kopyalanır,
    /// uygulama yeniden başlar; kenardakiler bir sonraki açılışta silinir. Yarıda kalırsa eskiler geri konur.
    /// </summary>
    static class Updater
    {
        const string Api = "https://api.github.com/repos/bahattinkoc/ValoLive/releases/latest";
        const string OldExt = ".valolive-old";

        public sealed class Release { public Version Version; public string Tag, ZipUrl, Page; }

        public static Version Current => Normal(Assembly.GetExecutingAssembly().GetName().Version);
        static string Dir => Path.GetDirectoryName(Application.ExecutablePath);

        /// <summary>"1.4.1" ile "1.4.1.0" eşit sayılsın: eksik parçalar 0.</summary>
        static Version Normal(Version v) => new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

        /// <summary>Daha yeni bir sürüm varsa onu, yoksa null döner. Ağ hatası fırlatır.</summary>
        public static Release Check()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(Api);
            req.UserAgent = "ValoLive/" + Current; // GitHub API kullanıcı aracısı olmayan isteği reddeder
            req.Accept = "application/vnd.github+json";
            req.Timeout = 15000;
            string json;
            using (var r = req.GetResponse())
            using (var s = new StreamReader(r.GetResponseStream())) json = s.ReadToEnd();
            var o = new JavaScriptSerializer().DeserializeObject(json);
            string tag = S(J(o, "tag_name"));
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v) || Normal(v) <= Current) return null;
            var zip = ((J(o, "assets") as object[]) ?? new object[0])
                .Select(a => S(J(a, "browser_download_url")))
                .FirstOrDefault(u => u.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            string page = S(J(o, "html_url"));
            return new Release { Version = Normal(v), Tag = tag, ZipUrl = zip, Page = page != "" ? page : "https://github.com/bahattinkoc/ValoLive/releases/latest" };
        }

        /// <summary>Uygulama klasörüne yazılabiliyor mu (Program Files'a çıkarıldıysa yazılamaz: o zaman sayfa açılır).</summary>
        public static bool CanWrite()
        {
            try
            {
                string probe = Path.Combine(Dir, ".valolive-write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Arka plan iş parçacığından: indirir (ilerleme 0-100), açar ve dosyaları değiştirir.</summary>
        public static void Install(Release r, Action<int> progress)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "valolive-update-" + r.Tag);
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            string zip = Path.Combine(tmp, "update.zip");
            Download(r.ZipUrl, zip, progress);

            string ex = Path.Combine(tmp, "x");
            ZipFile.ExtractToDirectory(zip, ex);
            string src = Path.Combine(ex, "ValoLive"); // sürüm zip'i: ValoLive\ValoLive.exe, valocap.exe, fonts\…
            if (!File.Exists(Path.Combine(src, "ValoLive.exe"))) throw new InvalidDataException(L.T("İndirilen pakette ValoLive.exe yok"));

            var moved = new List<string>();
            var copied = new List<string>();
            try
            {
                foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
                {
                    string dst = Path.Combine(Dir, f.Substring(src.Length + 1));
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    if (File.Exists(dst))
                    {
                        if (Same(f, dst)) continue; // yazı tipleri açıkken kilitli: değişmediyse dokunma
                        string old = dst + OldExt;
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(dst, old);
                        moved.Add(dst);
                    }
                    File.Copy(f, dst);
                    copied.Add(dst);
                }
            }
            catch
            {
                foreach (var c in copied) try { File.Delete(c); } catch { }
                foreach (var m in moved) try { File.Move(m + OldExt, m); } catch { }
                throw;
            }
            try { Directory.Delete(tmp, true); } catch { }
        }

        static void Download(string url, string path, Action<int> progress)
        {
            var req = (HttpWebRequest)WebRequest.Create(url); // GitHub indirme bağlantısı yönlendirir; izlenir
            req.UserAgent = "ValoLive/" + Current;
            req.Timeout = 30000;
            using (var r = req.GetResponse())
            using (var s = r.GetResponseStream())
            using (var f = File.Create(path))
            {
                long total = r.ContentLength, got = 0;
                var buf = new byte[81920];
                int n, last = -1;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    f.Write(buf, 0, n);
                    got += n;
                    int pct = total > 0 ? (int)(got * 100 / total) : 0;
                    if (pct != last) { last = pct; progress?.Invoke(pct); }
                }
            }
        }

        static bool Same(string a, string b)
        {
            var fa = new FileInfo(a); var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            try { return File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b)); }
            catch { return false; }
        }

        /// <summary>Açılışta: önceki güncellemeden kalan dosyalar (yalnızca uygulama klasörü ve fonts).</summary>
        public static void Cleanup()
        {
            foreach (var d in new[] { Dir, Path.Combine(Dir, "fonts") })
            {
                if (!Directory.Exists(d)) continue;
                try { foreach (var f in Directory.GetFiles(d, "*" + OldExt)) try { File.Delete(f); } catch { } } catch { }
            }
        }
    }
}
