#include "fpsmon.h"
#include <windows.h>
#include <evntrace.h>
#include <evntcons.h>
#include <tlhelp32.h>
#include <algorithm>
#include <atomic>
#include <cstdio>
#include <mutex>
#include <thread>
#include <vector>

// Microsoft-Windows-DXGI; Present_Start = 42 (PresentMon'un kullandığı olay).
static const GUID DxgiProvider = { 0xCA11C036, 0x0102, 0x4A2D, { 0xA6, 0xAD, 0xF0, 0x3C, 0xFE, 0xD5, 0xD3, 0xC9 } };
static const USHORT PresentStart = 42;
static const wchar_t* SessionName = L"ValoLiveFps";

static std::mutex lk;
static std::vector<int64_t> stamps;            // oyunun Present anları (QPC)
static std::atomic<DWORD> gamePid{ 0 };

static void WINAPI OnEvent(PEVENT_RECORD e)
{
    if (e->EventHeader.EventDescriptor.Id != PresentStart || e->EventHeader.ProcessId != gamePid.load() ||
        !IsEqualGUID(e->EventHeader.ProviderId, DxgiProvider)) return;
    std::lock_guard<std::mutex> g(lk);
    stamps.push_back(e->EventHeader.TimeStamp.QuadPart);
}

static DWORD FindProcess(const std::wstring& exe)
{
    DWORD pid = 0;
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return 0;
    PROCESSENTRY32W p = { sizeof p };
    for (BOOL ok = Process32FirstW(snap, &p); ok; ok = Process32NextW(snap, &p))
        if (_wcsicmp(p.szExeFile, exe.c_str()) == 0) { pid = p.th32ProcessID; break; }
    CloseHandle(snap);
    return pid;
}

struct Props
{
    EVENT_TRACE_PROPERTIES p;
    wchar_t name[64];
};

static void InitProps(Props& s)
{
    ZeroMemory(&s, sizeof s);
    s.p.Wnode.BufferSize = sizeof s;
    s.p.Wnode.Flags = WNODE_FLAG_TRACED_GUID;
    s.p.Wnode.ClientContext = 1;   // QPC zaman damgası
    s.p.LogFileMode = EVENT_TRACE_REAL_TIME_MODE;
    s.p.LoggerNameOffset = offsetof(Props, name);
}

int RunFpsMonitor(const std::wstring& exeName)
{
    Props props;
    InitProps(props);
    ControlTraceW(0, SessionName, &props.p, EVENT_TRACE_CONTROL_STOP);   // önceki çökmeden kalan oturum
    InitProps(props);
    TRACEHANDLE session = 0;
    ULONG rc = StartTraceW(&session, SessionName, &props.p);
    if (rc != ERROR_SUCCESS)
    {
        printf("error olay kaydı açılamadı (%lu%s)\n", rc, rc == ERROR_ACCESS_DENIED ? ", Performance Log Users grubu gerekli" : "");
        fflush(stdout);
        return 6;
    }
    rc = EnableTraceEx2(session, &DxgiProvider, EVENT_CONTROL_CODE_ENABLE_PROVIDER, TRACE_LEVEL_VERBOSE, ~0ull, 0, 0, nullptr);
    if (rc != ERROR_SUCCESS)
    {
        printf("error DXGI olayları açılamadı (%lu)\n", rc); fflush(stdout);
        ControlTraceW(session, nullptr, &props.p, EVENT_TRACE_CONTROL_STOP);
        return 7;
    }

    EVENT_TRACE_LOGFILEW log = {};
    log.LoggerName = const_cast<wchar_t*>(SessionName);
    log.ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD | PROCESS_TRACE_MODE_RAW_TIMESTAMP;
    log.EventRecordCallback = OnEvent;
    TRACEHANDLE trace = OpenTraceW(&log);
    if (trace == INVALID_PROCESSTRACE_HANDLE)
    {
        printf("error olay kaydı okunamadı (%lu)\n", GetLastError()); fflush(stdout);
        ControlTraceW(session, nullptr, &props.p, EVENT_TRACE_CONTROL_STOP);
        return 8;
    }
    std::thread reader([&] { ProcessTrace(&trace, 1, nullptr, nullptr); });

    std::atomic<bool> quit{ false };
    std::thread input([&] {
        char line[64];
        while (fgets(line, sizeof line, stdin)) if (strstr(line, "quit")) break;
        quit = true;
    });

    LARGE_INTEGER qf; QueryPerformanceFrequency(&qf);
    printf("ready\n"); fflush(stdout);
    int64_t last = 0;   // önceki pencerenin son karesi: aradaki süre de sayılsın
    std::vector<double> ms;
    for (int tick = 0; !quit; tick++)
    {
        Sleep(250);
        if (tick % 8 == 0) { DWORD pid = FindProcess(exeName); if (pid != gamePid) { gamePid = pid; last = 0; } }
        if (tick % 20 != 19) continue;   // 5 sn
        std::vector<int64_t> s;
        { std::lock_guard<std::mutex> g(lk); s.swap(stamps); }
        std::sort(s.begin(), s.end());
        ms.clear();
        for (int64_t t : s) { if (last) ms.push_back((t - last) * 1000.0 / qf.QuadPart); last = t; }
        if (ms.empty()) { printf("fps none\n"); fflush(stdout); continue; }
        double sum = 0; for (double m : ms) sum += m;
        std::vector<double> sorted = ms;
        std::sort(sorted.begin(), sorted.end());
        double p99 = sorted[std::min(sorted.size() - 1, (size_t)(sorted.size() * 0.99))];
        printf("fps avg=%.0f low1=%.0f max_ms=%.1f frames=%zu\n", ms.size() * 1000.0 / sum, 1000.0 / p99, sorted.back(), ms.size());
        fflush(stdout);
    }

    ControlTraceW(session, nullptr, &props.p, EVENT_TRACE_CONTROL_STOP);   // ProcessTrace döner
    CloseTrace(trace);
    if (reader.joinable()) reader.join();
    input.detach();
    printf("bye\n"); fflush(stdout);
    return 0;
}
