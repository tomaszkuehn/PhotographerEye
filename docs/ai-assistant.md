# Asystent AI (Ollama): dokument projektowy

Status: etapy 1–4 są zaimplementowane. Etap 5 (testy z prawdziwymi modelami) jest otwarty.

## 1. Cel

Użytkownik nie musi ręcznie ustawiać suwaków. Zamiast tego rozmawia z asystentem i wydaje polecenia, np.:

- „przygotuj to zdjęcie do albumu rodzinnego”,
- „przygotuj to zdjęcie w stylu reportażowym”,
- „przygotuj to zdjęcie jako czarno-białe artystyczne”,
- poprawki w dalszej rozmowie: „trochę cieplej”, „mniej kontrastu”, „cofnij ostatnią zmianę”.

Asystent korzysta z tych samych ustawień co użytkownik: suwaków, pluginów, presetów, kadru i masek. Po każdej zmianie
odczytuje wynik (statystyki obrazu, a dla modeli wizyjnych także podgląd) i poprawia, aż osiągnie zamierzony efekt.

## 2. Decyzje

| Temat | Decyzja |
|---|---|
| Model | Ollama. Lokalna Ollama (`http://localhost:11434`, również modele `-cloud` przez zalogowaną Ollamę) albo Ollama Cloud (`https://ollama.com`, klucz API). Model wybiera użytkownik z listy z rekomendacjami. |
| Tryb pracy | Rozmowa z poprawkami: każda tura asystenta zaczyna się od aktualnego stanu zdjęcia. |
| Narzędzia | Podstawowe suwaki, pluginy i presety, kadrowanie i prostowanie, maski lokalne (wdrażane etapami). |
| Architektura | Wbudowana w aplikację (`Services/Ai` + `MainWindow.Assistant.cs`), a nie plugin. Asystent ma dostęp do pełnego stanu, renderu i historii. |
| Undo | Jedna tura asystenta to jeden krok undo z etykietą „AI: …”. |
| Bezpieczeństwo | Model wywołuje tylko funkcje z katalogu. Każdy argument jest walidowany i przycinany do zakresu. Model nie ma dostępu do plików ani zapisu. |

## 3. Architektura

```
Services/Ai/
  AiSettings.cs       ustawienia (tryb, adresy, model, limit kroków, zgoda na chmurę, metadane) + klucz API (DPAPI)
  OllamaClient.cs     HTTP: /api/tags (lista), /api/show (możliwości: tools, vision), /api/chat (tools, images)
  AiModels.cs         klasyfikacja modeli i rekomendacje (z /api/show, a w razie jego braku z nazwy)
  ImageProbe.cs       „oczy”: statystyki obrazu (percentyle, clipping, nasycenie, dominanta, strefy 3×3), JPEG
  AiTools.cs          katalog funkcji (JSON schema) generowany z CoreParams + wykonawca (IAiEditor)
  AiSession.cs        pętla agenta i historia rozmowy
MainWindow.Assistant.cs  okno asystenta, IAiEditor na stanie aplikacji, czekanie na render, undo
```

`AiSession` i `AiTools` nie zależą od WPF. Pracują na interfejsach `IAiEditor` (stan i obraz) i `IAiChat`
(transport). Dzięki temu harness testuje pętlę na atrapach bez sieci i bez okna.

### 3.1 Pętla agenta

```
użytkownik: polecenie
  └─ wiadomość user (+ podgląd JPEG, gdy model widzi obrazy)
pętla (max N kroków, domyślnie 8):
  model → odpowiedź
    ├─ tool_calls → wykonaj każde narzędzie
    │     zmiana ustawień → zastosuj → czekaj na koniec renderu → wynik + nowe statystyki
    │     get_preview → dołącz obraz w następnej wiadomości user
    │     wyniki → wiadomości role=tool
    └─ brak tool_calls → odpowiedź końcowa (podsumowanie zmian w języku użytkownika), koniec tury
koniec tury → jeden krok undo „AI: <skrót polecenia>”
```

