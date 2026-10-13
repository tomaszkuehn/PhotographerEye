# PhotographerEye

Portable przeglądarka zdjęć JPG/JPEG/PNG oraz RAW dla Windows (WPF, .NET 8, pojedynczy plik `.exe`), z korekcją tonów (cienie, gamma, rozciąganie histogramu) i zapisem zmodyfikowanych zdjęć.

## Funkcje

- Podgląd obrazów z folderu, nawigacja (strzałki, Home/End)
- Zoom 10–400%: rolka myszy, `+`/`-`, `1` — 100%, `0`/przycisk „Dopasuj" — dopasuj do okna, dwuklik — przełączanie fit/100%
- Pan: przeciąganie myszką (gdy obraz większy od okna); obraz zawsze wyśrodkowany i nieucięty
- Korekcja cieni −100…+100 (maska tonalna nad czernią, podnosi głębokie cienie i oszczędza półtony; pipeline liniowy sRGB) i gamma 0.50–2.00 (LUT środkowych tonów)
- Suwaki w stylu Photoshopa: klik na torze ustawia wartość w klikniętym miejscu; przeciąganie **etykiety lub liczby** obok suwaka reguluje precyzyjnie (Shift ×10 szybciej, Ctrl/Alt ×0,1 dokładniej); **dwuklik liczby** pozwala wpisać dokładną wartość; strzałki — mały krok, Shift+strzałki — duży, Ctrl+strzałki — najmniejszy (0,01 EV); dwuklik na torze lub przycisk ♻ resetuje korektę
- Porównanie z oryginałem: przytrzymaj przycisk „Porównaj" lub spację (podgląd bez korekt; puszczenie wraca)
- Podgląd plików RAW: RW2 (Panasonic), NEF/NRW (Nikon), ARW/SRF/SR2 (Sony), CR2/CR3/CRW (Canon), ORF (Olympus), RAF (Fuji), PEF (Pentax), DNG i inne — wbudowany LibRaw: demosaikacja, balans bieli z aparatu, auto-flip orientacji; **rekonstrukcja prześwietleń** (tryb mieszany LibRaw + neutralizacja odcięcia: spalone kanały są odbudowywane, a piksele przy odcięciu odbarwiane do bieli — prześwietlone tło nie wpada w magenta/fiolet), dekodowanie **16 bit na kanał** (48 bpp — natywna głębia czujnika 12/14 bit przeskalowana do pełnego zakresu 0–65535, bez utraty stopni), podgląd ekranowy z ditheringiem Bayera 4×4; **podgląd i wszystkie korekty działają zawsze na pełnej rozdzielczości** (wyświetlanie skalowane w oknie); zapis edycji przez dialog (JPG 90% / TIFF 16-bit) w pełnej rozdzielczości RAW
- Panel korekt (pionowy pasek po prawej, przycisk suwaków lub klawisz `E`) — model tonalny w **scenie liniowej**: obraz RAW pracuje w **scenie liniowej** (LibRaw z wyłączoną gamma), proces bazowy to tonemap **rozszerzony Reinhard** na luminancji (LUT EV −14…+10 wokół szarości 0.18; whites przesuwają punkt bieli, blacks = miękkie kolano/podniesienie czerni, kontrast = nachylenie w EV) z kodowaniem sRGB na końcu; dla JPG/PNG S-krzywa w domenie gamma 2.2 (identity przy neutralnych); **ekspozycja** (±5 EV) jako gain `2^EV` przed regionami, **światła/cienie** jako delta gainy w log-luminancji (maski smoothstep w EV wokół szarości: cienie poniżej +0.3 EV, światła powyżej −1 EV, współczynniki 1.7); prześwietlenia przy tonemapie są desaturowane do luminancji (bez magenty); **balans bieli** (temperatura ±100, symetryczne R/B w liniowym) z **pipetą punktu bieli**, **clarity** (±100: lokalny kontrast duży-promieniowy z wagą półtonów), **nasycenie** (±100: 0 neutralne, −100 cz.-b., po tonemapie), **wyostrzenie** (0–150), **dehaze** (±100 na wartościach liniowych), **odszumianie** i **redukcja szumu kolorów** (0–100); pliki `.eye` z wersji 0.93 (stara skala nasycenia 0–200%) konwertowane automatycznie
- Przyspieszanie korekt na GPU: operacje edycji RAW (odszumianie, szum kolorów, clarity, wyostrzenie, światła, cienie, balans bieli/kontrast/nasycenie, krzywe) liczone na karcie graficznej przez DirectCompute (ComputeSharp) — wynik niemal identyczny z CPU (średnia różnica ~0.03 na 65535), kilkukrotnie szybciej na ciężkim zestawie korekt. Sprzętowe GPU wykrywane przy starcie (preferowana karta dyskretna o największej pamięci); przy braku GPU — lub po ustawieniu `PHOTOGRAPHEREYE_GPU=0` — przetwarzanie odbywa się wyłącznie na CPU (automatyczny fallback). Status akceleracji pokazany w „O aplikacji…".
- Overlay histogramu (luminancja + nakładki R/G/B) liczony z finalnego podglądu; pokazywany tylko przy otwartym panelu Adjustments (otwarciu towarzyszy świeży przelicz), chowany przy zmianie obrazu; skala znormalizowana do 99. percentyla zliczeń binów (piki clippingu przycinane przy górze, reszta pozostaje czytelna), wypełnienia kanałów domykane wzdłuż podstawy
- Ręczne rozciąganie histogramu: przeciągnij punkt na histogramie — część po jednej stronie punktu ściska się, po drugiej rozciąga; punkty są **zakotwiczone w EV sceny** (log-luminancja względem szarości 0.18) i wchodzą do LUT tonemap **przed kodowaniem sRGB**; mapy odcinkowo-liniowe w EV z zakotwiczonymi końcami zakresu, punkty zachowują monotoniczność; dodawanie kolejnych punktów kolejnymi chwytami; punkty usuwa: podwójny klik na histogramie, przycisk resetu (♻) przy krzyżyku overlayu, `R`/Reset. Przełącznik (⤮) przy krzyżyku wybiera tryb: **przekładanie na suwaki** (domyślnie — koniec przeciągnięcia przelicza knot na wartości Exposure/Highlights/Shadows/Whites/Blacks wg EV knotu i czyści punkty, kolejne przeciągnięcia kumulują się w suwakach) albo **niezależna krzywa** (knoty zostają krzywą EV i wchodzą do LUT tonemap przed kodowaniem sRGB, więc suwaki działają spójnie pod nią — ekspozycja +1 EV przesuwa scenę o dokładnie 1 EV pod niezmienioną krzywą)
- Czarno-białe w fotograficznym stylu dokumentalnym: szybki przełącznik trzy-stanowy — przycisk na pasku lub klawisz `B`: kolor → **dokumentalny** (konwersja panchromatyczna z lekką przewagą czerwieni — jak filtr żółto-czerwony: jasna skóra, przyciemniony błękit + filmowa krzywa S: głębokie czernie, wyraziste środki, miękkie rolloff świecidełek) → **Tri-X 400** (emulacja: mocniejsza krzywa i ziarno filmowe o stałym wzorcu per obraz, widoczne głównie w półtonach); histogram pokazuje wynik; reset przy zmianie obrazu; zapis uwzględnia tryb
- Kompaktowy ciemny interfejs: jedna linia narzędzi od minimalnej szerokości okna (980 px)
- Własny pasek tytułowy: czarny, o obniżonej wysokości, z przyciskami minimalizacji/maksymalizacji/zamknięcia
- Dekodowanie zawsze w pełnej rozdzielczości (JPG/PNG i RAW); gotowe obrazy trzymane w krótkim cache, skalowanie wyłącznie przy wyświetlaniu
- Strzałki przesuwania obrazów na lewym/prawym brzegu okna
- Symbol ustawień (⚙) w prawym górnym rogu → „O aplikacji…" (autor, e-mail, GitHub, licencja)
- Reset korekt automatycznie przy przełączeniu obrazu oraz ręcznie: `R`, przycisk „Reset", podwójny klik na suwak (lub na histogram — czyści punkty rozciągania)
- Pamięć korekt w sesji: ustawienia suwaków są zapamiętywane per zdjęcie podczas przeglądania folderu — cofnięcie się do zdjęcia przywraca suwaki i renduje je z powrotem (stan bieżącej sesji wygrywa z zapisanym plikiem `.eye`; `.eye` pozostaje trwałym zapisem, tworzonym przy zapisie zdjęcia)
- Zapis zmodyfikowanego zdjęcia (Ctrl+S lub przycisk zapisu na pasku) przez dialog **Zapisz zdjęcie** — wybór lokalizacji i nazwy (podpowiedź `<obecna nazwa>_eye`, wybrany format dodaje rozszerzenie) oraz formatu: **JPG (jakość 90%)** lub **TIFF 16-bit** (bezstratny 48 bpp RGB, realne wartości 16-bit z dekodowania RAW — bez kwantyzacji do 8 bit, bez kompresji — czytniki: WIC/Photoshop/GIMP); zapis w pełnej rozdzielczości źródła z włączonymi korektami (cienie, gamma, punkty rozciągania, tryb czarno-biały, panel korekt; JPG/PNG z rozmiaru zdekodowanego, RAW z natywnych wymiarów czujnika, np. 4624×3088 z RW2); przy obrazie powstaje plik-odcisk `<nazwa>.eye` z parametrami korekt, wczytywany automatycznie przy ponownym otwarciu; przycisk aktywny tylko gdy któraś korekta jest aktywna
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
- `Controls/PhotoshopSlider.cs` — zachowanie suwaków jak w Photoshopie (klik na torze, precyzyjne przeciąganie etykiety/liczby z modyfikatorami, wpisywanie wartości, kroki strzałkami)
- `Imaging/RawDecoder.cs` — dekod RAW (LibRaw: 16 bit/kanał **liniowo** (wyłączona gamma LibRaw, `-g 1 1` w fallbacku), mieszany tryb prześwietleń, balans z aparatu, cache par 16-bit + podgląd Bgra32; podgląd startowy = neutralny proces bazowy: tonemap Reinhard + kodowanie sRGB, bez autoekspozycji); gdy dekod wewnętrzny zawiedzie lub da śmieci — automatyczny fallback na dołączone narzędzia wiersza poleceń: `thirdparty/libraw22/dcraw_emu.exe` (LibRaw 0.22.2; obsługa m.in. zwykłych NEF z Nikon Z6 III) i `thirdparty/librawhe/dcraw_emu.exe` (fork LibRaw z dekoderem Nikon NEF High Efficiency = TicoRAW/JPEG-XS; dekoduje wariant HE, wariant HE* pozostaje nieobsługiwany — zamiast śmieci aplikacja pokazuje czytelny komunikat)
- `Services/AdjustmentsProcessor.cs` — pipeline 8-bit dla JPG/PNG: kolejność operacji deweloperskich (LUT balansu bieli+dehaze → regiony EV → clarity/wyostrzenie → tonemap luminancji z ekspozycją) + gamma, ręczna mapa rozciągania, histogram
- `Imaging/Adjust16.cs` — pipeline RAW na 16 bit/kanał w domenie **sceny liniowej** (LUT balansu bieli+dehaze → regiony świateł/cieni w EV na log-luminancji → clarity/wyostrzenie → tonemap scenowa Reinhard z ekspozycją i anty-desaturacją prześwietleń → saturacja → kodowanie sRGB → gamma/ręczna mapa/mono/grain → histogram)
- `Imaging/Adjust16.Gpu.cs` — backend GPU (ComputeSharp/DirectCompute) dla `Adjust16`: detekcja sprzętowego GPU (preferencja karty dyskretnej), shadery operacji edycji RAW (w tym ToneKernel — LUT EV), automatyczny fallback do CPU gdy brak GPU lub błąd
- `Imaging/LinearRender.cs` — liniowe wzmocnienia RAW: ekspozycja w EV + temperatura jako zwykłe mnożenie w przestrzeni sceny liniowej (przed regionami i tonemapą); LUT liniowe→sRGB do kodowania wyświetlania
- `Imaging/Raw16.cs` — obraz planarny RGB 16-bit, deinterleaving, kwantyzacja z ditheringiem Bayera 4×4, interleaving dla TIFF
- `Services/EyeFile.cs` — odcisk korekt `.eye` (JSON) obok źródła, auto-aplikacja przy otwarciu; punkty rozciągania zapisywane w EV (`.eye` ze starymi punktami w ułamkach wyświetlania konwertowane przy wczytaniu)
- `Imaging/PhotoOps.cs` — regionowe maski EV cieni/świateł (log-luminancja sceny), LUT-y balansu bieli/dehaze, tonemap luminancji (scenowa Reinhard dla RAW — zawsze aktywna jako proces bazowy, display S-curve gamma 2.2 dla JPG), saturacja, clarity/wyostrzenie (unsharp), odszumianie i szum kolorów, pomiar temperatury z próbki
- `Imaging/ManualStretch.cs` — mapa punktów rozciągania w EV sceny (składana do LUT tonemap przed kodowaniem sRGB)
- `Services/ImageFolder.cs`, `Services/SettingsStore.cs` — lista plików, ustawienia
- `tools/Harness/` — testy logiki
- `scripts/` — testy E2E UIA i pomoce

## Debugowanie

Ustaw `PHOTOGRAPHEREYE_DEBUG=1`, log: `%TEMP%\photographereye-debug.log`.

## Licencja

Użycie prywatne, niekomercyjne. Zabroniona modyfikacja i redystrybucja — pełne warunki: [LICENSE](LICENSE).