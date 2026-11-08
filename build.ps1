# ValoLive derleme betigi: Visual Studio'nun C# derleyicisiyle .NET Framework 4.8 hedefler.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Get-ChildItem "C:\Program Files\Microsoft Visual Studio\2022\*\MSBuild\Current\Bin\Roslyn\csc.exe" | Select-Object -First 1 -ExpandProperty FullName
# Arayüz çevirileri (lang\xx.txt) exe'ye gömülür: lang.xx.txt
$res = @(Get-ChildItem "$root\lang\*.txt" -ErrorAction SilentlyContinue | Where-Object { $_.BaseName -ne 'keys' } | ForEach-Object { "-resource:$($_.FullName),lang.$($_.BaseName).txt" })
& $csc -nologo @res -target:winexe -platform:x64 -optimize+ -langversion:latest -codepage:65001 `
    -out:"$root\ValoLive.exe" -win32icon:"$root\icon.ico" `
    -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll `
    -r:System.Web.Extensions.dll -r:System.Security.dll `
    -r:"$env:WINDIR\Microsoft.NET\assembly\GAC_MSIL\System.Speech\v4.0_4.0.0.0__31bf3856ad364e35\System.Speech.dll" `
    "$root\src\*.cs"
if ($LASTEXITCODE -ne 0) { throw "Derleme basarisiz" }
"OK: $root\ValoLive.exe"
