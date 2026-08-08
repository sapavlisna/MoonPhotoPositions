<#
.SYNOPSIS
    Sestaví podepsané APK a vydá ho jako GitHub Release.

.DESCRIPTION
    Vydání dosud probíhalo ručně a nikde nebylo zapsané, takže nešlo spolehlivě zopakovat.
    Skript drží tvar, který očekává UpdateService.cs: tag "vX.Y.Z" a v assets APK, jehož
    jméno končí na .apk. Verze se bere z csproj, aby se tag a ApplicationDisplayVersion
    nemohly rozejít.

    Heslo ke keystore se nikdy nezapisuje do repa — čte se z proměnné MOONAPP_KEYSTORE_PASS,
    nebo se zeptá.

.EXAMPLE
    $env:MOONAPP_KEYSTORE_PASS = '...'
    .\publish.ps1 -Notes "Slunce, body v okolí, panorama."

.EXAMPLE
    .\publish.ps1 -WhatIf      # jen sestaví a ukáže, co by vydal
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    # Text novinek do popisu release; bez něj se doplní jen odkaz na instalaci.
    [string]$Notes = "",
    # Přeskočí build a použije už hotové APK (ladění samotného vydání).
    [switch]$SkipBuild,
    # Podepíše klíčem z moonapp.keystore místo ladicího. POZOR: mění podpis proti dosud
    # vydaným verzím, takže se nová verze nedá nainstalovat přes starou — jen po odinstalaci.
    [switch]$UseKeystore
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$csproj = 'MoonApp.Maui/MoonApp.Maui.csproj'
[xml]$xml = Get-Content $csproj
$version = ($xml.Project.PropertyGroup | Where-Object { $_.ApplicationDisplayVersion }).ApplicationDisplayVersion
if (-not $version) { throw "V $csproj chybí ApplicationDisplayVersion." }
$tag = "v$version"
Write-Host "Verze $version (tag $tag)" -ForegroundColor Cyan

if (git status --porcelain) { Write-Warning "Pracovní strom není čistý — vydává se stav z posledního commitu." }
# Hotové vydání se nepřepisuje, ale nedokončené (tag je, release ne — třeba když minulý
# běh spadl na push) musí jít dokončit, ne nutit ke zvýšení verze.
cmd /c "gh release view $tag -R sapavlisna/MoonPhotoPositions >nul 2>&1"
if ($LASTEXITCODE -eq 0) { throw "Release $tag už existuje. Zvyš ApplicationDisplayVersion v csproj." }
if (git tag --list $tag) { Write-Host "Tag $tag už existuje — dokončuji vydání." -ForegroundColor Yellow }

# Dosud vydané verze (včetně 1.5.1) jsou podepsané ladicím klíčem Androidu — ověřeno
# otiskem certifikátu staženého APK. Android nedovolí přepsat instalaci jiným podpisem,
# takže výchozí je držet tentýž klíč; přechod na moonapp.keystore je jednosměrný krok,
# který si uživatelé zaplatí odinstalací, a musí se tedy vyžádat výslovně.
$signArgs = @()
if ($UseKeystore) {
    $keystore = Join-Path $PSScriptRoot 'moonapp.keystore'
    if (-not (Test-Path $keystore)) { throw "Chybí $keystore." }
    $pass = $env:MOONAPP_KEYSTORE_PASS
    if (-not $pass) {
        $sec = Read-Host "Heslo ke keystore" -AsSecureString
        $pass = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
            [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))
    }
    $signArgs = @(
        '-p:AndroidKeyStore=true',
        "-p:AndroidSigningKeyStore=$keystore",
        '-p:AndroidSigningKeyAlias=moonapp',
        "-p:AndroidSigningKeyPass=$pass",
        "-p:AndroidSigningStorePass=$pass")
    Write-Warning "Podepisuje se moonapp.keystore — nová verze NEPŮJDE nainstalovat přes stávající."
}

$outDir = Join-Path $PSScriptRoot 'artifacts'
$apk = Join-Path $outDir "MoonApp-$version-arm64.apk"

if (-not $SkipBuild) {
    Write-Host "Sestavuji release APK…" -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    # jen arm64: jiné ABI dnešní telefony nepotřebují a APK by bylo dvojnásobné
    dotnet publish $csproj -f net10.0-android -c Release -p:RuntimeIdentifier=android-arm64 @signArgs
    if ($LASTEXITCODE -ne 0) { throw "Build selhal." }

    $signed = Get-ChildItem 'MoonApp.Maui/bin/Release' -Filter '*-Signed.apk' -Recurse |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $signed) { throw "Podepsané APK se nenašlo." }
    Copy-Item $signed.FullName $apk -Force
}

if (-not (Test-Path $apk)) { throw "Chybí $apk (spusť bez -SkipBuild)." }
$sizeMb = [math]::Round((Get-Item $apk).Length / 1MB, 1)
Write-Host "APK: $apk ($sizeMb MB)" -ForegroundColor Green

# Podpis se musí shodovat s tím, co je nainstalované na telefonech, jinak aktualizace
# spadne až u uživatele. Otisk se proto vypíše, ať jde porovnat s předchozím vydáním.
$cert = (& keytool -printcert -jarfile $apk 2>&1 | Select-String 'SHA256:' | Select-Object -First 1)
Write-Host "Podpis: $($cert -replace '\s+', ' ')" -ForegroundColor Cyan

$body = if ($Notes) { "$Notes`n`nInstalace: stáhni MoonApp-$version-arm64.apk (Android arm64)." }
        else { "Instalace: stáhni MoonApp-$version-arm64.apk (Android arm64)." }

if ($PSCmdlet.ShouldProcess("GitHub Release $tag", "vytvořit a nahrát APK")) {
    # git i gh píšou průběh na stderr; s ErrorActionPreference=Stop by to PowerShell 5.1
    # vyhodnotil jako chybu a vydání by spadlo uprostřed, ačkoli všechno proběhlo
    if (-not (git tag --list $tag)) { git tag $tag }
    cmd /c "git push origin $tag 2>&1"
    if ($LASTEXITCODE -ne 0) { throw "Push tagu selhal." }
    cmd /c "gh release create $tag `"$apk`" --title `"MoonApp $version`" --notes `"$body`" 2>&1"
    if ($LASTEXITCODE -ne 0) { throw "Vytvoření release selhalo." }
    Write-Host "Vydáno: $tag" -ForegroundColor Green
    Write-Host "Appka nabídne aktualizaci při příštím spuštění (UpdateService)." -ForegroundColor Green
}
