# Polityka prywatności PhotographerEye

Wersja z 11 listopada 2026 r.

## 1. Kto odpowiada za program

Autorem programu jest Tomasz Kuehn (kontakt: tomasz.kuehn@gmail.com). PhotographerEye działa w całości na Twoim komputerze. Autor nie prowadzi żadnego serwera dla programu i nie otrzymuje od niego żadnych danych.

## 2. Czego program nie robi

- Nie wysyła telemetrii, statystyk użycia ani raportów o błędach.
- Nie ma kont użytkowników, reklam ani śledzenia.
- Nie wysyła Twoich zdjęć ani danych nigdzie poza asystentem AI, i to tylko wtedy, gdy sam go użyjesz (punkt 4).

## 3. Dane zapisywane na Twoim komputerze

Program zapisuje dane tylko lokalnie. Autor nie ma do nich dostępu, a Ty możesz je w każdej chwili usunąć.

- **Ustawienia**, w pliku `%APPDATA%\PhotographerEye\settings.json`. Są to: opcje widoku, ostatnio otwierane foldery i ustawienia asystenta AI. Plik zawiera też ostatnie 50 poleceń wysłanych do asystenta oraz datę akceptacji tej polityki. Jest w nim także klucz API Ollama, zaszyfrowany usługą Windows DPAPI, więc odczyta go tylko Twoje konto Windows.
- **Pluginy, presety, tabele LUT i dane pluginów**, w folderze `%APPDATA%\PhotographerEye`.
- **Zapamiętane podglądy zdjęć**, w folderze `%LOCALAPPDATA%\PhotographerEye\previews`. Wyłączysz je opcją *Remember previews*, a usuniesz przyciskiem *Clear preview cache*.
- **Pliki .eye** w podfolderze `_eye_` obok zdjęć. Zawierają ustawienia edycji, maski i rozmowę z asystentem AI: polecenia, odpowiedzi, podsumowania zmian i liczniki tokenów. Nie ma w nich podglądów zdjęć. Rozmowę usuwa przycisk *New conversation*, a cały plik możesz skasować.
- **Pliki tymczasowe** w folderze `%TEMP%`, tworzone przy dekodowaniu zdjęć RAW i przy instalacji pluginów.
- **Dziennik diagnostyczny** `%TEMP%\photographereye-debug.log`. Powstaje tylko wtedy, gdy ustawisz zmienną środowiskową `PHOTOGRAPHEREYE_DEBUG=1`.

## 4. Asystent AI (opcjonalny)

Asystent łączy się z siecią tylko wtedy, gdy otworzysz jego okno, aby pobrać listę modeli, i gdy wyślesz polecenie. Łączy się z serwerem Ollama wybranym w ustawieniach asystenta:

- **Lokalnym**, domyślnie `http://localhost:11434`. Dane nie opuszczają wtedy komputera. Wyjątkiem są modele z końcówką `-cloud`: lokalna Ollama przekazuje dane do ollama.com.
- **Ollama Cloud** (`https://ollama.com`), z Twoim kluczem API. Klucz trafia tylko do tego serwera.

Do modelu trafiają:

- Twoje polecenia i wcześniejsza rozmowa o tym zdjęciu.
- Bieżące ustawienia edycji, aktywne pluginy, kadr i maski.
- Statystyki obrazu, np. histogram i jasność, oraz wymiary zdjęcia.
- Podgląd zdjęcia w formacie JPEG o dłuższym boku 1024 px. Trafia tylko do modeli, które rozumieją obrazy.
- Dane aparatu: model, ISO, przysłona, czas naświetlania, ogniskowa i data. Trafiają tylko po włączeniu tej opcji.

Nigdy nie są wysyłane: położenie GPS, ścieżka do pliku ani inne zdjęcia.

Przed pierwszym wysłaniem danych do chmury program prosi o zgodę. Dane wysłane do ollama.com przetwarza firma Ollama według własnej polityki prywatności. Autor PhotographerEye tych danych nie otrzymuje.

## 5. Pluginy

Pluginy dołączone do programu działają lokalnie i nie łączą się z siecią. Pluginy innych autorów, które instalujesz samodzielnie, działają z uprawnieniami programu. Za ich działanie odpowiadają ich autorzy, więc instaluj je tylko z zaufanych źródeł.

## 6. Linki

Linki w oknie *About* (e-mail, GitHub) otwierają się w Twojej przeglądarce lub programie pocztowym. Dzieje się to dopiero po kliknięciu.

## 7. Twoje prawa

Autor nie zbiera Twoich danych, więc po jego stronie nie ma czego udostępnić, poprawić ani usunąć. Wszystkie dane opisane w punkcie 3 są pod Twoją kontrolą. Pytania dotyczące prywatności możesz kierować na adres podany w punkcie 1.

## 8. Zmiany polityki

Gdy polityka się zmieni, nowa wersja pojawi się przy uruchomieniu programu i trzeba będzie ją zaakceptować. Bieżącą wersję i datę jej akceptacji znajdziesz w ustawieniach, pod *Privacy policy*.