Gdy model przekroczy limit kroków, tura się kończy, a zmiany zostają (można je cofnąć). Przycisk Stop przerywa
żądanie HTTP. Zmiany wprowadzone do tej chwili tworzą krok undo.

### 3.2 Czekanie na render

Po zmianie ustawień asystent czeka, aż aplikacja skończy przeliczać obraz: brak renderu w toku, w kolejce i w
timerze, stan stabilny przez ~3 cykle po 60 ms, limit 20 s. Dopiero wtedy liczy statystyki z wyświetlanego obrazu
(`_lastDisplayed`, czyli po kadrze i pluginach). To ten sam warunek, na który czeka test UI (`APPLY:` w logu), ale
odczytany bezpośrednio z aplikacji.

### 3.3 Co widzi model

- `analyze_image`: rozmiar, średnia jasność, percentyle luminancji (1/5/50/95/99), % przepaleń i zatkanych cieni,
  średnie nasycenie, średnie R/G/B i opis dominanty, jasność 9 stref (3×3), czy obraz jest monochromatyczny.
- `get_preview` (tylko modele wizyjne): JPEG o dłuższym boku 1024 px.
- `get_state`: wszystkie podstawowe ustawienia z zakresami, a od etapu 2 także włączone pluginy, ich parametry,
  kadr i maski.
- Metadane (aparat, ISO, przysłona, czas, ogniskowa, data) są wysyłane tylko wtedy, gdy użytkownik na to pozwoli.
  GPS i ścieżka pliku nie są wysyłane nigdy.

### 3.4 Kontrola kontekstu i zużycia tokenów

Każde wywołanie modelu wysyła całą historię, więc koszt rośnie z długością rozmowy. Dlatego:

- **Zwijanie tur:** po zakończeniu tury (sukces, limit, Stop, błąd) wszystkie robocze wiadomości (tool_calls, wyniki
  narzędzi, podglądy) są zastępowane dwiema: poleceniem użytkownika i odpowiedzią `[changes: Exposure +0.30; …]` +
  wynik. Następne polecenie startuje z krótkiej historii; aktualny stan model odczytuje przez `get_state`.
- **Jeden podgląd:** w obrębie tury zostaje tylko najnowszy obraz (starsze są zastępowane tekstem
  „older preview removed”), a po turze w historii nie ma żadnego obrazu.
- **Budżet na zdjęcie** (ustawienia, domyślnie 200k tokenów, 0 = bez limitu): suma tokenów wejścia i wyjścia wszystkich
  wywołań w rozmowie o tym zdjęciu. Sprawdzany przed każdym wywołaniem; po przekroczeniu tura się kończy, a nowa się
  nie zacznie, dopóki użytkownik nie podniesie limitu albo nie zacznie nowej rozmowy.
- **`num_ctx`** (domyślnie 16384): rozmiar okna kontekstu wysyłany do Ollamy; wyświetlany limit to mniejsza z wartości
  `num_ctx` i długości kontekstu modelu z `/api/show`.
- **Limit kroków** na polecenie: ⚙ → „Most model calls per request”, zakres 1–30, domyślnie 8. Krok to jedno
  wywołanie modelu, które może zmienić kilka ustawień naraz. Powyżej 8 okno pokazuje ostrzeżenie:
  - każde wywołanie czyta całą rozmowę od nowa (u modeli wizyjnych także podgląd), więc jedno polecenie zużywa do
    około kroki × kontekst tokenów i trwa dłużej;
  - pogubiony model może się zapętlić i zmieniać te same ustawienia w tę i z powrotem;
  - przy modelu chmurowym to realny koszt.

  Budżet tokenów na zdjęcie nadal przerywa pracę. Komunikat o osiągnięciu limitu podaje jego wartość i miejsce,
  w którym można go zmienić.
- **Licznik w oknie:** „Context: ~3.1k / 16k tokens · used for this photo: 42k / 200k in N calls”. Czerwone
  ostrzeżenie przy 90% kontekstu albo 80% budżetu. Liczby pochodzą z `prompt_eval_count`/`eval_count`, ale liczy się
  większa z dwóch wartości: licznik serwera albo szacunek (znaki/4, ~1000 na obraz). Lokalna Ollama może podać tylko
  tokeny spoza bufora (ponownie użyty początek rozmowy pomija), co zaniżałoby kontekst i budżet. Wartości szacowane
  są oznaczone „~”.
