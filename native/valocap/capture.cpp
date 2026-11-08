#include "capture.h"
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/Windows.Foundation.Metadata.h>
#include <dxgi.h>
#include <algorithm>
#include <vector>

using namespace winrt;
namespace wgc = winrt::Windows::Graphics::Capture;
namespace wdx = winrt::Windows::Graphics::DirectX;
namespace wf = winrt::Windows::Foundation;
using Microsoft::WRL::ComPtr;

static std::string Hr(const char* what, hresult h) { char b[32]; sprintf_s(b, " (0x%08X)", (unsigned)h.value); return what + std::string(b); }

bool Capture::Init(ID3D11Device* device, std::string& error)
{
    dev = device;
    try
    {
        if (!wgc::GraphicsCaptureSession::IsSupported()) { error = "Windows.Graphics.Capture desteklenmiyor"; return false; }
        ComPtr<IDXGIDevice> dxgi;
        device->QueryInterface(IID_PPV_ARGS(&dxgi));
        com_ptr<::IInspectable> insp;
        check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgi.Get(), insp.put()));
        rtDevice = insp.as<wdx::Direct3D11::IDirect3DDevice>();
        return true;
    }
    catch (hresult_error const& e) { error = Hr("WGC aygıtı", e.code()); return false; }
}

bool Capture::StartWindow(HWND hwnd, std::string& error)
{
    Stop();
    try
    {
        auto interop = get_activation_factory<wgc::GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        check_hresult(interop->CreateForWindow(hwnd, guid_of<ABI::Windows::Graphics::Capture::IGraphicsCaptureItem>(), put_abi(item)));
        what = "pencere";
        return Begin(error);
    }
    catch (hresult_error const& e) { error = Hr("pencere yakalanamadı", e.code()); item = nullptr; return false; }
}

static BOOL CALLBACK EnumMon(HMONITOR m, HDC, LPRECT, LPARAM p) { ((std::vector<HMONITOR>*)p)->push_back(m); return TRUE; }

bool Capture::StartMonitor(int index, std::string& error)
{
    Stop();
    std::vector<HMONITOR> mons;
    EnumDisplayMonitors(nullptr, nullptr, EnumMon, (LPARAM)&mons);
    // 0 = birincil ekran (ddagrab output_idx=0 ile aynı).
    HMONITOR target = MonitorFromPoint({ 0, 0 }, MONITOR_DEFAULTTOPRIMARY);
    if (index > 0 && index < (int)mons.size()) target = mons[index];
    try
    {
        auto interop = get_activation_factory<wgc::GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        check_hresult(interop->CreateForMonitor(target, guid_of<ABI::Windows::Graphics::Capture::IGraphicsCaptureItem>(), put_abi(item)));
        what = "ekran";
        return Begin(error);
    }
    catch (hresult_error const& e) { error = Hr("ekran yakalanamadı", e.code()); item = nullptr; return false; }
}

bool Capture::Begin(std::string& error)
{
    try
    {
        closed = false;
        poolSize = item.Size();
        pool = wgc::Direct3D11CaptureFramePool::CreateFreeThreaded(rtDevice, wdx::DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, poolSize);
        session = pool.CreateCaptureSession(item);
        using wf::Metadata::ApiInformation;
        const wchar_t* type = L"Windows.Graphics.Capture.GraphicsCaptureSession";
        if (ApiInformation::IsPropertyPresent(type, L"IsCursorCaptureEnabled")) session.IsCursorCaptureEnabled(false);
        if (ApiInformation::IsPropertyPresent(type, L"IsBorderRequired"))
        {
            try { wgc::GraphicsCaptureAccess::RequestAccessAsync(wgc::GraphicsCaptureAccessKind::Borderless).get(); } catch (...) {}
            session.IsBorderRequired(false);
        }
        // Ekran 144/240 Hz olsa da saniyede en fazla 60 kare kopyalansın.
        if (ApiInformation::IsPropertyPresent(type, L"MinUpdateInterval"))
            session.MinUpdateInterval(wf::TimeSpan{ std::chrono::microseconds(16000) });
        closedToken = item.Closed([this](auto&&, auto&&) { closed = true; });
        session.StartCapture();
        return true;
    }
    catch (hresult_error const& e) { error = Hr("yakalama başlatılamadı", e.code()); Stop(); return false; }
}

void Capture::Stop()
{
    try
    {
        if (item && closedToken.value) item.Closed(closedToken);
        if (session) session.Close();
        if (pool) pool.Close();
    }
    catch (...) {}
    closedToken = {};
    session = nullptr; pool = nullptr; item = nullptr;
}

bool Capture::Latest(ID3D11DeviceContext* ctx, ComPtr<ID3D11Texture2D>& dst)
{
    if (!pool) return false;
    try
    {
        wgc::Direct3D11CaptureFrame last{ nullptr };
        for (auto f = pool.TryGetNextFrame(); f; f = pool.TryGetNextFrame())
        {
            if (last) last.Close();
            last = f;
        }
        if (!last) return false;
        auto size = last.ContentSize();
        auto access = last.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
        ComPtr<ID3D11Texture2D> src;
        check_hresult(access->GetInterface(IID_PPV_ARGS(&src)));
        D3D11_TEXTURE2D_DESC sd;
        src->GetDesc(&sd);
        UINT w = (UINT)std::max(1, std::min((int)sd.Width, size.Width)), h = (UINT)std::max(1, std::min((int)sd.Height, size.Height));

        D3D11_TEXTURE2D_DESC dd = {};
        if (dst) dst->GetDesc(&dd);
        if (!dst || dd.Width != w || dd.Height != h)
        {
            dst.Reset();
            D3D11_TEXTURE2D_DESC d = {};
            d.Width = w; d.Height = h; d.MipLevels = 1; d.ArraySize = 1;
            d.Format = DXGI_FORMAT_B8G8R8A8_UNORM; d.SampleDesc.Count = 1;
            d.Usage = D3D11_USAGE_DEFAULT; d.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            check_hresult(dev->CreateTexture2D(&d, nullptr, &dst));
        }
        D3D11_BOX box = { 0, 0, 0, w, h, 1 };
        ctx->CopySubresourceRegion(dst.Get(), 0, 0, 0, 0, src.Get(), 0, &box);
        last.Close();
        // Pencere boyutu değiştiyse havuz yeni boyuta göre kurulur (sonraki karelerden itibaren).
        if (size.Width != poolSize.Width || size.Height != poolSize.Height)
        {
            poolSize = size;
            pool.Recreate(rtDevice, wdx::DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, size);
        }
        return true;
    }
    catch (hresult_error const&) { return false; }
}
