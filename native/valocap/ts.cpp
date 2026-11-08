#include "ts.h"
#include <cstring>

static uint32_t Crc32(const uint8_t* p, size_t n)
{
    uint32_t crc = 0xFFFFFFFF;
    for (size_t i = 0; i < n; i++)
    {
        crc ^= (uint32_t)p[i] << 24;
        for (int b = 0; b < 8; b++) crc = (crc & 0x80000000) ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
    }
    return crc;
}

static void PutTs(uint8_t* p, uint8_t marker, int64_t t)
{
    // 33 bit zaman, PES başlığı biçimi: 4 bit işaret + 3 + 1 | 15 + 1 | 15 + 1
    t &= 0x1FFFFFFFFLL;
    p[0] = (uint8_t)((marker << 4) | (((t >> 30) & 0x07) << 1) | 1);
    p[1] = (uint8_t)(t >> 22);
    p[2] = (uint8_t)((((t >> 15) & 0x7F) << 1) | 1);
    p[3] = (uint8_t)(t >> 7);
    p[4] = (uint8_t)(((t & 0x7F) << 1) | 1);
}

void TsMux::Section(uint16_t pid, uint8_t& cc, const std::vector<uint8_t>& section, std::vector<uint8_t>& out)
{
    uint8_t pkt[188];
    memset(pkt, 0xFF, sizeof pkt);
    pkt[0] = 0x47;
    pkt[1] = (uint8_t)(0x40 | (pid >> 8));      // payload_unit_start
    pkt[2] = (uint8_t)pid;
    pkt[3] = (uint8_t)(0x10 | (cc++ & 0x0F));    // yalnızca yük
    pkt[4] = 0;                                  // pointer_field
    memcpy(pkt + 5, section.data(), section.size());
    out.insert(out.end(), pkt, pkt + 188);
}

void TsMux::Tables(std::vector<uint8_t>& out)
{
    std::vector<uint8_t> pat = { 0x00, 0xB0, 0x0D, 0x00, 0x01, 0xC1, 0x00, 0x00,
                                 0x00, 0x01, (uint8_t)(0xE0 | (PmtPid >> 8)), (uint8_t)PmtPid };
    uint32_t c = Crc32(pat.data(), pat.size());
    pat.insert(pat.end(), { (uint8_t)(c >> 24), (uint8_t)(c >> 16), (uint8_t)(c >> 8), (uint8_t)c });
    Section(0, ccPat, pat, out);

    std::vector<uint8_t> pmt = { 0x02, 0xB0, 0x12, 0x00, 0x01, 0xC1, 0x00, 0x00,
                                 (uint8_t)(0xE0 | (VideoPid >> 8)), (uint8_t)VideoPid, 0xF0, 0x00,
                                 0x1B, (uint8_t)(0xE0 | (VideoPid >> 8)), (uint8_t)VideoPid, 0xF0, 0x00 };
    c = Crc32(pmt.data(), pmt.size());
    pmt.insert(pmt.end(), { (uint8_t)(c >> 24), (uint8_t)(c >> 16), (uint8_t)(c >> 8), (uint8_t)c });
    Section(PmtPid, ccPmt, pmt, out);
}

void TsMux::Write(const uint8_t* au, size_t size, int64_t pts, int64_t dts, bool key, std::vector<uint8_t>& out)
{
    if (key || lastTables < 0 || dts - lastTables >= 45000) { Tables(out); lastTables = dts; }

    // PES başlığı: uzunluk 0 (video için sınırsız), PTS+DTS.
    uint8_t pes[19] = { 0x00, 0x00, 0x01, 0xE0, 0x00, 0x00, 0x80, 0xC0, 10 };
    PutTs(pes + 9, 0x3, pts);
    PutTs(pes + 14, 0x1, dts);

    size_t total = sizeof pes + size, done = 0;
    bool first = true;
    while (done < total)
    {
        uint8_t pkt[188];
        pkt[0] = 0x47;
        pkt[1] = (uint8_t)((first ? 0x40 : 0) | (VideoPid >> 8));
        pkt[2] = (uint8_t)VideoPid;
        size_t pos = 4;
        size_t left = total - done;

        // Uyarlama alanı: ilk pakette PCR (+ anahtar karede rastgele erişim), son pakette doldurma.
        size_t afLen = 0;
        bool af = first || left < 184;
        if (af)
        {
            uint8_t flags = 0;
            size_t need = 1;                               // bayrak baytı
            if (first) { flags |= 0x10; need += 6; if (key) flags |= 0x40; }
            size_t room = 184 - (need + 1);                // +1: uzunluk baytı
            size_t stuff = left < room ? room - left : 0;
            afLen = need + stuff;
            pkt[pos++] = (uint8_t)afLen;
            pkt[pos++] = flags;
            if (first)
            {
                int64_t pcr = (dts & 0x1FFFFFFFFLL);       // PCR taban = DTS (90 kHz), uzantı 0
                pkt[pos++] = (uint8_t)(pcr >> 25);
                pkt[pos++] = (uint8_t)(pcr >> 17);
                pkt[pos++] = (uint8_t)(pcr >> 9);
                pkt[pos++] = (uint8_t)(pcr >> 1);
                pkt[pos++] = (uint8_t)(((pcr & 1) << 7) | 0x7E);
                pkt[pos++] = 0;
            }
            memset(pkt + pos, 0xFF, stuff);
            pos += stuff;
        }
        pkt[3] = (uint8_t)((af ? 0x30 : 0x10) | (ccVideo++ & 0x0F));

        size_t n = 188 - pos;
        for (size_t i = 0; i < n; i++, done++)
            pkt[pos + i] = done < sizeof pes ? pes[done] : au[done - sizeof pes];
        out.insert(out.end(), pkt, pkt + 188);
        first = false;
    }
}
