# PhotographerEye

Portable przeglądarka zdjęć JPG/JPEG/PNG oraz RAW dla Windows (WPF, .NET 8, pojedynczy plik `.exe`), z korekcją tonów (cienie, gamma, rozciąganie histogramu) i zapisem zmodyfikowanych zdjęć.

## Funkcje

- Podgląd obrazów z folderu, nawigacja (strzałki, Home/End)
- Zoom 10–400%: rolka myszy, `+`/`-`, `1` — 100%, `0`/przycisk „Dopasuj" — dopasuj do okna, dwuklik — przełączanie fit/100%
- Pan: przeciąganie myszką (gdy obraz większy od okna); obraz zawsze wyśrodkowany i nieucięty
- Korekcja cieni −50%…+70% (podnoszenie i przyciemnianie; pipeline liniowy sRGB, miękka maska luminancji — styl Photoshop) i gamma 0.50–2.00 (LUT środkowych tonów)
- Przy regulacji cieni automatyczne rozciąganie histogramu narastające proporcjonalnie z siłą regulacji (pełne od ±50%): auto-contrast z percentyli 0.5% histogramu luminancji, jedna skala dla wszystkich kanałów — balans bieli nietknięty, czernie zakotwiczone, pomijane gdy zakres już pełny lub obraz płaski
- Porównanie z oryginałem: przytrzymaj przycisk „Porównaj" lub spację (podgląd bez korekt; puszczenie wraca)
- Podgląd plików RAW: RW2 (Panasonic), NEF/NRW (Nikon), ARW/SRF/SR2 (Sony), CR2/CR3/CRW (Canon), ORF (Olympus), RAF (Fuji), PEF (Pentax), DNG i inne — wbudowany LibRaw: demosaikacja, balans bieli z aparatu, auto-flip orientacji; dekodowanie w cache (podgląd 1/4 skali, pełna rozdzielczość przy przybliżeniu); zapis edycji jako `_edited.jpg`
- Panel korekt (pionowy pasek po prawej, przycisk suwaków lub klawisz `E`): **ekspozycja** (±2 EV, mnożenie liniowe z hard-clippingiem świateł), **kontrast** (±100: tanh S-krzywa / spłaszczenie), **balans bieli** (temperatura: wzmocnienie R/B, ±100) z **pipetą punktu bieli** (ikona palety przy suwaku — kliknij neutralny obszar zdjęcia, temperatura obliczana ze średniej 9×9 px, `Esc` anuluj), **nasycenie** (0–200 %, interpolacja do luminancji), **cienie** (zsynchronizowane z suwakiem Cienie na pasku głównym), **światła** (−100…+100: odzyskiwanie/przypalanie świateł maską luminancji nad progiem miękkim), **clarity** (±100: unsharp-mask na low-pass luminancji), **wyostrzenie** (0–100: unsharp-mask o małym promieniu po odszumianiu), **odszumianie** (0–100, mieszanina z rozmyciem dwuprzebiegowym), **redukcja szumu kolorów** (0–100: rozmycie chrominancji z zachowaniem luminancji); reset przy zmianie obrazu i przyciskiem ♻ w panelu; histogram pokazuje wynik; zapis uwzględnia panel w pełnej rozdzielczości
- Overlay histogramu (luminancja + nakładki R/G/B) liczony z finalnego podglądu; pojawia się przy ruchu suwaków korekt lub chwytaniu histogramu, chowany przy zmianie obrazu i krzyżykiem
- Ręczne rozciąganie histogramu: przeciągnij punkt na histogramie — część po jednej stronie punktu ściska się, po drugiej rozciąga (mapa odcinkowo-liniowa, końce 0 i 1 zakotwiczone, punkty zachowują monotoniczność); dodawanie kolejnych punktów kolejnymi chwytami; punkty usuwa: podwójny klik na histogramie, przycisk resetu (♻) przy krzyżyku overlayu, `R`/Reset
- Czarno-białe w fotograficznym stylu dokumentalnym: szybki przełącznik trzy-stanowy — przycisk na pasku lub klawisz `B`: kolor → **dokumentalny** (konwersja panchromatyczna z lekką przewagą czerwieni — jak filtr żółto-czerwony: jasna skóra, przyciemniony błękit + filmowa krzywa S: głębokie czernie, wyraziste środki, miękkie rolloff świecidełek) → **Tri-X 400** (emulacja: mocniejsza krzywa i ziarno filmowe o stałym wzorcu per obraz, widoczne głównie w półtonach); histogram pokazuje wynik; reset przy zmianie obrazu; zapis uwzględnia tryb
- Kompaktowy ciemny interfejs: jedna linia narzędzi od minimalnej szerokości okna (980 px)
- Własny pasek tytułowy: czarny, o obniżonej wysokości, z przyciskami minimalizacji/maksymalizacji/zamknięcia
- Dynamiczne dekodowanie zależne od zoomu (pełna jakość przy dużych powiększeniach), cache podglądów sąsiednich plików
- Strzałki przesuwania obrazów na lewym/prawym brzegu okna
- Symbol ustawień (⚙) w prawym górnym rogu → „O aplikacji…" (autor, e-mail, GitHub, licencja)
- Reset korekt automatycznie przy przełączeniu obrazu oraz ręcznie: `R`, przycisk „Reset", podwójny klik na suwak (lub na histogram — czyści punkty rozciągania)
- Zapis zmodyfikowanego zdjęcia (Ctrl+S lub przycisk zapisu na pasku) w rozdzielczości oryginału — JPG (jakość 92) lub PNG (RAW → JPG), plik obok oryginału z przyrostkiem `_edited`; przycisk aktywny tylko gdy któraś korekta (cienie, gamma, punkty rozciągania, tryb czarno-biały, panel korekt) jest aktywna
- Drag & drop pliku, otwieranie przez argument CLI, `Ctrl+O`
- Ustawienia zapisywane w `%AppData%\PhotographerEye\settings.json`

