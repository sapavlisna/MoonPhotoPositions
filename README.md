# MoonApp.Maui — nativní Android appka (C# / .NET MAUI)

Samostatná mobilní aplikace plánovače focení Měsíce + AR, **běží celá na zařízení, bez serveru**.
Výškopis se tahá přímo z veřejné ČÚZK ImageServer a cachuje na telefonu (offline po stažení oblasti).
Sesterský `../server/` (Python FastAPI + web) je **referenční implementace / specifikace algoritmů**
a zdroj hodnot pro paritní testy. Žádný JavaScript ani recyklace webového kódu.

Plán: `C:\Users\Pavel\.claude\plans\glowing-giggling-bumblebee.md`.

## Struktura
```
MoonApp.slnx
  MoonApp.Core/         čistá .NET knihovna (net10.0) — veškerá logika, testovatelná bez UI
    Geo.cs              projekce WGS84⇄EPSG:5514 (DotSpatial) + sférické azimuty/vzdálenosti
    Astro.cs            poloha Měsíce (CoordinateSharp) — track/events
    (další: Cuzk, Dsm, Raycast, Coverage, Ar)
  MoonApp.Core.Tests/   xUnit — paritní testy proti Python backendu
  (později: MoonApp.Maui/ — UI: Mapsui mapa + SkiaSharp + AR)
```

## Stav
- **F0–F3 hotovo a ověřeno na emulátoru.** Core: 18 paritních testů proti Python backendu zelených.
  - `Geo` — 4326⇄5514 (shoda s pyproj ~3 m, round-trip < cm), pravý azimut/vzdálenost/cíl.
  - `Astro` — az/alt Měsíce (CoordinateSharp, shoda se skyfield az<0,6°/alt<1°).
  - `Cuzk`+`Dsm` — ČÚZK F32 exportImage (HttpClient + LibTiff) + bilineární vzorkování + cache.
  - `Raycast` — drop/snap/area/horizont/LOS; `Coverage` — mřížka + LOS (Parallel.For); `Planner` —
    stanoviště (LOS, horizont, dráha, „na špici"); `Ar` — projekce az/alt→obrazovka.
  - **MAUI UI:** mapa Mapsui/OSM → výběr objektu (snap) → coverage overlay (SkiaSharp PNG) →
    výběr stanoviště → horizont (SkiaSharp) + dráha Měsíce + „Měsíc na špici v HH:MM".
  - **AR:** OrientationSensor + Geolocation + projekce + kalibrace na Měsíc (překryv).
  - **APK:** debug-signed, sideloadovatelné (`bin/Debug/net10.0-android/*-Signed.apk`).
- **Zbývá (polish):** živá kamera v AR (bump MAUI workloadu na 10.0.60 + CommunityToolkit.Maui.Camera),
  ČÚZK ortofoto WMS vrstva, offline dlaždice, release-signed APK, validace AR na telefonu.

## Závislosti (NuGet)
Core: `CoordinateSharp`, `DotSpatial.Projections`, `BitMiracle.LibTiff.NET`.
UI: `Mapsui.Maui` + `Mapsui.Tiling` + `Mapsui.Nts` + `SkiaSharp` (+ MAUI senzory/poloha vestavěné).

## Build & test
```
dotnet test mobile/MoonApp.Core.Tests/MoonApp.Core.Tests.csproj
```
Core se staví jen s .NET SDK (10.0.300) — **netřeba MAUI workload ani Android SDK**. Ty jsou
potřeba až pro `MoonApp.Maui` UI a APK (`dotnet workload install maui`, Android SDK, JDK 17).

## Další kroky (dle plánu)
- **F1 — terén & coverage v C#** (v Core, stále bez MAUI): `Cuzk` (HttpClient → ČÚZK `exportImage`
  F32 + `identify`, cache), `Dsm` (bilineární vzorkování), `Raycast` (`drop`, horizont, LOS, snap,
  area_stats), `Coverage` (mřížka + vektorové LOS přes `Parallel.For`). Paritní testy proti
  `/coverage`,`/snap`,`/viewpoint`,`/horizon` z `../server`.
- **F2 — UI (MAUI):** Mapsui mapa (OSM + ČÚZK ortofoto WMS), výběr objektu, coverage overlay,
  horizont (SkiaSharp), slider; pak **AR** (kamera + Compass/OrientationSensor + projekce + kalibrace).
- **F3 — offline + podepsané APK** (sideload).

## ČÚZK API (z telefonu, HttpClient — bez CORS)
```
exportImage: https://ags.cuzk.gov.cz/arcgis/rest/services/3D/{dmp|dmr5g}/ImageServer/exportImage
  ?f=image&format=tiff&bbox={xmin},{ymin},{xmax},{ymax}&bboxSR=5514&imageSR=5514
  &size={w},{h}&pixelType=F32&interpolation=RSP_BilinearInterpolation        → GeoTIFF F32
identify:    .../3D/{dmp|dmr5g}/ImageServer/identify?f=json&geometryType=esriGeometryPoint
  &geometry={"x":lon,"y":lat,"spatialReference":{"wkid":4326}}&returnGeometry=false
```
