# ImgViewer

Portable przeglądarka obrazów JPG/JPEG/PNG dla Windows (WPF, .NET 8, pojedynczy plik `.exe`), z korekcją cieni i gamma.

## Funkcje

- Podgląd obrazów z folderu, nawigacja (`‹`/`›`, strzałki, Home/End)
- Zoom 10–400%: `Ctrl+rolka`, `+`/`-`, `1` — 100%, `0`/przycisk „Dopasuj" — dopasuj do okna, dwuklik — przełączanie fit/100%
- Pan: przeciąganie myszką (gdy obraz większy od okna); obraz zawsze wyśrodkowany i nieucięty
- Korekcja cieni −50%…+70% (podnoszenie i przyciemnianie; pipeline liniowy sRGB, miękka maska luminancji — styl Photoshop) i gamma 0.50–2.00 (LUT środkowych tonów)
- Przy regulacji cieni automatyczne rozciąganie histogramu (auto-contrast: percentyle 0.5% z histogramu luminancji, jedna skala dla wszystkich kanałów — balans bieli nietknięty, czernie zakotwiczone, pomijane gdy zakres już pełny lub obraz płaski)
- Porównanie z oryginałem: przytrzymaj przycisk „Porównaj" lub spację (podgląd bez korekt; puszczenie wraca)
- Overlay histogramu (luminancja + nakładki R/G/B) liczony z finalnego podglądu; pojawia się przy ruchu suwaków korekt, zamykany krzyżykiem; trwa przy zmianie obrazu
- Kompaktowy ciemny interfejs: jedna linia narzędzi od minimalnej szerokości okna (980 px)
- Dynamiczne dekodowanie zależne od zoomu (pełna jakość przy dużych powiększeniach), cache podglądów sąsiednich plików
- Strzałki przesuwania obrazów na lewym/prawym brzegu okna
- Reset korekt automatycznie przy przełączeniu obrazu oraz ręcznie: `R`, przycisk „Reset", podwójny klik na suwak
- Drag & drop pliku, otwieranie przez argument CLI, `Ctrl+O`
- Ustawienia zapisywane w `%AppData%\ImgViewer\settings.json`

## Uruchamianie

Portable: `dist\ImgViewer.exe [plik|folder]` — bez instalacji, .NET wbudowany w plik.

## Skróty klawiszowe

| Klucz | Akcja |
|---|---|
| `‹`/`›`, ←/→, PgUp/PgDn | następny/poprzedni obraz |
| Home/End | pierwszy/ostatni obraz |
| `Ctrl+rolka`, `+`/`-` | zoom |
| `1` / `0` | 100% / dopasuj |
| `R` | reset korekt |
| spacja (przytrzymaj) | podgląd oryginału |
| `Ctrl+O` | otwórz plik |

## Budowanie

```
dotnet build ImgViewer.csproj -c Release
dotnet publish ImgViewer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

## Testy

Harness logiki (zoom/pan/fit, DPI, korekty, histogram):

```
dotnet run --project tools\Harness\tools.csproj -c Release
```

E2E UIA (wymaga uruchomionej aplikacji): `scripts\uia-e2e.ps1 <pid>`

## Struktura

- `MainWindow.xaml(.cs)` — toolbar, oświecanie widoku, ustawienia
- `Controls/ImageViewer.cs` — zoom/pan/fit (renderowanie layoutowe, bez transformacji macierzowej)
- `Controls/HistogramOverlay.cs` — histogram w prawym górnym rogu
- `Services/ImageLoader.cs` — dekodowanie, DPI 96, orientacja EXIF
- `Services/AdjustmentsProcessor.cs` — cienie (pipeline liniowy) + gamma + histogram
- `Services/ImageFolder.cs`, `Services/SettingsStore.cs` — lista plików, ustawienia
- `tools/Harness/` — testy logiki
- `scripts/` — testy E2E UIA i pomoce

## Debugowanie

Ustaw `IMGVIEWER_DEBUG=1`, log: `%TEMP%\imgviewer-debug.log`.