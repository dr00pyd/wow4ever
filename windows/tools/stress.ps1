<#
  Stresstest av Forever Scout (bara Debug-byggen: --stress, FOREVERSCOUT_DATADIR och FOREVERSCOUT_BASEURL finns inte i Release).

  Bygger en isolerad datakatalog och en falsk WoW-mapp (_forever_ med ForeverScout.lua, Auctionator.lua och DBCache.bin
  från client/fixtures), pekar API:t på en död adress, startar appen med --stress=<minuter> och mäter utifrån:
  tid, arbetsmängd (Working Set), privat minne och "Working Set - Private" (kolumnen Minne i Aktivitetshanteraren)
  var 10:e sekund, medan filerna ändras med 1–4 sekunders mellanrum (synk). Kör aldrig mot produktionen.

    powershell -ExecutionPolicy Bypass -File client\windows\tools\stress.ps1 [-Minutes 10] [-Exe <ForeverScout.exe>] [-Seed 1]
    ... -Scenario close-exit            X → Stäng: appen ska avsluta sig själv (1.0.11-kraschen)
    ... -Screenshots                    samma miljö men --screenshots; bilderna kopieras till <WorkDir>\screenshots

  Resultat i <WorkDir>: memory.csv, data\stress.log (appens egen logg), data\uploads.log, summary.txt.
  Exit-kod 0 = appen avslutade sig själv i tid med kod 0 (inga ohanterade undantag, inga kvarlevande fönster).
#>
param(
    [double]$Minutes = 10,
    [string]$Exe = "",
    [string]$WorkDir = "",
    [int]$Seed = 0,
    [string]$BaseUrl = "http://127.0.0.1:9",  # död adress: anslutningen nekas direkt
    [string]$Scenario = "",                   # FOREVERSCOUT_STRESS_SCENARIO (se StressTest.cs)
    [switch]$Screenshots
)
$ErrorActionPreference = "Stop"
function Fmt([string]$f) { [string]::Format([Globalization.CultureInfo]::InvariantCulture, $f, [object[]]@($args | ForEach-Object { $_ })) }
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$windows = Split-Path -Parent $here
$fixtures = Join-Path (Split-Path -Parent $windows) "fixtures"
if (-not $Exe) { $Exe = Join-Path $windows "src\ForeverScout\bin\full\Debug\net10.0-windows\win-x64\ForeverScout.exe" }
if (-not (Test-Path $Exe)) { throw "Hittar inte $Exe - bygg först: dotnet build src\ForeverScout -c Debug" }
if (-not $WorkDir) { $WorkDir = Join-Path $env:TEMP ("fs-stress-" + (Get-Date -Format "yyyyMMdd-HHmmss")) }
if ($Seed -eq 0) { $Seed = Get-Random -Minimum 1 -Maximum 1000000 }

# ---------------------------------------------------------------- isolerad miljö
$data = Join-Path $WorkDir "data"
$game = Join-Path $WorkDir "World of Warcraft\_forever_"
$sv = Join-Path $game "WTF\Account\STRESSKONTO\SavedVariables"
$adb = Join-Path $game "Cache\ADB\enUS"
New-Item -ItemType Directory -Force $data, $sv, $adb, (Join-Path $game "Interface\AddOns") | Out-Null
Set-Content -Path (Join-Path $WorkDir "World of Warcraft\.build.info") -Value "stress" -Encoding ASCII

$scout = Join-Path $sv "ForeverScout.lua"
$auction = Join-Path $sv "Auctionator.lua"
$dbcache = Join-Path $adb "DBCache.bin"
$svBytes = [IO.File]::ReadAllBytes((Join-Path $fixtures "ForeverScout.example.lua"))
$aucBytes = [IO.File]::ReadAllBytes((Join-Path $fixtures "Auctionator.lua"))
[IO.File]::WriteAllBytes($scout, $svBytes)
# Stora filer som hos användaren (Auctionator ~19 MB, DBCache ~8 MB): utfyllnad med blanksteg efter tabellen.
$ms = New-Object IO.MemoryStream; $ms.Write($aucBytes, 0, $aucBytes.Length)
$spaces = [Text.Encoding]::ASCII.GetBytes(" " * 12MB); $ms.Write($spaces, 0, $spaces.Length); $aucBig = $ms.ToArray()
[IO.File]::WriteAllBytes($auction, $aucBig)
$rng = New-Object Random $Seed
$cache = New-Object byte[] (8MB); $rng.NextBytes($cache)
[IO.File]::WriteAllBytes($dbcache, $cache)

