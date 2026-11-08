#include "grab.h"
#include <cstring>

bool Grabber::Open(const std::wstring& name, std::string& error)
{
    map = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, name.c_str());
    if (!map) { error = "bölge belleği açılamadı"; return false; }
    hdr = (GrabHeader*)MapViewOfFile(map, FILE_MAP_ALL_ACCESS, 0, 0, 0);
    if (!hdr) { error = "bölge belleği eşlenemedi"; Close(); return false; }
    data = (uint8_t*)hdr + sizeof(GrabHeader);
    ready = OpenEventW(EVENT_MODIFY_STATE, FALSE, (name + L"_ev").c_str());   // yoksa ValoLive yoklayarak bekler
    return true;
}

void Grabber::Close()
{
    if (hdr) UnmapViewOfFile(hdr);
    if (map) CloseHandle(map);
    if (ready) CloseHandle(ready);
    hdr = nullptr; map = nullptr; data = nullptr; ready = nullptr;
}

void Grabber::Finish(LONG r, LONG status)
{
    hdr->status = status;
    MemoryBarrier();
    hdr->done = r;
    if (ready) SetEvent(ready);
}

void Grabber::Tick(ID3D11Device* dev, ID3D11DeviceContext* ctx, ID3D11Texture2D* frame)
{
    if (!hdr) return;
    if (inflight)
    {
        D3D11_MAPPED_SUBRESOURCE m;
        HRESULT hr = ctx->Map(staging.Get(), 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &m);
        if (hr == DXGI_ERROR_WAS_STILL_DRAWING) return;   // kopya bitmedi: bir sonraki karede
        LONG r = inflight;
        inflight = 0;
        if (FAILED(hr)) { Finish(r, 3); return; }
        for (int y = 0; y < ih; y++) memcpy(data + (size_t)y * iw * 4, (uint8_t*)m.pData + (size_t)y * m.RowPitch, (size_t)iw * 4);
        ctx->Unmap(staging.Get(), 0);
        Finish(r, 0);
        return;
    }

    LONG r = hdr->req;
    if (r == hdr->done) return;
    MemoryBarrier();
    if (!frame) { Finish(r, 1); return; }
    D3D11_TEXTURE2D_DESC fd;
    frame->GetDesc(&fd);
    hdr->capW = fd.Width; hdr->capH = fd.Height;
    LONG x = hdr->x, y = hdr->y, w = hdr->w, h = hdr->h;
    if (w <= 0 || h <= 0 || x < 0 || y < 0 || x + w > (LONG)fd.Width || y + h > (LONG)fd.Height ||
        (int64_t)w * h * 4 > hdr->capacity)
    { Finish(r, 2); return; }

    // Hazırlama dokusu oyun karesi boyutunda: bölge değişse de (minimap, skor çubuğu) yeniden oluşturulmaz.
    D3D11_TEXTURE2D_DESC sd = {};
    if (staging) staging->GetDesc(&sd);
    if (!staging || sd.Width != fd.Width || sd.Height != fd.Height)
    {
        staging.Reset();
        D3D11_TEXTURE2D_DESC d = {};
        d.Width = fd.Width; d.Height = fd.Height; d.MipLevels = 1; d.ArraySize = 1;
        d.Format = DXGI_FORMAT_B8G8R8A8_UNORM; d.SampleDesc.Count = 1;
        d.Usage = D3D11_USAGE_STAGING; d.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        if (FAILED(dev->CreateTexture2D(&d, nullptr, &staging))) { Finish(r, 3); return; }
    }
    D3D11_BOX box = { (UINT)x, (UINT)y, 0, (UINT)(x + w), (UINT)(y + h), 1 };
    ctx->CopySubresourceRegion(staging.Get(), 0, 0, 0, 0, frame, 0, &box);
    inflight = r; iw = w; ih = h;
}
