using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ValoLive
{
    enum CaptureMode { FullScreen = 0, Valorant = 1, App = 2 }

    /// <summary>
    /// Yayında yalnızca seçilen uygulamanın görünmesini sağlar. Ekran yine tümüyle yakalanır (ddagrab hiç
    /// donmaz); ffmpeg grafiğindeki örtüler canlı komutla açılıp kapanır:
    /// - uygulama önde değilse (alt-tab, masaüstü, simge durumu) tüm ekran örtülür,
    /// - önde ama tam ekran değilse penceresinin dışı örtülür.
    /// Komut ~50-100 ms'de uygulanır; panel katmanındaki "birazdan dönüyorum" kartı ardından gelir.
    /// Oyuna dokunmaz: yalnızca ön plandaki pencerenin süreç adını ve konumunu okur.
    /// </summary>
    sealed class CaptureGuard : IDisposable
    {
        public const string ValorantExe = "VALORANT-Win64-Shipping";
        const string Fill = "0x0b0d12";

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, int cmd);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc cb, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr h);
        delegate bool EnumProc(IntPtr h, IntPtr p);
        delegate void WinEventProc(IntPtr hook, uint ev, IntPtr h, int obj, int child, uint thread, uint time);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

        readonly int W, H;
        readonly Timer timer = new Timer { Interval = 100 };
        readonly WinEventProc hookProc;
        readonly IntPtr hook;
        readonly Dictionary<int, string> names = new Dictionary<int, string>();

        public CaptureMode Mode = CaptureMode.FullScreen;
        public string AppName = "";      // süreç adı (.exe'siz)
        public Action<string> Send;      // ffmpeg'e canlı filtre komutu
        /// <summary>
        /// Yakalama yalnızca Valorant penceresini alıyor (hafif yakalama): üstteki başka pencereler görüntüye hiç girmez,
        /// bu yüzden Valorant önde olmasa da örtü gerekmez. Oyun simge durumuna küçülünce yakalama ekrana döner ve bu false olur.
        /// </summary>
        public Func<bool> WindowOnly = () => false;
        public event Action<bool> CoveredChanged;

        bool covered;
        Rectangle hole;                  // görünür bırakılan alan (ekran koordinatı)
        // ffmpeg'e son gönderilen değerler: yalnızca değişeni göndermek için (her komut ~50 ms sürer)
        bool sentCover;
        readonly int[][] sentBoxes = new int[4][];

        public bool Covered => covered;
        /// <summary>Son değerlendirmede öndeki uygulamanın süreç adı (tanılama günlüğü için).</summary>
        public string Foreground { get; private set; } = "";
        public string TargetName => Mode == CaptureMode.Valorant ? "Valorant" : AppName;

        public CaptureGuard(int screenW, int screenH)
        {
            W = screenW; H = screenH;
            hole = new Rectangle(0, 0, W, H);
            timer.Tick += (s, e) => Evaluate();
            timer.Start();
            // Ön plan değişimini anında yakala (zamanlayıcıyı beklemeden): alt-tab'ın ilk anı.
            hookProc = (h, ev, wnd, obj, child, thread, time) => Evaluate();
            hook = SetWinEventHook(3, 3, IntPtr.Zero, hookProc, 0, 0, 0); // EVENT_SYSTEM_FOREGROUND, WINEVENT_OUTOFCONTEXT
            Evaluate();
        }

        /// <summary>ffmpeg başlarken o anki duruma göre örtü filtreleri (sonraki değişiklikler komutla gelir).</summary>
        public string FilterChain()
        {
            lock (lk) return FilterChainCore();
        }

        // Yakalama yeniden başlarken FilterChain arka plan iş parçacığından gelir; durum tek kilitle korunur.
        readonly object lk = new object();

        string FilterChainCore()
        {
            Evaluate();
            sentCover = covered;
            var sb = new StringBuilder();
            var boxes = Boxes(hole);
            for (int i = 0; i < 4; i++)
            {
                var b = boxes[i];
                sentBoxes[i] = b;
                sb.Append($",drawbox@vlm{i}=x={b[0]}:y={b[1]}:w={b[2]}:h={b[3]}:color={Fill}:t=fill");
            }
            sb.Append($",drawbox@vlc=x=0:y=0:w=iw:h=ih:color={Fill}:t=fill:enable={(covered ? 1 : 0)}");
            return sb.ToString();
        }

        void Evaluate()
        {
            lock (lk) EvaluateCore();
        }

        void EvaluateCore()
        {
            bool cov; Rectangle h;
            if (Mode == CaptureMode.FullScreen || (Mode == CaptureMode.Valorant && WindowOnly())) { cov = false; h = new Rectangle(0, 0, W, H); }
            else
            {
                IntPtr fg = GetForegroundWindow();
                Foreground = fg == IntPtr.Zero ? "" : ProcessName(fg);
                cov = fg == IntPtr.Zero || IsIconic(fg) || !string.Equals(Foreground, Mode == CaptureMode.Valorant ? ValorantExe : AppName, StringComparison.OrdinalIgnoreCase);
                h = cov ? hole : Bounds(fg);
                if (h.Width <= 0 || h.Height <= 0) cov = true;
            }
            // Pencere sürüklenirken her konum için komut yollamak kuyruk biriktirir (her komut ~120 ms):
            // konum 400 ms sabit kalana dek ekran örtülü tutulur, delik sonra bir kez taşınır.
            if (h != seen) { seen = h; seenAt = DateTime.Now; }
            bool settling = !cov && h != hole && (DateTime.Now - seenAt).TotalMilliseconds < 400;
            Apply(cov, h, settling);
        }

        Rectangle seen;
        DateTime seenAt;

        void Apply(bool cov, Rectangle h, bool settling)
        {
            bool changed = cov != covered;
            covered = cov;
            if (!cov && !settling) hole = h;
            if (Send != null)
            {
                // Sıra önemli: önce örtü açılır, delik en son değişir, örtü en son kalkar → arada hiç sızıntı olmaz.
                if ((cov || settling) && !sentCover) { Send("drawbox@vlc enable 1"); sentCover = true; }
                if (!cov && !settling)
                {
                    var boxes = Boxes(hole);
                    bool moved = Enumerable.Range(0, 4).Any(i => sentBoxes[i] == null || !sentBoxes[i].SequenceEqual(boxes[i]));
                    if (moved && !sentCover) { Send("drawbox@vlc enable 1"); sentCover = true; }
                    for (int i = 0; i < 4; i++)
                    {
                        if (sentBoxes[i] != null && sentBoxes[i].SequenceEqual(boxes[i])) continue;
                        string[] keys = { "x", "y", "w", "h" };
                        for (int k = 0; k < 4; k++) Send($"drawbox@vlm{i} {keys[k]} {boxes[i][k]}");
                        sentBoxes[i] = boxes[i];
                    }
                    if (sentCover) { Send("drawbox@vlc enable 0"); sentCover = false; }
                }
            }
            if (changed) CoveredChanged?.Invoke(cov);
        }

        /// <summary>Deliğin dışını kaplayan 4 kutu (üst, alt, sol, sağ). Boş kutu ekran dışına konur.</summary>
        int[][] Boxes(Rectangle r)
        {
            r = Rectangle.Intersect(r, new Rectangle(0, 0, W, H));
            int[] Box(int x, int y, int w, int h) => w > 0 && h > 0 ? new[] { x, y, w, h } : new[] { -9, -9, 1, 1 };
            return new[]
            {
                Box(0, 0, W, r.Top),
                Box(0, r.Bottom, W, H - r.Bottom),
                Box(0, r.Top, r.Left, r.Height),
                Box(r.Right, r.Top, W - r.Right, r.Height),
            };
        }

        static Rectangle Bounds(IntPtr h)
        {
            // DWM sınırları DPI ölçeğinden bağımsız gerçek pikseldir (ddagrab ile aynı uzay).
            if (DwmGetWindowAttribute(h, 9, out var r, Marshal.SizeOf(typeof(RECT))) != 0) return Rectangle.Empty;
            return Rectangle.FromLTRB(r.L, r.T, r.R, r.B);
        }

        string ProcessName(IntPtr h)
        {
            GetWindowThreadProcessId(h, out int pid);
            if (names.TryGetValue(pid, out var n)) return n;
            try { n = Process.GetProcessById(pid).ProcessName; } catch { n = ""; }
            if (names.Count > 200) names.Clear();
            return names[pid] = n;
        }

        /// <summary>Görünür penceresi olan uygulamalar: (süreç adı, "ad — pencere başlığı").</summary>
        public static List<KeyValuePair<string, string>> ListApps()
        {
            var self = Process.GetCurrentProcess().Id;
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            EnumWindows((h, p) =>
            {
                if (!IsWindowVisible(h) || GetWindow(h, 4) != IntPtr.Zero) return true; // GW_OWNER: açılır pencereleri atla
                var sb = new StringBuilder(256);
                if (GetWindowText(h, sb, sb.Capacity) == 0) return true;
                GetWindowThreadProcessId(h, out int pid);
                if (pid == self) return true;
                string name;
                try { name = Process.GetProcessById(pid).ProcessName; } catch { return true; }
                if (name == "explorer" || name == "TextInputHost" || name == "ApplicationFrameHost") return true;
                if (!seen.ContainsKey(name)) seen[name] = $"{name} — {sb}";
                return true;
            }, IntPtr.Zero);
            return seen.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public void Dispose()
        {
            timer.Dispose();
            if (hook != IntPtr.Zero) UnhookWinEvent(hook);
        }
    }
}
