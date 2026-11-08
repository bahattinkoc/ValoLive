#include "render.h"
#include <d3dcompiler.h>
#include <algorithm>

using Microsoft::WRL::ComPtr;

static const char* Hlsl = R"(
cbuffer C : register(b0) { float4 dst; float4 src; float4 color; float4 target; };
struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
V vs(uint id : SV_VertexID)
{
    float2 t = float2(id & 1, id >> 1);
    float2 p = lerp(dst.xy, dst.zw, t);
    V o;
    o.pos = float4(p.x / target.x * 2 - 1, 1 - p.y / target.y * 2, 0, 1);
    o.uv = lerp(src.xy, src.zw, t);
    return o;
}
Texture2D tex : register(t0);
SamplerState smp : register(s0);
float4 ps_tex(V i) : SV_Target { return tex.Sample(smp, i.uv); }
float4 ps_opaque(V i) : SV_Target { return float4(tex.Sample(smp, i.uv).rgb, 1); }
float4 ps_color(V i) : SV_Target { return color; }
// BT.709, sınırlı aralık (16-235 / 16-240).
float ps_y(V i) : SV_Target
{
    float3 c = tex.Sample(smp, i.uv).rgb;
    return 16.0 / 255 + dot(c, float3(0.2126, 0.7152, 0.0722)) * 219.0 / 255;
}
float2 ps_uv(V i) : SV_Target
{
    float3 c = tex.Sample(smp, i.uv).rgb;   // yarım çözünürlük: doğrusal örnekleme 2x2 ortalaması verir
    return 128.0 / 255 + float2(dot(c, float3(-0.1146, -0.3854, 0.5)), dot(c, float3(0.5, -0.4542, -0.0458))) * 224.0 / 255;
}
)";

static bool Compile(ID3D11Device* dev, const char* entry, const char* profile, ComPtr<ID3DBlob>& blob, std::string& error)
{
    ComPtr<ID3DBlob> err;
    if (FAILED(D3DCompile(Hlsl, strlen(Hlsl), "valocap", nullptr, nullptr, entry, profile, D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &blob, &err)))
    {
        error = std::string("gölgelendirici: ") + (err ? (const char*)err->GetBufferPointer() : entry);
        return false;
    }
    return true;
}

bool Renderer::Target(int w, int h, ComPtr<ID3D11Texture2D>& t, ComPtr<ID3D11RenderTargetView>& rtv, ComPtr<ID3D11ShaderResourceView>& srv)
{
    D3D11_TEXTURE2D_DESC d = {};
    d.Width = w; d.Height = h; d.MipLevels = 1; d.ArraySize = 1;
    d.Format = DXGI_FORMAT_B8G8R8A8_UNORM; d.SampleDesc.Count = 1;
    d.Usage = D3D11_USAGE_DEFAULT; d.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    return SUCCEEDED(dev->CreateTexture2D(&d, nullptr, &t)) &&
           SUCCEEDED(dev->CreateRenderTargetView(t.Get(), nullptr, &rtv)) &&
           SUCCEEDED(dev->CreateShaderResourceView(t.Get(), nullptr, &srv));
}

bool Renderer::Init(ID3D11Device* device, int width, int height, int outWidth, int outHeight, const LayerSpec* panelSpec, const std::vector<LayerSpec>& specs, std::string& error)
{
    dev = device; W = width; H = height; OW = outWidth; OH = outHeight;
    ComPtr<ID3DBlob> b;
    if (!Compile(dev, "vs", "vs_5_0", b, error) || FAILED(dev->CreateVertexShader(b->GetBufferPointer(), b->GetBufferSize(), nullptr, &vs))) return false;
    struct { const char* e; ComPtr<ID3D11PixelShader>* ps; } list[] = {
        { "ps_tex", &psTex }, { "ps_opaque", &psOpaque }, { "ps_color", &psColor }, { "ps_y", &psY }, { "ps_uv", &psUV } };
    for (auto& p : list)
        if (!Compile(dev, p.e, "ps_5_0", b, error) || FAILED(dev->CreatePixelShader(b->GetBufferPointer(), b->GetBufferSize(), nullptr, p.ps->GetAddressOf()))) return false;

    D3D11_BUFFER_DESC cbd = { 64, D3D11_USAGE_DEFAULT, D3D11_BIND_CONSTANT_BUFFER };
    D3D11_SAMPLER_DESC sd = {};
    sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    sd.MaxLOD = D3D11_FLOAT32_MAX;
    // Düz (önceden çarpılmamış) alfa: GDI+ ve ffmpeg overlay ile aynı karışım.
    D3D11_BLEND_DESC bd = {};
    auto& rt = bd.RenderTarget[0];
    rt.BlendEnable = TRUE;
    rt.SrcBlend = D3D11_BLEND_SRC_ALPHA; rt.DestBlend = D3D11_BLEND_INV_SRC_ALPHA; rt.BlendOp = D3D11_BLEND_OP_ADD;
    rt.SrcBlendAlpha = D3D11_BLEND_ONE; rt.DestBlendAlpha = D3D11_BLEND_INV_SRC_ALPHA; rt.BlendOpAlpha = D3D11_BLEND_OP_ADD;
    rt.RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
    D3D11_RASTERIZER_DESC rd = {};
    rd.FillMode = D3D11_FILL_SOLID; rd.CullMode = D3D11_CULL_NONE;
    if (FAILED(dev->CreateBuffer(&cbd, nullptr, &cb)) || FAILED(dev->CreateSamplerState(&sd, &linear)) ||
        FAILED(dev->CreateBlendState(&bd, &alpha)) || FAILED(dev->CreateRasterizerState(&rd, &raster)) ||
        !Target(W, H, scene, sceneRtv, sceneSrv) || !Target(W, H, out, outRtv, outSrv))
    { error = "çizim kaynakları oluşturulamadı"; return false; }

    if (panelSpec) { hasPanel = true; panel.spec = *panelSpec; if (!Open(panel, error)) return false; }
    layers.resize(specs.size());
    for (size_t i = 0; i < specs.size(); i++) { layers[i].spec = specs[i]; if (!Open(layers[i], error)) return false; }
    return true;
}

