# Asystent AI (Ollama): dokument projektowy

Status: etap 1 jest zaimplementowany. Etapy 2–5 są zaplanowane.

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
- **Limit kroków** na polecenie (domyślnie 8).
- **Licznik w oknie:** „Context: ~3.1k / 16k tokens · used for this photo: 42k / 200k in N calls”. Czerwone
  ostrzeżenie przy 90% kontekstu albo 80% budżetu. Liczby pochodzą z `prompt_eval_count`/`eval_count`; gdy serwer ich
  nie poda, są szacowane (znaki/4, ~1000 na obraz) i oznaczane „~”.

Rozmowy są trzymane w oknie głównym (słownik ścieżka → sesja), więc zamknięcie i ponowne otwarcie okna czatu albo
powrót do zdjęcia kontynuuje tę samą rozmowę z licznikami, do zamknięcia aplikacji. „Nowa rozmowa” zeruje historię
i liczniki.

## 4. Narzędzia (katalog funkcji)

| Etap | Funkcja | Opis |
|---|---|---|
| 1 | `get_state` | Wartości i zakresy podstawowych ustawień. |
| 1 | `analyze_image` | Statystyki aktualnego renderu. |
| 1 | `get_preview` | Podgląd JPEG (tylko modele wizyjne). |
| 1 | `set_core` | Obiekt `{Exposure, Contrast, Highlights, Shadows, Whites, Blacks, Temperature, Tint, Saturation, Clarity, Dehaze, Sharpen, Denoise, ColorNoise, Gamma, Mono}`. Ustawia tylko podane klucze; wartości są przycinane. |
| 1 | `reset_core` | Przywraca wartości domyślne podanych kluczy (albo wszystkich). |
| 2 | `list_plugins`, `set_plugin`, `set_plugin_active`, `apply_preset` | Pluginy (B&W profiles, split tone, color balance, vignette…). Parametry są opisane z deskryptorów `ParamDef`. |
| 3 | `set_crop`, `straighten` | Proporcje (`4:5`, `3:2`…), prostokąt kadru 0..1, kąt. Korzysta z `SetGeometry`. |
| 4 | `create_mask`, `set_mask_values`, `link_tool_to_mask` | Maski radialne i liniowe we współrzędnych 0..1 (model zna je z podglądu) oraz wartości wewnątrz maski. |

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
- Dół: pole polecenia (Enter wysyła, Shift+Enter nowa linia), Wyślij, Stop, Nowa rozmowa, licznik kontekstu i zużycia.
- Rozmowa należy do zdjęcia. Po zmianie zdjęcia zaczyna się nowa, a historia poprzednich (także po zamknięciu okna
  czatu) zostaje w pamięci do zamknięcia aplikacji. Zapis w `.eye` jest planowany w etapie 2.

## 8. Etapy

1. **Fundament (zrobione):** ustawienia i lista modeli, klient Ollama, okno czatu, `get_state`, `analyze_image`,
   `get_preview`, `set_core`, `reset_core`, jedna tura równa jednemu undo, testy harness (`--ai`).
   Nie sprawdzone jeszcze z prawdziwym modelem (podczas budowy Ollama nie działała); do zrobienia: rozmowa na żywo
   z modelem ★ i dostrojenie promptu systemowego.
2. Pluginy i presety, zapis rozmowy w `.eye`.
3. Kadrowanie i prostowanie.
4. Maski lokalne.
5. Rozszerzone testy: nagrane rozmowy, testy UI okna asystenta.

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