$settings = [ordered]@{
    GameDir = $game; GameDirConfirmed = $game; Language = "sv"; AutoUpload = $true; UploadHotfix = $true
    ShareAuctionPrices = $true; DontAskOnClose = $true; CloseAction = "tray"; TrayHintShown = $false
    ContributorName = "Stresstest"; LastRunVersion = "1.0.11"
}
$settings | ConvertTo-Json | Set-Content -Path (Join-Path $data "settings.json") -Encoding UTF8
Add-Type -AssemblyName System.Security
$key = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes("stress-test-key"),
    [Text.Encoding]::UTF8.GetBytes("ForeverScout.v1"), [Security.Cryptography.DataProtectionScope]::CurrentUser)
[IO.File]::WriteAllBytes((Join-Path $data "key.bin"), $key)

# ---------------------------------------------------------------- start
$env:FOREVERSCOUT_DATADIR = $data
$env:FOREVERSCOUT_BASEURL = $BaseUrl
$env:FOREVERSCOUT_STRESS_SEED = "$Seed"
$env:FOREVERSCOUT_STRESS_SCENARIO = $Scenario
if ($Screenshots) {
    $p = Start-Process -FilePath $Exe -ArgumentList "--screenshots" -PassThru
    if (-not $p.WaitForExit(180000)) { Stop-Process -Id $p.Id -Force; throw "--screenshots avslutades inte inom 3 min" }
    Copy-Item -Recurse -Force (Join-Path $data "screenshots") (Join-Path $WorkDir "screenshots")
    Get-ChildItem (Join-Path $WorkDir "screenshots") | ForEach-Object { Write-Host $_.FullName }
    exit $p.ExitCode
}
$csv = Join-Path $WorkDir "memory.csv"
"seconds,workingSetMB,privateBytesMB,workingSetPrivateMB,handles" | Set-Content $csv -Encoding ASCII
$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process -FilePath $Exe -ArgumentList "--stress=$Minutes" -PassThru -RedirectStandardError (Join-Path $WorkDir "stderr.txt") -RedirectStandardOutput (Join-Path $WorkDir "stdout.txt")
$null = $p.Handle   # behåll handtaget: annars blir ExitCode tom när processen avslutats
Write-Host "pid $($p.Id), seed $Seed, $Minutes min, arbetskatalog $WorkDir"

function Sample {
    $p.Refresh()
    $wsp = ""
    try {
        $c = Get-Counter -Counter "\Process(ForeverScout*)\ID Process", "\Process(ForeverScout*)\Working Set - Private" -ErrorAction Stop
        $inst = ($c.CounterSamples | Where-Object { $_.Path -like "*id process" -and [int]$_.CookedValue -eq $p.Id }).InstanceName
        if ($inst) { $wsp = Fmt "{0:0.0}" (($c.CounterSamples | Where-Object { $_.InstanceName -eq $inst -and $_.Path -like "*working set - private" }).CookedValue / 1MB) }
    } catch { }
    $line = Fmt "{0:0},{1:0.0},{2:0.0},{3},{4}" $sw.Elapsed.TotalSeconds, ($p.WorkingSet64 / 1MB), ($p.PrivateMemorySize64 / 1MB), $wsp, $p.HandleCount
    Add-Content $csv $line -Encoding ASCII
    Write-Host $line
}