bool Renderer::Open(Shared& s, std::string& error)
{
    s.map = OpenFileMappingW(FILE_MAP_READ, FALSE, s.spec.name.c_str());
    if (!s.map) { error = "paylaşılan bellek açılamadı"; return false; }
    s.head = (const volatile int*)MapViewOfFile(s.map, FILE_MAP_READ, 0, 0, 0);
    if (!s.head) { error = "paylaşılan bellek eşlenemedi"; return false; }
    D3D11_TEXTURE2D_DESC d = {};
    d.Width = s.spec.w; d.Height = s.spec.h; d.MipLevels = 1; d.ArraySize = 1;
    d.Format = DXGI_FORMAT_B8G8R8A8_UNORM; d.SampleDesc.Count = 1;
    d.Usage = D3D11_USAGE_DEFAULT; d.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    if (FAILED(dev->CreateTexture2D(&d, nullptr, &s.tex)) || FAILED(dev->CreateShaderResourceView(s.tex.Get(), nullptr, &s.srv)))
    { error = "katman dokusu oluşturulamadı"; return false; }
    return true;
}

// Sıra kilidi: yazıcı seq'i tek yapar, pikselleri yazar, çift yapar. Okuma sırasında seq değiştiyse bir sonraki karede tekrar denenir.
void Renderer::Refresh(ID3D11DeviceContext* ctx, Shared& s)
{
    int seq = s.head[0];
    if (seq == s.lastSeq || (seq & 1)) return;
    MemoryBarrier();
    ctx->UpdateSubresource(s.tex.Get(), 0, nullptr, (const void*)(s.head + 4), s.spec.w * 4, 0);
    MemoryBarrier();
    if (s.head[0] == seq) s.lastSeq = seq;
}

void Renderer::Quad(ID3D11DeviceContext* ctx, float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1, const float* color, float tw, float th)
{
    float c[16] = { x0, y0, x1, y1, u0, v0, u1, v1, 0, 0, 0, 0, tw, th, 0, 0 };
    if (color) memcpy(c + 8, color, 16);
    ctx->UpdateSubresource(cb.Get(), 0, nullptr, c, 0, 0);
    ctx->Draw(4, 0);
}

