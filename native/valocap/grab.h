// Bölge okuma: ValoLive'ın ekran okuyan özellikleri (rakip hafızası, yayın anları) oyun karesini GDI yerine buradan alır.
// Paylaşılan bellek (ValoLive oluşturur): 64 bayt başlık + BGRA. İstek gelince bölge bir karede hazırlama dokusuna
// kopyalanır, sonraki karede beklemeden okunur: çizim döngüsü hiç durmaz.
#pragma once
#include <d3d11.h>
#include <wrl/client.h>
#include <string>

struct GrabHeader
{
    volatile LONG req, done;     // ValoLive req'i artırır; valocap işi bitirince done = req
    volatile LONG x, y, w, h;    // oyun karesi koordinatı (pencerede iç alan)
    volatile LONG status;        // 0 tamam, 1 kare yok, 2 bölge karenin dışında, 3 okunamadı
    volatile LONG capW, capH;    // oyun karesinin boyutu
    volatile LONG capacity;      // başlıktan sonraki bayt
    LONG pad[6];
};
static_assert(sizeof(GrabHeader) == 64, "başlık 64 bayt");

class Grabber
{
public:
    bool Open(const std::wstring& name, std::string& error);
    void Close();
    // Her karede bir kez, yakalama karesi güncellendikten sonra.
    void Tick(ID3D11Device* dev, ID3D11DeviceContext* ctx, ID3D11Texture2D* frame);
    // Bekleyen istek ya da yolda kopya var: döngü kareler arasında da Tick çağırsın (bir kare beklemesin).
    bool Busy() const { return hdr && (inflight || hdr->req != hdr->done); }

private:
    HANDLE map = nullptr, ready = nullptr;   // ready: <ad>_ev, iş bitince tetiklenir (ValoLive uyumadan bekler)
    GrabHeader* hdr = nullptr;
    uint8_t* data = nullptr;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> staging;
    LONG inflight = 0;   // kopyası yolda olan istek
    int iw = 0, ih = 0;

    void Finish(LONG r, LONG status);
};
