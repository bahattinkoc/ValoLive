// valocap: ValoLive'ın ekran kartı motoru. Yakalama → birleştirme → NV12 → NVENC → MPEG-TS; hiçbir kare işlemciye inmez.
//
// valocap.exe --out <dosya | \\.\pipe\ad> --epoch <unix ms> [--size 1920x1080] [--fps 60] [--bitrate 6000]
//             [--window <hwnd> | --monitor <n>] [--fill RRGGBB] [--panel <ad>] [--layer <ad>,x,y,w,h,up]...
//
// stdin komutları: layer <i> <0|1> · panel <0|1> · mask <i> <x> <y> <w> <h> · cover <0|1> · flip <0|1> · zoom <0|1>
//                  source window <hwnd> · source monitor <n> · quit
// stdout: "ready", "stats ..." (5 sn'de bir), "error ..." satırları.
#include <windows.h>
#include <d3d11.h>
#include <d3d10.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <winrt/base.h>
#include <algorithm>
#include <atomic>
#include <condition_variable>
#include <cstdio>
#include <deque>
#include <mutex>
#include <string>
#include <thread>
#include <vector>
#include "capture.h"
#include "nvenc.h"
#include "render.h"
#include "ts.h"

using Microsoft::WRL::ComPtr;

static std::mutex outLock;
static void Say(const std::string& s) { std::lock_guard<std::mutex> g(outLock); fputs((s + "\n").c_str(), stdout); fflush(stdout); }

static std::wstring Wide(const std::string& s)
{
    int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, nullptr, 0);
    std::wstring w(n ? n - 1 : 0, L'\0');
    if (n) MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, &w[0], n);
    return w;
}

static int64_t UnixMs()
{
    FILETIME ft; GetSystemTimePreciseAsFileTime(&ft);
    return (int64_t)((((uint64_t)ft.dwHighDateTime << 32) | ft.dwLowDateTime) / 10000 - 11644473600000ULL);
}

// Çıkış ayrı iş parçacığında yazılır: ffmpeg bir an yavaşlasa da çizim döngüsü beklemez.
class Writer
{
public:
    bool Open(const std::string& path, std::string& error)
    {
        std::wstring w = Wide(path);
        if (path.rfind("\\\\.\\pipe\\", 0) == 0)
        {
            h = CreateNamedPipeW(w.c_str(), PIPE_ACCESS_OUTBOUND, PIPE_TYPE_BYTE | PIPE_WAIT, 1, 1 << 20, 0, 0, nullptr);
            if (h == INVALID_HANDLE_VALUE) { error = "pipe oluşturulamadı"; return false; }
            pipe = true;
        }
        else
        {
            h = CreateFileW(w.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, 0, nullptr);
            if (h == INVALID_HANDLE_VALUE) { error = "çıkış dosyası açılamadı"; return false; }
        }
        thread = std::thread([this] { Run(); });
        return true;
    }
    void Push(std::vector<uint8_t>&& data)
    {
        std::lock_guard<std::mutex> g(m);
        queued += data.size();
        q.push_back(std::move(data));
        cv.notify_one();
    }
    void Close()
    {
        { std::lock_guard<std::mutex> g(m); done = true; cv.notify_one(); }
        if (thread.joinable()) thread.join();
        if (h != INVALID_HANDLE_VALUE) { if (pipe) FlushFileBuffers(h); CloseHandle(h); h = INVALID_HANDLE_VALUE; }
    }
    size_t Queued() { std::lock_guard<std::mutex> g(m); return queued; }
    std::atomic<bool> failed{ false };

private:
    HANDLE h = INVALID_HANDLE_VALUE;
    bool pipe = false, done = false;
    std::thread thread;
    std::mutex m;
    std::condition_variable cv;
    std::deque<std::vector<uint8_t>> q;
    size_t queued = 0;