- **Widoczność zdjęcia:** licznik mówi, czy model widzi zdjęcie („model sees the photo (N preview(s) sent)”) czy
  pracuje tylko na statystykach („text only: the photo is not sent”), a każdy wysłany podgląd ma w rozmowie
  wyszarzoną linię „📷 photo preview (1024 px JPEG, ~1k tokens) sent to the model”.

Rozmowy są trzymane w oknie głównym (słownik ścieżka → sesja), więc zamknięcie i ponowne otwarcie okna czatu albo
powrót do zdjęcia kontynuuje tę samą rozmowę z licznikami. Rozmowa jest też zapisywana w `.eye` (sekcja 7), więc
przetrwa zamknięcie aplikacji. „Nowa rozmowa” zeruje historię i liczniki.

## 4. Narzędzia (katalog funkcji)

| Etap | Funkcja | Opis |
|---|---|---|
| 1 | `get_state` | Wartości i zakresy podstawowych ustawień. |
| 1 | `analyze_image` | Statystyki aktualnego renderu. |
| 1 | `get_preview` | Podgląd JPEG (tylko modele wizyjne). |
| 1 | `set_core` | Obiekt `{Exposure, Contrast, Highlights, Shadows, Whites, Blacks, Temperature, Tint, Saturation, Clarity, Dehaze, Sharpen, Denoise, ColorNoise, Gamma, Mono}`. Ustawia tylko podane klucze; wartości są przycinane. |
| 1 | `reset_core` | Przywraca wartości domyślne podanych kluczy (albo wszystkich). |
| 2 | `list_plugins` | Filtry pluginów z parametrami (zakres, domyślna, bieżąca wartość, wybory), flagami `toolbar`, `no_mask`, `mask` oraz lista presetów (użytkownika `user:Nazwa` i pluginów `pluginId:presetId`). Opis filtra jest przycinany do 120 znaków. |
| 2 | `set_plugin` | `{filter, values}`: suwak jest przycinany do zakresu, przełącznik przyjmuje 0/1 albo true/false, wybór musi być jednym z id, kolor ma postać `#RRGGBB`. Krzywe i inne rodzaje są odrzucane. Filtr można wskazać pełnym id, końcówką po `:` albo etykietą. |
| 2 | `set_plugin_active`, `reset_plugin` | Włącza i wyłącza filtr paska narzędzi; przywraca wartości domyślne filtra (maska zostaje). |
| 2 | `apply_preset` | Stosuje preset (po id albo nazwie) jako część tej samej tury. |
| 3 | `set_crop` | `aspect` (`original`, `free`, `W:H`, także pionowe `4:5`), `size` 0.2..1, `center_x/center_y`; przy `free` prostokąt `x,y,w,h`; `reset`. Kadr mieści się w ramie. Proporcje spoza listy UI (1:1, pionowe) są zapisywane jako `original`, bo etykieta wpływa tylko na późniejsze przeciąganie. |
| 3 | `straighten` | Kąt −45..45°, reszta geometrii bez zmian. |
| 4 | `create_mask` | `radial` (środek, promienie, feather), `linear` (x1,y1 → x2,y2), `luma` (low, high, feather). Współrzędne 0..1 są w kadrze, tak jak model widzi podgląd. Aplikacja przelicza je na zdjęcie źródłowe (kadr, prostowanie, obrót). Z `mask` zmienia kształt istniejącej maski, a pominięte parametry zostają bez zmian. W `radial` promienie opisują wewnętrzną elipsę (pełny efekt), a feather 0..1 to ta sama skala co suwak Feather w panelu: 0 = twarda krawędź, 1 (domyślnie) = miękka krawędź do 2× promienia. `get_state` zwraca je w tej samej postaci. |
| 4 | `set_mask_values` | Wartości podstawowych suwaków dodawane wewnątrz maski. Zera są pomijane, `{}` usuwa wartości. |
| 4 | `link_tool_to_mask`, `delete_mask` | Ogranicza filtr do maski (`none` przywraca całe zdjęcie). Usunięcie maski przywraca całe zdjęcie powiązanym filtrom. |

