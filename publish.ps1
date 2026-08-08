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
    [switch]$SkipBuild
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
if (git tag --list $tag) { throw "Tag $tag už existuje. Zvyš ApplicationDisplayVersion v csproj." }

$keystore = Join-Path $PSScriptRoot 'moonapp.keystore'
if (-not (Test-Path $keystore)) { throw "Chybí $keystore." }
$pass = $env:MOONAPP_KEYSTORE_PASS
if (-not $pass) {
    $sec = Read-Host "Heslo ke keystore" -AsSecureString
    $pass = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))
}

$outDir = Join-Path $PSScriptRoot 'artifacts'
$apk = Join-Path $outDir "MoonApp-$version-arm64.apk"

if (-not $SkipBuild) {
    Write-Host "Sestavuji release APK…" -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    # jen arm64: jiné ABI dnešní telefony nepotřebují a APK by bylo dvojnásobné
    dotnet publish $csproj -f net10.0-android -c Release `
        -p:RuntimeIdentifier=android-arm64 `
        -p:AndroidKeyStore=true `
        -p:AndroidSigningKeyStore=$keystore `
        -p:AndroidSigningKeyAlias=moonapp `
        -p:AndroidSigningKeyPass=$pass `
        -p:AndroidSigningStorePass=$pass `
        -p:PublishDir=$outDir
    if ($LASTEXITCODE -ne 0) { throw "Build selhal." }

    $signed = Get-ChildItem $outDir -Filter '*-Signed.apk' -Recurse |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $signed) { throw "Podepsané APK se nenašlo v $outDir." }
    Copy-Item $signed.FullName $apk -Force
}

if (-not (Test-Path $apk)) { throw "Chybí $apk (spusť bez -SkipBuild)." }
$sizeMb = [math]::Round((Get-Item $apk).Length / 1MB, 1)
Write-Host "APK: $apk ($sizeMb MB)" -ForegroundColor Green

$body = if ($Notes) { "$Notes`n`nInstalace: stáhni MoonApp-$version-arm64.apk (Android arm64)." }
        else { "Instalace: stáhni MoonApp-$version-arm64.apk (Android arm64)." }

if ($PSCmdlet.ShouldProcess("GitHub Release $tag", "vytvořit a nahrát APK")) {
    git tag $tag
    git push origin $tag
    gh release create $tag $apk --title "MoonApp $version" --notes $body
    Write-Host "Vydáno: $tag" -ForegroundColor Green
    Write-Host "Appka nabídne aktualizaci při příštím spuštění (UpdateService)." -ForegroundColor Green
}
