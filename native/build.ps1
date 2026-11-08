# valocap.exe (ekran kartı motoru) derleme betiği: Visual Studio C++ araçları (MSVC) ve Windows SDK gerekir.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "MSVC bulunamadi (Visual Studio 'C++ ile masaustu gelistirme' gerekli)" }
$vcvars = "$vs\VC\Auxiliary\Build\vcvars64.bat"
$src = "$root\valocap"
$obj = "$src\obj"
New-Item -ItemType Directory -Force $obj | Out-Null
$out = (Resolve-Path "$root\..").Path + "\valocap.exe"
$cl = "cl /nologo /std:c++20 /EHsc /O2 /MT /W3 /utf-8 /permissive- /DNOMINMAX /DWIN32_LEAN_AND_MEAN /DUNICODE /D_UNICODE " +
      "/Fo`"$obj\\`" `"$src\main.cpp`" `"$src\capture.cpp`" `"$src\render.cpp`" `"$src\nvenc.cpp`" `"$src\ts.cpp`" " +
      "/Fe`"$out`" /link d3d11.lib dxgi.lib d3dcompiler.lib windowsapp.lib user32.lib"
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"   # vcvars vswhere'i PATH'te arar
cmd /c "`"$vcvars`" >nul && $cl"
if ($LASTEXITCODE -ne 0) { throw "valocap derlemesi basarisiz" }
"OK: $out"