`get_state` podaje też geometrię (`geometry`), włączone pluginy (`plugins_on`) i maski (`masks`, we współrzędnych
kadru). Przed turą aplikacja zamyka tryb kadrowania i edycję maski, żeby zmiany asystenta nie mieszały się
z otwartym narzędziem.

Katalog jest generowany z `CoreParams` i z deskryptorów pluginów, więc nowy suwak albo plugin trafia do asystenta
bez ręcznych zmian.

## 5. Modele i rekomendacje

Lista modeli pochodzi z `/api/tags` wybranego serwera. Możliwości każdego modelu są odczytywane z `/api/show`
(`capabilities`: `tools`, `vision`). Gdy serwer ich nie poda, klasyfikacja opiera się na nazwie rodziny modelu.
Kategorie:

| Kategoria | Warunek | Uwagi |
|---|---|---|
| ★ Polecany | tools + vision | Widzi zdjęcie i ocenia efekt, np. rodzina qwen-vl. |
| Wizyjny | vision, słabsze tools | Może działać, ale częściej myli wywołania funkcji. |
| Tylko tekst | tools, bez vision | Pracuje na samych statystykach i nie oceni treści ani kadru. |
| Nieodpowiedni | bez tools | Nie może zmieniać ustawień. |

Oferta modeli w chmurze Ollamy zmienia się, dlatego aplikacja nie ma sztywnej listy. Pokazuje to, co zwraca
serwer, z etykietami kategorii.

## 6. Prywatność i ustawienia

- Tryb chmurowy: przy pierwszym wysłaniu aplikacja prosi o jednorazową zgodę, bo podgląd zdjęcia trafi na serwer
  ollama.com.
- Klucz API jest szyfrowany DPAPI (dla bieżącego konta Windows) i trzymany w `settings.json`. Nie trafia do pliku `.eye`.
  Można też użyć zmiennej środowiskowej `OLLAMA_API_KEY`.
- Opcja „Wysyłaj metadane zdjęcia” jest domyślnie wyłączona.

## 7. Interfejs

- Okno „Asystent AI”: przycisk na pasku narzędzi (ikona robota). Okno jest niemodalne, więc zdjęcie i panel pozostają dostępne.
- U góry: serwer (lokalny/chmura), model z listy z etykietami, odświeżanie, ustawienia (adres, klucz, limit kroków,
  `num_ctx`, budżet tokenów na zdjęcie, metadane). Liczby tokenów można wpisywać jako `16384`, `200k`, `1,5M`.
- Środek: przebieg rozmowy. Wiadomości użytkownika, odpowiedzi asystenta i wyszarzone linie kroków („Exposure +0.30,
  Contrast +15”).
- Szybkie polecenia: „Album rodzinny”, „Reportaż”, „Czarno-białe artystyczne”.
- Dół: pole polecenia (Enter wysyła, Shift+Enter nowa linia, strzałki Góra/Dół przywołują wcześniejsze polecenia — ostatnie 50, ze wszystkich zdjęć, zapisane w settings.json; za najnowszym wraca niedokończony tekst), Wyślij, Stop, Nowa rozmowa, licznik kontekstu i zużycia.
- Rozmowa należy do zdjęcia. Po zmianie zdjęcia pokazuje się rozmowa tego zdjęcia albo pusta. Rozmowa trafia do
  pliku `.eye` (pole `Assistant`, tylko w stanie głównym, nigdy w krokach historii) przy autozapisie i przy Save Photo.
  Zapisywane są wiadomości użytkownika i podsumowania tur, przebieg widoczny w oknie oraz liczniki tokenów. Nie są
  zapisywane podglądy ani prompt systemowy. Po ponownym otwarciu zdjęcia rozmowa jest kontynuowana. „Nowa rozmowa”
  usuwa ją także z `.eye`.

