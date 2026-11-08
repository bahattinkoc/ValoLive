// MPEG-TS paketleyici: tek H.264 video akışı (PID 0x100), PTS/DTS'li PES. ffmpeg bunu "-f mpegts" ile okur,
// sesleri ekleyip videoyu kopyalar (-c:v copy). PAT/PMT her anahtar karede ve en az 0,5 sn'de bir tekrarlanır.
#pragma once
#include <cstdint>
#include <vector>

class TsMux
{
public:
    // pts/dts 90 kHz biriminde. key: anahtar kare (PAT/PMT önüne yazılır, rastgele erişim işareti konur).
    void Write(const uint8_t* au, size_t size, int64_t pts, int64_t dts, bool key, std::vector<uint8_t>& out);

private:
    static constexpr uint16_t PmtPid = 0x1000, VideoPid = 0x100;
    uint8_t ccPat = 0, ccPmt = 0, ccVideo = 0;
    int64_t lastTables = -1;

    void Tables(std::vector<uint8_t>& out);
    void Section(uint16_t pid, uint8_t& cc, const std::vector<uint8_t>& section, std::vector<uint8_t>& out);
};