    void Run()
    {
        if (pipe && !ConnectNamedPipe(h, nullptr) && GetLastError() != ERROR_PIPE_CONNECTED) { failed = true; return; }
        for (;;)
        {
            std::vector<uint8_t> d;
            {
                std::unique_lock<std::mutex> g(m);
                cv.wait(g, [this] { return done || !q.empty(); });
                if (q.empty()) return;
                d = std::move(q.front()); q.pop_front(); queued -= d.size();
            }
            DWORD n;
            if (!WriteFile(h, d.data(), (DWORD)d.size(), &n, nullptr)) { failed = true; return; }
        }
    }
};

struct Args
{
    std::string out;
    int64_t epoch = 0;
    int width = 1920, height = 1080, fps = 60, bitrate = 6000;
    HWND window = nullptr;
    int monitor = -1;
    float fill[4] = { 0.07f, 0.07f, 0.09f, 1 };
    bool hasPanel = false;
    LayerSpec panel;
    std::vector<LayerSpec> layers;
};

static bool Parse(int argc, char** argv, Args& a)
{
    for (int i = 1; i < argc; i++)
    {
        std::string k = argv[i], v = i + 1 < argc ? argv[i + 1] : "";
        if (k == "--out") { a.out = v; i++; }
        else if (k == "--epoch") { a.epoch = _strtoi64(v.c_str(), nullptr, 10); i++; }
        else if (k == "--size") { sscanf_s(v.c_str(), "%dx%d", &a.width, &a.height); i++; }
        else if (k == "--fps") { a.fps = atoi(v.c_str()); i++; }
        else if (k == "--bitrate") { a.bitrate = atoi(v.c_str()); i++; }
        else if (k == "--window") { a.window = (HWND)(intptr_t)_strtoi64(v.c_str(), nullptr, 10); i++; }
        else if (k == "--monitor") { a.monitor = atoi(v.c_str()); i++; }
        else if (k == "--fill")
        {
            unsigned c = strtoul(v.c_str(), nullptr, 16);
            a.fill[0] = ((c >> 16) & 255) / 255.f; a.fill[1] = ((c >> 8) & 255) / 255.f; a.fill[2] = (c & 255) / 255.f; i++;
        }
        else if (k == "--panel") { a.hasPanel = true; a.panel.name = Wide(v); a.panel.w = -1; i++; }
        else if (k == "--layer")
        {
            char name[256] = {}; LayerSpec l;
            if (sscanf_s(v.c_str(), "%255[^,],%d,%d,%d,%d,%d", name, (unsigned)sizeof name, &l.x, &l.y, &l.w, &l.h, &l.up) != 6) return false;
            l.name = Wide(name);
            a.layers.push_back(l); i++;
        }
        else return false;
    }
    if (a.hasPanel) { a.panel.w = a.width; a.panel.h = a.height; }
    return !a.out.empty() && a.layers.size() <= 32;
}

