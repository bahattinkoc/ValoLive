// Ekran kartında birleştirici: oyun (en-boy korunarak) · maskeler · örtü → (ters çevirme) · büyüteç · panel · katmanlar,
// sonra BT.709 sınırlı aralık NV12'ye iki geçişte dönüştürme. Katman pikselleri ValoLive'ın paylaşılan belleğinden gelir.
#pragma once
#include <d3d11.h>
#include <wrl/client.h>
#include <map>
#include <string>
#include <vector>

struct LayerSpec
{
    std::wstring name;   // paylaşılan bellek adı
    int x = 0, y = 0, w = 0, h = 0, up = 1;
};

class Renderer
{
public:
    bool Init(ID3D11Device* device, int width, int height, const LayerSpec* panel, const std::vector<LayerSpec>& layers, std::string& error);
    // capture null olabilir (henüz kare yok): siyah.
    void Render(ID3D11DeviceContext* ctx, ID3D11Texture2D* capture, ID3D11Texture2D* nv12);

    // Canlı durum (komut iş parçacığı yazar, çizim okur; tek tek int/bool, yarım okuma zararsız).
    struct State
    {
        int masks[4][4] = {};        // x, y, w, h (w=0: yok)
        bool cover = false, flip = false, zoom = false;
        bool panel = false;
        bool layer[32] = {};
        float fill[4] = { 0, 0, 0, 1 };
    } state;

    int LayerCount() const { return (int)layers.size(); }

private:
    struct Shared
    {
        LayerSpec spec;
        HANDLE map = nullptr;
        const volatile int* head = nullptr;    // seq, w, h, 0 ve ardından w*h*4 BGRA
        int lastSeq = -1;
        Microsoft::WRL::ComPtr<ID3D11Texture2D> tex;
        Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> srv;
    };

    ID3D11Device* dev = nullptr;
    int W = 0, H = 0;
    Microsoft::WRL::ComPtr<ID3D11VertexShader> vs;
    Microsoft::WRL::ComPtr<ID3D11PixelShader> psTex, psOpaque, psColor, psY, psUV;
    Microsoft::WRL::ComPtr<ID3D11Buffer> cb;
    Microsoft::WRL::ComPtr<ID3D11SamplerState> linear;
    Microsoft::WRL::ComPtr<ID3D11BlendState> alpha;
    Microsoft::WRL::ComPtr<ID3D11RasterizerState> raster;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> scene, out;
    Microsoft::WRL::ComPtr<ID3D11RenderTargetView> sceneRtv, outRtv;
    Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> sceneSrv, outSrv;
    std::map<ID3D11Texture2D*, std::pair<Microsoft::WRL::ComPtr<ID3D11RenderTargetView>, Microsoft::WRL::ComPtr<ID3D11RenderTargetView>>> nvViews;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> capTexSeen;
    Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> capSrv;
    bool hasPanel = false;
    Shared panel;
    std::vector<Shared> layers;

    bool Target(int w, int h, Microsoft::WRL::ComPtr<ID3D11Texture2D>& t, Microsoft::WRL::ComPtr<ID3D11RenderTargetView>& rtv, Microsoft::WRL::ComPtr<ID3D11ShaderResourceView>& srv);
    bool Open(Shared& s, std::string& error);
    void Refresh(ID3D11DeviceContext* ctx, Shared& s);
    void Quad(ID3D11DeviceContext* ctx, float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1, const float* color, float tw, float th);
};
