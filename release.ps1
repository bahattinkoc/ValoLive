# ValoLive sürüm betiği: derler, zip'i hazırlar (ValoLive.exe, valocap.exe, fonts, LICENSE) ve GitHub sürümünü oluşturur.
#   .\release.ps1 -Notes notlar.md            # sürüm numarası src\AssemblyInfo.cs'ten (1.3.1.0 → v1.3.1)
#   .\release.ps1 -Notes notlar.md -DryRun    # yalnızca derle ve zip'le
# GitHub kimliği git'in kayıtlı kimlik bilgisinden alınır; sürüm o hesabın adıyla çıkar.
param([Parameter(Mandatory)][string]$Notes, [string]$Title = '', [switch]$DryRun)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$git = (Get-Command git -ErrorAction SilentlyContinue).Source
if (-not $git) { $git = 'C:\Program Files\Git\cmd\git.exe' }
$repo = 'bahattinkoc/ValoLive'

$ver = [regex]::Match((Get-Content "$root\src\AssemblyInfo.cs" -Raw), 'AssemblyVersion\("(\d+\.\d+\.\d+)').Groups[1].Value
if (-not $ver) { throw "AssemblyInfo.cs'te sürüm bulunamadı" }
$tag = "v$ver"
if (-not (Test-Path $Notes)) { throw "Not dosyası yok: $Notes" }
if (-not $DryRun)
{
    if (& $git -C $root status --porcelain) { throw 'Kaydedilmemiş değişiklik var: önce commit' }
    if ((& $git -C $root branch --show-current) -ne 'main') { throw 'Sürüm yalnızca main dalından çıkar' }
    & $git -C $root fetch -q origin main
    if ((& $git -C $root rev-parse HEAD) -ne (& $git -C $root rev-parse origin/main)) { throw 'main pushlanmamış (origin/main ile aynı değil)' }
}

& "$root\build.ps1"
& "$root\native\build.ps1"

$stage = Join-Path ([IO.Path]::GetTempPath()) "valolive-release-$ver"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory "$stage\ValoLive\fonts" | Out-Null
Copy-Item "$root\ValoLive.exe", "$root\valocap.exe" "$stage\ValoLive\"
Copy-Item "$root\fonts\*" "$stage\ValoLive\fonts\"
Copy-Item "$root\LICENSE" "$stage\ValoLive\LICENSE.txt"
$zip = "$stage\ValoLive-$tag.zip"
Compress-Archive -Path "$stage\ValoLive" -DestinationPath $zip -Force
"zip: $zip ($([math]::Round((Get-Item $zip).Length / 1KB)) KB)"
if ($DryRun) { return }

# git credential fill: girdi dosyadan (PowerShell 5.1 boruya BOM ekler, git reddeder)
$q = "$stage\cred.txt"
[IO.File]::WriteAllText($q, "protocol=https`nhost=github.com`n`n")
$cred = cmd /c "`"$git`" credential fill < `"$q`""
Remove-Item $q
$tok = ($cred | Where-Object { $_ -like 'password=*' }) -replace '^password=', ''
if (-not $tok) { throw 'GitHub kimlik bilgisi bulunamadı' }
$h = @{ Authorization = "token $tok"; Accept = 'application/vnd.github+json'; 'User-Agent' = 'ValoLive' }

$body = @{ tag_name = $tag; target_commitish = 'main'; name = $(if ($Title) { "ValoLive ${tag}: $Title" } else { "ValoLive $tag" })
           body = [IO.File]::ReadAllText((Resolve-Path $Notes), [Text.Encoding]::UTF8); draft = $false; prerelease = $false } | ConvertTo-Json
$rel = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$repo/releases" -Headers $h `
                         -Body ([Text.Encoding]::UTF8.GetBytes($body)) -ContentType 'application/json; charset=utf-8'
$up = $rel.upload_url -replace '\{.*\}', ''
$a = Invoke-RestMethod -Method Post -Uri "$($up)?name=ValoLive-$tag.zip" -Headers $h -InFile $zip -ContentType 'application/zip'
"$($rel.html_url) · $($rel.author.login) · $($a.name) $($a.state)"
