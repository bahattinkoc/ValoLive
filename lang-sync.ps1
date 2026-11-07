# Çeviri bakımı: koddaki L.T("…") / L.F("…") metinlerini toplar, lang\keys.txt'ye yazar ve
# her lang\xx.txt için eksik (çevrilmemiş) ve artık kullanılmayan satırları raporlar.
# Satır biçimi: Türkçe<TAB>çeviri ; satır sonu \n, sekme \t olarak yazılır.
param([switch]$Quiet)
$root = $PSScriptRoot
$keys = New-Object 'System.Collections.Generic.SortedSet[string]' ([StringComparer]::Ordinal)
foreach ($f in Get-ChildItem "$root\src\*.cs") {
    $src = [IO.File]::ReadAllText($f.FullName)
    foreach ($m in [regex]::Matches($src, 'L\.[TF]\("((?:[^"\\]|\\.)*)"')) {
        $s = [regex]::Replace($m.Groups[1].Value, '\\(.)', { param($x) switch ($x.Groups[1].Value) { 'n' { "`n" } 't' { "`t" } default { $x.Groups[1].Value } } })
        [void]$keys.Add($s)
    }
}
function Esc([string]$s) { $s.Replace("`n", '\n').Replace("`t", '\t') }
$utf8 = New-Object Text.UTF8Encoding $false
New-Item -ItemType Directory -Force "$root\lang" | Out-Null
[IO.File]::WriteAllLines("$root\lang\keys.txt", [string[]]@($keys | ForEach-Object { Esc $_ }), $utf8)
"$($keys.Count) metin -> lang\keys.txt"
$escKeys = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($k in $keys) { [void]$escKeys.Add((Esc $k)) }
foreach ($lf in Get-ChildItem "$root\lang\*.txt" | Where-Object { $_.BaseName -ne 'keys' }) {
    $have = New-Object Collections.Hashtable ([StringComparer]::Ordinal)
    foreach ($line in [IO.File]::ReadAllLines($lf.FullName, [Text.Encoding]::UTF8)) {
        $tab = $line.IndexOf("`t"); if ($tab -gt 0 -and -not $line.StartsWith('#')) { $have[$line.Substring(0, $tab)] = 1 }
    }
    $missing = @($escKeys | Where-Object { -not $have.ContainsKey($_) })
    $unused = @($have.Keys | Where-Object { -not $escKeys.Contains($_) })
    "{0,-3} eksik {1,3} · kullanılmayan {2,3}" -f $lf.BaseName, $missing.Count, $unused.Count
    if (-not $Quiet) { $missing | ForEach-Object { "    - $_" }; $unused | ForEach-Object { "    ? $_" } }
}