void Renderer::Render(ID3D11DeviceContext* ctx, ID3D11Texture2D* capture, ID3D11Texture2D* nv12)
{
    const State st = state;
    ID3D11Buffer* cbs[] = { cb.Get() };
    ID3D11SamplerState* smps[] = { linear.Get() };
    ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
    ctx->IASetInputLayout(nullptr);
    ctx->VSSetShader(vs.Get(), nullptr, 0);
    ctx->VSSetConstantBuffers(0, 1, cbs);
    ctx->PSSetConstantBuffers(0, 1, cbs);
    ctx->PSSetSamplers(0, 1, smps);
    ctx->RSSetState(raster.Get());
    float fW = (float)W, fH = (float)H;
    D3D11_VIEWPORT vp = { 0, 0, fW, fH, 0, 1 };
    ctx->RSSetViewports(1, &vp);
    ID3D11ShaderResourceView* none[] = { nullptr };

    // 1) Sahne: oyun + maskeler + örtü (ffmpeg'de ters çevirmeden önceki kısım).
    float black[4] = { 0, 0, 0, 1 };
    ctx->ClearRenderTargetView(sceneRtv.Get(), black);
    ctx->OMSetRenderTargets(1, sceneRtv.GetAddressOf(), nullptr);
    ctx->OMSetBlendState(nullptr, nullptr, 0xFFFFFFFF);
    if (capture)
    {
        if (capture != capTexSeen.Get()) { capTexSeen = capture; capSrv.Reset(); dev->CreateShaderResourceView(capture, nullptr, &capSrv); }
        D3D11_TEXTURE2D_DESC d; capture->GetDesc(&d);
        float s = std::min(fW / d.Width, fH / d.Height), w = d.Width * s, h = d.Height * s;   // en-boy korunur, kenarlar siyah
        ctx->PSSetShader(psOpaque.Get(), nullptr, 0);
        ctx->PSSetShaderResources(0, 1, capSrv.GetAddressOf());
        Quad(ctx, (fW - w) / 2, (fH - h) / 2, (fW + w) / 2, (fH + h) / 2, 0, 0, 1, 1, nullptr, fW, fH);
    }
    ctx->PSSetShader(psColor.Get(), nullptr, 0);
    for (auto& m : st.masks)
        if (m[2] > 0 && m[3] > 0) Quad(ctx, (float)m[0], (float)m[1], (float)(m[0] + m[2]), (float)(m[1] + m[3]), 0, 0, 1, 1, st.fill, fW, fH);
    if (st.cover) Quad(ctx, 0, 0, fW, fH, 0, 0, 1, 1, st.fill, fW, fH);

    // 2) Çıkış: sahne (istenirse ters) · büyüteç · panel · katmanlar.
    ctx->OMSetRenderTargets(1, outRtv.GetAddressOf(), nullptr);
    ctx->PSSetShader(psOpaque.Get(), nullptr, 0);
    ctx->PSSetShaderResources(0, 1, sceneSrv.GetAddressOf());
    if (st.flip) Quad(ctx, 0, 0, fW, fH, 1, 1, 0, 0, nullptr, fW, fH);
    else Quad(ctx, 0, 0, fW, fH, 0, 0, 1, 1, nullptr, fW, fH);
    if (st.zoom)
    {
        float z = (float)(std::max(60, (int)(140 * H / 1080.0)) * 2);
        float u0 = (fW - z) / 2 / fW, v0 = (fH - z) / 2 / fH, u1 = (fW + z) / 2 / fW, v1 = (fH + z) / 2 / fH;
        if (st.flip) { std::swap(u0, u1); std::swap(v0, v1); }
        Quad(ctx, fW / 2 - z, fH / 2 - z, fW / 2 + z, fH / 2 + z, u0, v0, u1, v1, nullptr, fW, fH);
    }
    ctx->OMSetBlendState(alpha.Get(), nullptr, 0xFFFFFFFF);
    ctx->PSSetShader(psTex.Get(), nullptr, 0);
    if (hasPanel)
    {
        Refresh(ctx, panel);
        if (st.panel)
        {
            ctx->PSSetShaderResources(0, 1, panel.srv.GetAddressOf());
            Quad(ctx, 0, 0, fW, fH, 0, 0, 1, 1, nullptr, fW, fH);
        }
    }
    for (size_t i = 0; i < layers.size(); i++)
    {
        auto& l = layers[i];
        if (!st.layer[i]) continue;               // gizli katman: ne kopyalanır ne çizilir
        Refresh(ctx, l);
        ctx->PSSetShaderResources(0, 1, l.srv.GetAddressOf());
        float x = (float)l.spec.x, y = (float)l.spec.y;
        Quad(ctx, x, y, x + l.spec.w * l.spec.up, y + l.spec.h * l.spec.up, 0, 0, 1, 1, nullptr, fW, fH);
    }
    ctx->PSSetShaderResources(0, 1, none);

    // 3) NV12: Y tam çözünürlükte, UV yarı çözünürlükte. Kodlayıcı boyutu tuvalden farklıysa (720p yayın) burada ölçeklenir.
    auto& views = nvViews[nv12];
    if (!views.first)
    {
        D3D11_RENDER_TARGET_VIEW_DESC rv = {};
        rv.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
        rv.Format = DXGI_FORMAT_R8_UNORM;
        dev->CreateRenderTargetView(nv12, &rv, &views.first);
        rv.Format = DXGI_FORMAT_R8G8_UNORM;
        dev->CreateRenderTargetView(nv12, &rv, &views.second);
    }
    ctx->OMSetBlendState(nullptr, nullptr, 0xFFFFFFFF);
    ctx->OMSetRenderTargets(1, views.first.GetAddressOf(), nullptr);
    ctx->PSSetShader(psY.Get(), nullptr, 0);
    ctx->PSSetShaderResources(0, 1, outSrv.GetAddressOf());
    float oW = (float)OW, oH = (float)OH;
    D3D11_VIEWPORT full = { 0, 0, oW, oH, 0, 1 };
    ctx->RSSetViewports(1, &full);
    Quad(ctx, 0, 0, oW, oH, 0, 0, 1, 1, nullptr, oW, oH);
    D3D11_VIEWPORT half = { 0, 0, oW / 2, oH / 2, 0, 1 };
    ctx->RSSetViewports(1, &half);
    ctx->OMSetRenderTargets(1, views.second.GetAddressOf(), nullptr);
    ctx->PSSetShader(psUV.Get(), nullptr, 0);
    Quad(ctx, 0, 0, oW / 2, oH / 2, 0, 0, 1, 1, nullptr, oW / 2, oH / 2);
    ctx->PSSetShaderResources(0, 1, none);
    ID3D11RenderTargetView* noRt[] = { nullptr };
    ctx->OMSetRenderTargets(1, noRt, nullptr);
}
