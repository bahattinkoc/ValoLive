// Windows.Graphics.Capture: bir pencereyi ya da ekranı D3D11 dokusu olarak yakalar (ekran kartından hiç çıkmadan).
// Olay dinlenmez: çizim döngüsü her karede havuzu boşaltıp en yenisini alır (iş parçacığı yarışı yok).
#pragma once
#include <d3d11.h>
#include <wrl/client.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <string>

class Capture
{
public:
    bool Init(ID3D11Device* device, std::string& error);
    bool StartWindow(HWND hwnd, std::string& error);
    bool StartMonitor(int index, std::string& error);
    void Stop();
    // Yeni kare geldiyse true; dst içeriğin kopyası (boyut değişirse yeniden oluşturulur).
    bool Latest(ID3D11DeviceContext* ctx, Microsoft::WRL::ComPtr<ID3D11Texture2D>& dst);
    bool Active() const { return session != nullptr && !closed; }
    std::string Describe() const { return what; }

private:
    ID3D11Device* dev = nullptr;
    winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice rtDevice{ nullptr };
    winrt::Windows::Graphics::Capture::GraphicsCaptureItem item{ nullptr };
    winrt::Windows::Graphics::Capture::Direct3D11CaptureFramePool pool{ nullptr };
    winrt::Windows::Graphics::Capture::GraphicsCaptureSession session{ nullptr };
    winrt::event_token closedToken{};
    winrt::Windows::Graphics::SizeInt32 poolSize{};
    volatile bool closed = false;
    std::string what;

    bool Begin(std::string& error);
};