## Uruchamianie

Portable: `dist\PhotographerEye.exe [plik|folder]` — bez instalacji, .NET wbudowany w plik. Obsługuje też pliki RAW (RW2, NEF, ARW, CR2/CR3, ORF, RAF, DNG…) przez wbudowany LibRaw.

## Skróty klawiszowe

| Klucz | Akcja |
|---|---|
| ←/→, PgUp/PgDn | następny/poprzedni obraz |
| Home/End | pierwszy/ostatni obraz |
| rolka myszy | zoom (z punktem pod kursorem) |
| `Shift`+rolka | przesuwanie poziome |
| `1` / `0` | 100% / dopasuj |
| `R` | reset korekt |
| `E` | panel korekt: ekspozycja, kontrast, balans bieli, nasycenie, clarity, dehaze, odszumianie |
| `B` | czarno-białe: kolor → dokumentalny → Tri-X 400 (pętla) |
| spacja (przytrzymaj) | podgląd oryginału |
| `Ctrl+O` | otwórz plik |
| `Ctrl+S` | zapis zmodyfikowanego zdjęcia |

## Budowanie

```
dotnet build ImgViewer.csproj -c Release
dotnet publish ImgViewer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

## Testy

Harness logiki (zoom/pan/fit, DPI, korekty, histogram, RAW, panel korekt):

```
dotnet run --project tools\Harness\tools.csproj -c Release
```

Tryb diagnostyczny auto-stretchu (percentyle kanałów, progi luminancji, wyjście po regulacji):

```
dotnet run --project tools\Harness\tools.csproj -c Release -- obraz1.jpg obraz2.jpg
```

E2E UIA (wymaga uruchomionej aplikacji): `scripts\uia-e2e.ps1 <pid>`

## Struktura

- `MainWindow.xaml(.cs)` — toolbar, pasek tytułowy, nawigacja, zapis, odświeżanie widoku, ustawienia
- `Controls/ImageViewer.cs` — zoom/pan/fit (renderowanie layoutowe, bez transformacji macierzowej)
- `Controls/HistogramOverlay.cs` — histogram w prawym górnym rogu + interaktywne punkty rozciągania
- `Services/ImageLoader.cs` — dekodowanie, DPI 96, orientacja EXIF; delegacja RAW do `Imaging/RawDecoder.cs` (LibRaw: dekodowanie, cache podglądów, zapis `_edited.jpg`)
- `Services/AdjustmentsProcessor.cs` — cienie (pipeline liniowy) + gamma + auto-stretch + ręczna mapa rozciągania + histogram
- `Imaging/ManualStretch.cs` — mapa odcinkowo-liniowa punktów rozciągania
- `Services/ImageFolder.cs`, `Services/SettingsStore.cs` — lista plików, ustawienia
- `tools/Harness/` — testy logiki
- `scripts/` — testy E2E UIA i pomoce

## Debugowanie

Ustaw `PHOTOGRAPHEREYE_DEBUG=1`, log: `%TEMP%\photographereye-debug.log`.

## Licencja

Użycie prywatne, niekomercyjne. Zabroniona modyfikacja i redystrybucja — pełne warunki: [LICENSE](LICENSE).