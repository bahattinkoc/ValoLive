// NVENC (sürücünün nvEncodeAPI64.dll'i) ile D3D11 NV12 dokularından H.264 kodlama.
// Senkron mod: kare gönderildikten "gecikme" kadar sonra çıktısı kilitlenir (NVIDIA örneğindeki düzen);
// tüm çağrılar tek iş parçacığından (çizim döngüsü) yapılır.
#pragma once
#include <d3d11.h>
#include <wrl/client.h>
#include <cstdint>
#include <functional>
#include <string>
#include <vector>
#include "nvEncodeAPI.h"

struct EncodedFrame
{
    const uint8_t* data;
    size_t size;
    int64_t pts;     // kare sayısı (girişteki sıra numarası)
    bool key;
};

class NvEncoder
{
public:
    struct Options
    {
        int width = 1920, height = 1080, fps = 60;
        int bitrateK = 6000;
        int bframes = 2, lookahead = 20, gop = 120;
    };

    bool Open(ID3D11Device* device, const Options& o, std::string& error);
    // Sıradaki boş giriş dokusu (çizim bunun Y/UV yüzeylerine yazar).
    ID3D11Texture2D* NextInput();
    // NextInput'a yazılmış kareyi kodlar; hazır olan çıktılar sırayla sink'e verilir.
    bool Encode(int64_t frameIndex, const std::function<void(const EncodedFrame&)>& sink, std::string& error);
    // Kalanları boşaltır.
    void Flush(const std::function<void(const EncodedFrame&)>& sink);
    void Close();
    ~NvEncoder() { Close(); }

    int Pending() const { return (int)(sent - got); }

private:
    struct Slot
    {
        Microsoft::WRL::ComPtr<ID3D11Texture2D> tex;
        NV_ENC_REGISTERED_PTR reg = nullptr;
        NV_ENC_INPUT_PTR mapped = nullptr;
        NV_ENC_OUTPUT_PTR bitstream = nullptr;
    };

    HMODULE dll = nullptr;
    NV_ENCODE_API_FUNCTION_LIST api = { NV_ENCODE_API_FUNCTION_LIST_VER };
    void* enc = nullptr;
    std::vector<Slot> slots;
    uint64_t sent = 0, got = 0;
    int delay = 0;
    Options opt;

    bool LockOne(const std::function<void(const EncodedFrame&)>& sink);
};