static ComPtr<ID3D11Device> MakeDevice(ComPtr<ID3D11DeviceContext>& ctx)
{
    // NVENC NVIDIA aygıtında çalışır: varsa NVIDIA bağdaştırıcısı seçilir (dizüstünde dahili ekran kartı yerine).
    ComPtr<IDXGIFactory1> f;
    CreateDXGIFactory1(IID_PPV_ARGS(&f));
    ComPtr<IDXGIAdapter1> pick, a;
    for (UINT i = 0; f && f->EnumAdapters1(i, &a) != DXGI_ERROR_NOT_FOUND; i++, a.Reset())
    {
        DXGI_ADAPTER_DESC1 d; a->GetDesc1(&d);
        if (d.VendorId == 0x10DE) { pick = a; break; }
    }
    ComPtr<ID3D11Device> dev;
    D3D_FEATURE_LEVEL fl[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
    if (FAILED(D3D11CreateDevice(pick.Get(), pick ? D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE_HARDWARE, nullptr,
                                 D3D11_CREATE_DEVICE_BGRA_SUPPORT, fl, 2, D3D11_SDK_VERSION, &dev, nullptr, &ctx)))
        return nullptr;
    // WGC kare havuzu aygıtı kendi iş parçacığından da kullanır.
    ComPtr<ID3D10Multithread> mt;
    if (SUCCEEDED(ctx.As(&mt))) mt->SetMultithreadProtected(TRUE);
    return dev;
}

int main(int argc, char** argv)
{
    Args a;
    if (!Parse(argc, argv, a)) { Say("error kullanım: valocap --out <yol> --epoch <ms> ..."); return 2; }
    winrt::init_apartment(winrt::apartment_type::multi_threaded);
    SetPriorityClass(GetCurrentProcess(), ABOVE_NORMAL_PRIORITY_CLASS);   // iş çok küçük; zamanında yapılsın

    ComPtr<ID3D11DeviceContext> ctx;
    auto dev = MakeDevice(ctx);
    if (!dev) { Say("error D3D11 aygıtı oluşturulamadı"); return 3; }
    // Oyun ekran kartını doldursa da kareler gecikmesin (Windows izin verdiği ölçüde).
    ComPtr<IDXGIDevice> dxgi;
    if (SUCCEEDED(dev.As(&dxgi))) dxgi->SetGPUThreadPriority(7);

    std::string err;
    Capture cap;
    Renderer ren;
    NvEncoder enc;
    NvEncoder::Options eo;
    eo.width = a.width; eo.height = a.height; eo.fps = a.fps; eo.bitrateK = a.bitrate; eo.gop = a.fps * 2;
    if (!cap.Init(dev.Get(), err) || !ren.Init(dev.Get(), a.width, a.height, a.hasPanel ? &a.panel : nullptr, a.layers, err) ||
        !enc.Open(dev.Get(), eo, err))
    { Say("error " + err); return 4; }
    memcpy(ren.state.fill, a.fill, sizeof a.fill);
    if (a.window && !cap.StartWindow(a.window, err)) Say("error " + err);
    else if (!a.window && a.monitor >= 0 && !cap.StartMonitor(a.monitor, err)) Say("error " + err);

    Writer out;
    if (!out.Open(a.out, err)) { Say("error " + err); return 5; }

    std::atomic<bool> quit{ false };
    std::mutex srcLock;
    std::string pendingSource;   // komut iş parçacığı yazar, çizim döngüsü uygular (WGC nesneleri tek yerden)
    std::thread input([&] {
        char line[512];
        while (!quit && fgets(line, sizeof line, stdin))
        {
            if ((uint8_t)line[0] == 0xEF && (uint8_t)line[1] == 0xBB && (uint8_t)line[2] == 0xBF) memmove(line, line + 3, strlen(line) - 2);   // .NET UTF-8 BOM
            char cmd[32] = {}; int i = 0, v = 0, x = 0, y = 0, w = 0, h = 0;
            sscanf_s(line, "%31s", cmd, (unsigned)sizeof cmd);
            std::string c = cmd;
            auto& st = ren.state;
            if (c == "quit") break;
            else if (c == "layer" && sscanf_s(line, "%*s %d %d", &i, &v) == 2 && i >= 0 && i < 32) st.layer[i] = v != 0;
            else if (c == "panel" && sscanf_s(line, "%*s %d", &v) == 1) st.panel = v != 0;
            else if (c == "cover" && sscanf_s(line, "%*s %d", &v) == 1) st.cover = v != 0;
            else if (c == "flip" && sscanf_s(line, "%*s %d", &v) == 1) st.flip = v != 0;
            else if (c == "zoom" && sscanf_s(line, "%*s %d", &v) == 1) st.zoom = v != 0;
            else if (c == "mask" && sscanf_s(line, "%*s %d %d %d %d %d", &i, &x, &y, &w, &h) == 5 && i >= 0 && i < 4)
            { st.masks[i][0] = x; st.masks[i][1] = y; st.masks[i][2] = w; st.masks[i][3] = h; }
            else if (c == "source") { std::lock_guard<std::mutex> g(srcLock); pendingSource = line; }
        }
        quit = true;
    });

    // Sabit 60 FPS saat: kare n, epoch + n/fps anında. Yeni yakalama yoksa son kare tekrar kullanılır.
    LARGE_INTEGER qf, q0; QueryPerformanceFrequency(&qf); QueryPerformanceCounter(&q0);
    int64_t unix0 = UnixMs();
    if (a.epoch <= 0) a.epoch = unix0;
    auto due = [&](int64_t n) { return q0.QuadPart + (int64_t)(((double)(a.epoch - unix0) / 1000.0 + (double)n / a.fps) * qf.QuadPart); };
    HANDLE timer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
    if (!timer) timer = CreateWaitableTimerW(nullptr, FALSE, nullptr);

    TsMux ts;
    const int64_t base = 900000, tick = 90000 / a.fps;   // TS zamanı 10 sn'den başlar (DTS eksiye düşmesin)
    int64_t outCount = 0;
    std::vector<uint8_t> buf;
    auto sink = [&](const EncodedFrame& f) {
        buf.clear();
        int64_t dts = base + (outCount++ - 2) * tick;        // 2 B kare gecikmesi: DTS <= PTS
        ts.Write(f.data, f.size, base + f.pts * tick, dts, f.key, buf);
        out.Push(std::vector<uint8_t>(buf));
    };

    ComPtr<ID3D11Texture2D> capTex;
    int64_t n = 0, statFrames = 0, statFresh = 0, statLate = 0, statSkip = 0;
    double statGpu = 0;
    LARGE_INTEGER statAt; QueryPerformanceCounter(&statAt);
    Say("ready");
    while (!quit && !out.failed)
    {
        LARGE_INTEGER now; QueryPerformanceCounter(&now);
        int64_t d = due(n);
        if (now.QuadPart < d)
        {
            LARGE_INTEGER rel; rel.QuadPart = -(int64_t)((d - now.QuadPart) * 10000000.0 / qf.QuadPart);
            SetWaitableTimer(timer, &rel, 0, nullptr, nullptr, FALSE);
            WaitForSingleObject(timer, 100);
            continue;
        }
        // Çok geride kaldıysak (sistem takıldı) kareleri yetiştirmeye çalışmadan bugüne atlanır.
        int64_t behind = (int64_t)((now.QuadPart - d) * a.fps / qf.QuadPart);
        if (behind > 3) { n += behind; statSkip += behind; continue; }
        if (behind >= 1) statLate++;

        {
            std::string s;
            { std::lock_guard<std::mutex> g(srcLock); s.swap(pendingSource); }
            if (!s.empty())
            {
                long long hw = 0; int mon = 0;
                bool ok = sscanf_s(s.c_str(), "source window %lld", &hw) == 1 ? cap.StartWindow((HWND)(intptr_t)hw, err)
                        : sscanf_s(s.c_str(), "source monitor %d", &mon) == 1 ? cap.StartMonitor(mon, err) : false;
                Say(ok ? "source " + cap.Describe() : "error " + err);
            }
        }

        LARGE_INTEGER g0; QueryPerformanceCounter(&g0);
        if (cap.Latest(ctx.Get(), capTex)) statFresh++;
        ID3D11Texture2D* target = enc.NextInput();
        ren.Render(ctx.Get(), capTex.Get(), target);
        ctx->Flush();
        if (!enc.Encode(n, sink, err)) { Say("error " + err); break; }
        LARGE_INTEGER g1; QueryPerformanceCounter(&g1);
        statGpu += (g1.QuadPart - g0.QuadPart) * 1000.0 / qf.QuadPart;
        statFrames++;
        n++;

        if ((g1.QuadPart - statAt.QuadPart) >= qf.QuadPart * 5)
        {
            double sec = (g1.QuadPart - statAt.QuadPart) / (double)qf.QuadPart;
            char b[256];
            sprintf_s(b, "stats fps=%.1f fresh=%.1f late=%lld skipped=%lld submit_ms=%.2f queue_kb=%zu pending=%d capture=%s",
                      statFrames / sec, statFresh / sec, statLate, statSkip, statGpu / std::max<int64_t>(1, statFrames),
                      out.Queued() / 1024, enc.Pending(), cap.Active() ? cap.Describe().c_str() : "yok");
            Say(b);
            statFrames = statFresh = statLate = statSkip = 0; statGpu = 0; statAt = g1;
        }
    }
    quit = true;
    enc.Flush(sink);
    cap.Stop();
    out.Close();
    if (input.joinable()) input.detach();   // stdin okuması bloklu kalabilir; süreç zaten bitiyor
    Say("bye");
    return 0;
}
