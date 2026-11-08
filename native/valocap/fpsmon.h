// Oyun FPS ölçümü (valocap --fps <exe>): DXGI'nin Present olaylarını Windows olay kaydından (ETW) dinler.
// Oyuna hiçbir şey enjekte edilmez; PresentMon/CapFrameX ile aynı kaynak. Kullanıcı "Performance Log Users"
// grubunda ya da yönetici olmalı. 5 sn'de bir stdout'a: "fps avg=.. low1=.. max_ms=.. frames=..".
#pragma once
#include <string>

int RunFpsMonitor(const std::wstring& exeName);