## 8. Etapy

1. **Fundament (zrobione):** ustawienia i lista modeli, klient Ollama, okno czatu, `get_state`, `analyze_image`,
   `get_preview`, `set_core`, `reset_core`, jedna tura równa jednemu undo, testy harness (`--ai`).
   Nie sprawdzone jeszcze z prawdziwym modelem (podczas budowy Ollama nie działała); do zrobienia: rozmowa na żywo
   z modelem ★ i dostrojenie promptu systemowego.
2. **Pluginy i presety, zapis rozmowy w `.eye` (zrobione).**
3. **Kadrowanie i prostowanie (zrobione).**
4. **Maski lokalne (zrobione):** radialne, liniowe, jasnościowe. Maski pędzla zostają w rękach użytkownika.
5. Rozszerzone testy: test UI okna asystenta jest gotowy (`tools/UiAiTest/run.ps1`). Do zrobienia: nagrane rozmowy
   z prawdziwymi modelami i dostrojenie promptu.

## 9. Testy

`dotnet run --project tools\Harness\tools.csproj -c Release -- --ai` uruchamia testy bez sieci:

- `ImageProbe`: statystyki na syntetycznych obrazach (czerń, biel, gradient, dominanta, monochrom).
- Katalog narzędzi: każdy klucz `CoreParams` ma swój zakres, a JSON jest poprawny.
- Pętla `AiSession` na atrapie transportu:
  - przycinanie wartości spoza zakresu,
  - argumenty podane jako tekst JSON,
  - nieznane narzędzie (błąd wraca do modelu, bez wyjątku),
  - limit kroków,
  - odpowiedź końcowa,
  - podgląd dołączany tylko modelom wizyjnym, tylko najnowszy,
  - zwijanie tury do polecenia i podsumowania (także po Stop i limicie), brak obrazów w historii,
  - liczniki tokenów (z serwera i szacowane), zatrzymanie po przekroczeniu budżetu, parsowanie „200k”/„1,5M”.
- Parsowanie odpowiedzi `/api/tags`, `/api/show` (capabilities, długość kontekstu) i `/api/chat` (liczniki, błąd),
  klasyfikacja modeli.
- Szyfrowanie klucza DPAPI (zaszyfrowanie i odszyfrowanie).
- Etapy 2–4 na atrapie edytora:
  - `set_plugin`: przycinanie, przełączniki, wybory, kolory, odrzucanie krzywych;
  - przełączanie filtrów paska, reset, presety;
  - kadry `1:1`, `2:3` przy brzegu, `free`, reset; prostowanie z limitem ±45°;
  - maski w kadrze (przeliczanie współrzędnych w obie strony), zmiana kształtu, wartości, powiązania, usuwanie;
  - kroki w podsumowaniu tury;
  - rozmowa w `.eye`: zapis i odczyt, brak podglądów i promptu systemowego, kontynuacja po wczytaniu.

### Test UI

`powershell -ExecutionPolicy Bypass -File tools\UiAiTest\run.ps1 [-Exe <ścieżka>]` (domyślnie build Debug):

- uruchamia w skrypcie atrapę serwera Ollama (`localhost`, model `fake-vl` z tools i vision) i własny proces
  aplikacji z odizolowanymi ustawieniami (`PHOTOGRAPHEREYE_APPDATA`);
- steruje wyłącznie tym procesem przez UI Automation, bez myszy i klawiatury;
- sprawdza:
  - otwarcie okna i listę modeli;
  - turę z `set_core`: przebieg, notatkę 📷, licznik tokenów;
  - zachowanie rozmowy po zamknięciu okna czatu;
  - turę etapów 2–4 bez błędów narzędzi i jej zapis w `.eye` (kąt, kadr, maska z Exposure, wartość pluginu,
    powiązanie z maską, rozmowa);
  - jedno Undo, które cofa całą turę;
  - „Nową rozmowę”, która usuwa rozmowę z `.eye`.

Żadne zapytanie nie trafia do prawdziwego modelu.