# ---------------------------------------------------------------- filändringar + mätning
$deadline = $Minutes * 60 + 180     # appen avslutar sig själv efter $Minutes; 3 min marginal för pågående dialoger
$nextSample = 0; $nextChange = 2; $changes = 0
while (-not $p.HasExited -and $sw.Elapsed.TotalSeconds -lt $deadline) {
    $t = $sw.Elapsed.TotalSeconds
    if ($t -ge $nextSample) { Sample; $nextSample += 10 }
    if ($t -ge $nextChange) {
        try {
            switch ($rng.Next(4)) {
                0 { [IO.File]::WriteAllBytes($scout, $svBytes); $fs = [IO.File]::Open($scout, "Append", "Write", "ReadWrite"); $n = $rng.Next(1, 200); $fs.Write([byte[]](@(10) * $n), 0, $n); $fs.Close() }   # spelet sparar (/reload)
                1 { $rng.NextBytes($cache); [IO.File]::WriteAllBytes($dbcache, $cache) }                    # ny hotfix-cache
                2 { [IO.File]::WriteAllBytes($auction, $aucBig); $fs = [IO.File]::Open($auction, "Append", "Write", "ReadWrite"); $n = $rng.Next(1, 50); $fs.Write([byte[]](@(10) * $n), 0, $n); $fs.Close() }
                3 { $fs = [IO.File]::Open($scout, "Open", "ReadWrite", "ReadWrite"); $fs.SetLength([Math]::Max(0, $fs.Length - 10)); $fs.Close()
                    [IO.File]::WriteAllBytes($scout, $svBytes) }                                             # halvskriven fil, sedan hel
            }
            $changes++
        } catch { }   # filen kan vara öppen hos appen just då – som i spelet
        $nextChange = $t + 1 + $rng.NextDouble() * 3
    }
    Start-Sleep -Milliseconds 250
}
$elapsed = $sw.Elapsed.TotalSeconds
if (-not $p.HasExited) { Sample; Stop-Process -Id $p.Id -Force; $exit = "killed (hängde sig eller avslutade inte i tid)" } else { $exit = $p.ExitCode }

# ---------------------------------------------------------------- sammanfattning
$rows = Import-Csv $csv
$ws = $rows | ForEach-Object { [double]$_.workingSetMB }
$pr = $rows | ForEach-Object { [double]$_.privateBytesMB }
$half = [int]($rows.Count / 2)
$avg = { param($a) if ($a.Count) { ($a | Measure-Object -Average).Average } else { 0 } }
$stressLog = Join-Path $data "stress.log"
$done = if (Test-Path $stressLog) { Select-String -Path $stressLog -Pattern " DONE " | Select-Object -Last 1 } else { $null }
$exceptions = if (Test-Path $stressLog) { @(Select-String -Path $stressLog -Pattern "EXCEPTION|FATAL").Count } else { -1 }
$summary = @(
    (Fmt "seed=$Seed minutes=$Minutes elapsedSeconds={0:0} exit=$exit fileChanges=$changes samples=$($rows.Count)" $elapsed)
    (Fmt "workingSetMB min={0:0.0} max={1:0.0} firstHalfAvg={2:0.0} secondHalfAvg={3:0.0}" ($ws | Measure-Object -Minimum).Minimum, ($ws | Measure-Object -Maximum).Maximum, (& $avg $ws[0..($half - 1)]), (& $avg $ws[$half..($ws.Count - 1)]))
    (Fmt "privateMB    min={0:0.0} max={1:0.0} firstHalfAvg={2:0.0} secondHalfAvg={3:0.0}" ($pr | Measure-Object -Minimum).Minimum, ($pr | Measure-Object -Maximum).Maximum, (& $avg $pr[0..($half - 1)]), (& $avg $pr[$half..($pr.Count - 1)]))
    "exceptionsInStressLog=$exceptions"
    "app: $($done.Line)"
)
$summary | Set-Content (Join-Path $WorkDir "summary.txt") -Encoding UTF8
$summary | ForEach-Object { Write-Host $_ }
if ($exit -is [int] -and $exit -eq 0 -and $exceptions -eq 0) { exit 0 } else { exit 1 }
