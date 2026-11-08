#include "nvenc.h"

using Microsoft::WRL::ComPtr;

static std::string Err(const char* what, NVENCSTATUS s) { return std::string(what) + " (NVENC " + std::to_string((int)s) + ")"; }

bool NvEncoder::Open(ID3D11Device* device, const Options& o, std::string& error)
{
    opt = o;
    dll = LoadLibraryW(L"nvEncodeAPI64.dll");
    if (!dll) { error = "nvEncodeAPI64.dll yok (NVIDIA sürücüsü?)"; return false; }
    using CreateFn = NVENCSTATUS(NVENCAPI*)(NV_ENCODE_API_FUNCTION_LIST*);
    auto create = (CreateFn)GetProcAddress(dll, "NvEncodeAPICreateInstance");
    if (!create) { error = "NvEncodeAPICreateInstance yok"; return false; }
    NVENCSTATUS s = create(&api);
    if (s != NV_ENC_SUCCESS) { error = Err("API açılamadı (sürücü eski olabilir)", s); return false; }

    NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS sp = { NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER };
    sp.device = device;
    sp.deviceType = NV_ENC_DEVICE_TYPE_DIRECTX;
    sp.apiVersion = NVENCAPI_VERSION;
    s = api.nvEncOpenEncodeSessionEx(&sp, &enc);
    if (s != NV_ENC_SUCCESS) { error = Err("kodlama oturumu açılamadı", s); enc = nullptr; return false; }

    NV_ENC_PRESET_CONFIG preset = { NV_ENC_PRESET_CONFIG_VER, { NV_ENC_CONFIG_VER } };
    s = api.nvEncGetEncodePresetConfigEx(enc, NV_ENC_CODEC_H264_GUID, NV_ENC_PRESET_P5_GUID, NV_ENC_TUNING_INFO_HIGH_QUALITY, &preset);
    if (s != NV_ENC_SUCCESS) { error = Err("ön ayar alınamadı", s); return false; }
    NV_ENC_CONFIG cfg = preset.presetCfg;
    cfg.version = NV_ENC_CONFIG_VER;
    cfg.profileGUID = NV_ENC_H264_PROFILE_HIGH_GUID;
    cfg.gopLength = o.gop;
    cfg.frameIntervalP = o.bframes + 1;
    auto& rc = cfg.rcParams;
    rc.rateControlMode = NV_ENC_PARAMS_RC_CBR;
    rc.averageBitRate = rc.maxBitRate = (uint32_t)o.bitrateK * 1000;
    rc.vbvBufferSize = rc.vbvInitialDelay = (uint32_t)o.bitrateK * 1000;   // 1 sn (ffmpeg -bufsize ile aynı)
    rc.enableLookahead = o.lookahead > 0;
    rc.lookaheadDepth = (uint16_t)o.lookahead;
    rc.enableAQ = 1;                                                       // uzamsal AQ
    auto& h = cfg.encodeCodecConfig.h264Config;
    h.idrPeriod = o.gop;
    h.repeatSPSPPS = 1;            // her anahtar karede SPS/PPS: yayına sonradan katılan izleyici için
    h.outputAUD = 1;
    h.chromaFormatIDC = 1;
    auto& vui = h.h264VUIParameters;
    vui.videoSignalTypePresentFlag = 1;
    vui.videoFormat = NV_ENC_VUI_VIDEO_FORMAT_UNSPECIFIED;
    vui.videoFullRangeFlag = 0;
    vui.colourDescriptionPresentFlag = 1;
    vui.colourPrimaries = NV_ENC_VUI_COLOR_PRIMARIES_BT709;
    vui.transferCharacteristics = NV_ENC_VUI_TRANSFER_CHARACTERISTIC_BT709;
    vui.colourMatrix = NV_ENC_VUI_MATRIX_COEFFS_BT709;

    NV_ENC_INITIALIZE_PARAMS init = { NV_ENC_INITIALIZE_PARAMS_VER };
    init.encodeGUID = NV_ENC_CODEC_H264_GUID;
    init.presetGUID = NV_ENC_PRESET_P5_GUID;
    init.tuningInfo = NV_ENC_TUNING_INFO_HIGH_QUALITY;
    init.encodeWidth = init.darWidth = init.maxEncodeWidth = o.width;
    init.encodeHeight = init.darHeight = init.maxEncodeHeight = o.height;
    init.frameRateNum = o.fps;
    init.frameRateDen = 1;
    init.enablePTD = 1;
    init.encodeConfig = &cfg;
    s = api.nvEncInitializeEncoder(enc, &init);
    if (s != NV_ENC_SUCCESS) { error = Err("kodlayıcı başlatılamadı", s); return false; }

    // Giriş halkası: B kareleri + ileri bakış + birkaç yedek (NVIDIA örneğindeki hesap).
    int count = cfg.frameIntervalP + rc.lookaheadDepth + 3;
    delay = count - 1;
    slots.resize(count);
    for (auto& sl : slots)
    {
        D3D11_TEXTURE2D_DESC d = {};
        d.Width = o.width; d.Height = o.height; d.MipLevels = 1; d.ArraySize = 1;
        d.Format = DXGI_FORMAT_NV12; d.SampleDesc.Count = 1;
        d.Usage = D3D11_USAGE_DEFAULT; d.BindFlags = D3D11_BIND_RENDER_TARGET;
        if (FAILED(device->CreateTexture2D(&d, nullptr, &sl.tex))) { error = "NV12 dokusu oluşturulamadı"; return false; }
        NV_ENC_REGISTER_RESOURCE r = { NV_ENC_REGISTER_RESOURCE_VER };
        r.resourceType = NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX;
        r.resourceToRegister = sl.tex.Get();
        r.width = o.width; r.height = o.height;
        r.bufferFormat = NV_ENC_BUFFER_FORMAT_NV12;
        r.bufferUsage = NV_ENC_INPUT_IMAGE;
        s = api.nvEncRegisterResource(enc, &r);
        if (s != NV_ENC_SUCCESS) { error = Err("doku kaydedilemedi", s); return false; }
        sl.reg = r.registeredResource;
        NV_ENC_CREATE_BITSTREAM_BUFFER b = { NV_ENC_CREATE_BITSTREAM_BUFFER_VER };
        s = api.nvEncCreateBitstreamBuffer(enc, &b);
        if (s != NV_ENC_SUCCESS) { error = Err("çıkış tamponu oluşturulamadı", s); return false; }
        sl.bitstream = b.bitstreamBuffer;
    }
    return true;
}

