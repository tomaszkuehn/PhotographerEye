# ImgViewer

Portable przeglądarka obrazów JPG/JPEG/PNG dla Windows (WPF, .NET 8, pojedynczy plik `.exe`), z korekcją cieni i gamma.

## Funkcje

- Podgląd obrazów z folderu, nawigacja (`‹`/`›`, strzałki, Home/End)
- Zoom 10–400%: `Ctrl+rolka`, `+`/`-`, `1` — 100%, `0`/przycisk „Dopasuj" — dopasuj do okna, dwuklik — przełączanie fit/100%
- Pan: przeciąganie myszką (gdy obraz większy od okna); obraz zawsze wyśrodkowany i nieucięty
- Korekcja cieni (pipeline liniowy sRGB, miękka maska luminancji — styl Photoshop) i gamma (LUT środkowych tonów)
- Overlay histogramu (luminancja + nakładki R/G/B) liczony z finalnego podglądu
- Dynamiczne dekodowanie zależne od zoomu (pełna jakość przy dużych powiększeniach), cache podglądów sąsiednich plików
- Reset korekt: `R`, przycisk „Reset", podwójny klik na suwak
- Drag & drop pliku, otwieranie przez argument CLI, `Ctrl+O`
- Ustawienia zapisywane w `%AppData%\ImgViewer\settings.json`

## Uruchamianie

Portable: `dist\ImgViewer.exe [plik|folder]` — bez instalacji, .NET wbudowany w plik.

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