ID3D11Texture2D* NvEncoder::NextInput() { return slots[sent % slots.size()].tex.Get(); }

bool NvEncoder::Encode(int64_t frameIndex, const std::function<void(const EncodedFrame&)>& sink, std::string& error)
{
    // Halka dolduysa en eski çıktı alınmadan aynı yuva yeniden kullanılmaz.
    while (sent - got >= slots.size()) if (!LockOne(sink)) { error = "çıktı alınamadı"; return false; }
    auto& sl = slots[sent % slots.size()];
    NV_ENC_MAP_INPUT_RESOURCE m = { NV_ENC_MAP_INPUT_RESOURCE_VER };
    m.registeredResource = sl.reg;
    NVENCSTATUS s = api.nvEncMapInputResource(enc, &m);
    if (s != NV_ENC_SUCCESS) { error = Err("doku eşlenemedi", s); return false; }
    sl.mapped = m.mappedResource;

    NV_ENC_PIC_PARAMS p = { NV_ENC_PIC_PARAMS_VER };
    p.inputBuffer = sl.mapped;
    p.bufferFmt = NV_ENC_BUFFER_FORMAT_NV12;
    p.inputWidth = opt.width; p.inputHeight = opt.height; p.inputPitch = opt.width;
    p.outputBitstream = sl.bitstream;
    p.pictureStruct = NV_ENC_PIC_STRUCT_FRAME;
    p.inputTimeStamp = (uint64_t)frameIndex;
    s = api.nvEncEncodePicture(enc, &p);
    if (s != NV_ENC_SUCCESS && s != NV_ENC_ERR_NEED_MORE_INPUT) { error = Err("kare kodlanamadı", s); return false; }
    sent++;
    while ((int)(sent - got) > delay) if (!LockOne(sink)) { error = "çıktı alınamadı"; return false; }
    return true;
}

bool NvEncoder::LockOne(const std::function<void(const EncodedFrame&)>& sink)
{
    auto& sl = slots[got % slots.size()];
    NV_ENC_LOCK_BITSTREAM l = { NV_ENC_LOCK_BITSTREAM_VER };
    l.outputBitstream = sl.bitstream;
    if (api.nvEncLockBitstream(enc, &l) != NV_ENC_SUCCESS) return false;
    EncodedFrame f;
    f.data = (const uint8_t*)l.bitstreamBufferPtr;
    f.size = l.bitstreamSizeInBytes;
    f.pts = (int64_t)l.outputTimeStamp;
    f.key = l.pictureType == NV_ENC_PIC_TYPE_IDR || l.pictureType == NV_ENC_PIC_TYPE_I;
    if (f.size) sink(f);
    api.nvEncUnlockBitstream(enc, sl.bitstream);
    if (sl.mapped) { api.nvEncUnmapInputResource(enc, sl.mapped); sl.mapped = nullptr; }
    got++;
    return true;
}

void NvEncoder::Flush(const std::function<void(const EncodedFrame&)>& sink)
{
    if (!enc) return;
    NV_ENC_PIC_PARAMS p = { NV_ENC_PIC_PARAMS_VER };
    p.encodePicFlags = NV_ENC_PIC_FLAG_EOS;
    api.nvEncEncodePicture(enc, &p);
    while (got < sent && LockOne(sink)) {}
}

void NvEncoder::Close()
{
    if (enc)
    {
        for (auto& sl : slots)
        {
            if (sl.mapped) api.nvEncUnmapInputResource(enc, sl.mapped);
            if (sl.reg) api.nvEncUnregisterResource(enc, sl.reg);
            if (sl.bitstream) api.nvEncDestroyBitstreamBuffer(enc, sl.bitstream);
        }
        api.nvEncDestroyEncoder(enc);
        enc = nullptr;
    }
    slots.clear();
    if (dll) { FreeLibrary(dll); dll = nullptr; }
}
