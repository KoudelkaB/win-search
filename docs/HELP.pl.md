# Pomoc File Search Manager

File Search Manager ma dwa główne pola:

- **Filtr** zawęża listę plików według nazwy, folderu, ścieżki lub wybranych katalogów.
- **Szukaj** przeszukuje zawartość plików w obrębie bieżących wyników filtrowania.

Lista wyników jest przystosowana do obsługi klawiaturą. Gdy ma fokus, panel **Skróty** pokazuje polecenia dostępne dla bieżącego wyboru.

## Indeksowanie i podniesienie uprawnień

Przy uruchomieniu File Search Manager wczytuje gotowe dyski i śledzi zmiany w systemie plików. Każdy dysk wczytuje się niezależnie, więc wolny dysk sieciowy nigdy nie opóźnia pozostałych.

Domyślnie indeksowane są tylko dyski NTFS — inne systemy plików (zasoby sieciowe, pamięci FAT) nie mogą korzystać z szybkiego odczytu MFT, a przechodzenie po nich plik po pliku potrafi zdominować całe wczytywanie. Przyciskiem **💽 Dyski…** na pasku stanu wybierzesz dokładnie te dyski, które mają być indeksowane; niezaznaczone dyski nie są ani skanowane, ani śledzone.

W przypadku dysków NTFS najszybsza jest lektura tablicy MFT systemu NTFS. File Search Manager może to zrobić na jeden z tych sposobów:

- Usługa systemu Windows: bez żadnego monitu. W instalatorze zaznaczona domyślnie.
- Pomocnik z podwyższonymi uprawnieniami: jeden monit UAC na uruchomienie aplikacji. Bez usługi jest proponowany przy starcie, aby indeksowanie mogło ruszyć od razu. Z usługą pojawia się dopiero przy pierwszej czynności, która naprawdę wymaga uprawnień administratora — klawisz `A` lub odczyt plików dostępnych tylko dla administratorów. Przycisk **🛡** obok **🌐** sprawia, że pytanie pada już przy uruchomieniu.
- Aplikacja uruchomiona z podwyższonymi uprawnieniami: uruchom File Search Manager jako administrator.

Jeśli nic z powyższego nie jest dostępne, File Search Manager przechodzi na przeglądanie folderów. Aplikacja nadal działa, ale początkowe wczytywanie może być wolniejsze.

Usługa działa dla indeksowania w trybie tylko do odczytu. Udostępnia dane MFT aplikacji i nazywa się `WinSearchService`.

## Składnia filtrów

Terminy filtru oddziela się spacjami. Terminy lub ścieżki zawierające spacje należy ująć w cudzysłów.

Dopasowanie nazw:

- `report` pasuje do nazw zawierających `report`.
- `:report` pasuje do nazw zaczynających się od `report`.
- `report:` pasuje do nazw kończących się na `report`.
- `:report:` pasuje do dokładnej nazwy `report`.
- `.pdf:|.docx:` pasuje do nazw kończących się na `.pdf` lub `.docx`.
- Wiele terminów łączy się operatorem AND, więc `report .pdf:` znajduje nazwy zawierające `report` i kończące się na `.pdf`.

Dopasowanie folderów:

- `src\` pasuje do elementów, których bezpośredni folder nadrzędny nazywa się `src`.
- `src\\` pasuje do elementów mających `src` w dowolnym miejscu pełnej ścieżki.
- Kotwice nazwy działają dla każdego składnika ścieżki, więc `:src:\\` znajduje elementy z folderem o dokładnej nazwie `src` w dowolnym miejscu ścieżki.
- `"C:\Work"` pasuje do elementów bezpośrednio w `C:\Work`.
- `"C:\Work\\"` pasuje rekurencyjnie do elementów poniżej `C:\Work`.

Historia:

- `Ctrl+Left` i `Ctrl+Right` przechodzą po historii filtrów.
- `Down` otwiera podpowiedzi.
- `Del` usuwa zaznaczoną podpowiedź.
- Przytrzymaj `Ctrl` przy otwieraniu podpowiedzi, aby użyć historii najnowszej zamiast najczęściej używanej.

### Przypięte filtry

- Kliknij **📌 Przypnij…**, aby dodać edytowalną kartę do wiersza przypiętych filtrów. Wpisz nazwę na miejscu i naciśnij `Enter`; `Esc` anuluje.
- Kliknij nazwany filtr, aby przywrócić zapisane wyrażenie. Aktywny nazwany filtr jest wyróżniony.
- Kliknij nazwany filtr prawym przyciskiem, aby zaktualizować go z bieżącego wyrażenia, zmienić nazwę na miejscu lub odpiąć.
- Przypięte filtry są przywracane przy uruchomieniu aplikacji.

**Eksportuj…** zapisuje wszystkie przypięte filtry i wpisy koszyka celów do jednego pliku ustawień JSON. **Importuj…** sprawdza plik ustawień i po potwierdzeniu zastępuje bieżące przypięte filtry oraz cele jego zawartością.

## Wyszukiwanie w treści

Wpisz termin w polu **Szukaj** i naciśnij `Enter`.

Opcje kodowania:

- `UTF-8` szuka tekstu zakodowanego w UTF-8.
- `UTF-16` szuka tekstu zakodowanego w UTF-16 little-endian.
- `HEX` szuka bajtów zapisanych szesnastkowo i oddzielonych spacjami, na przykład `48 65 6C 6C 6F`.

Pole wyboru **Ignoruj wielkość liter** dotyczy wyszukiwania tekstu.

Kolory wyników po wyszukiwaniu w treści:

- Zielony: treść została znaleziona.
- Czerwony: treści nie znaleziono.
- Czarny: element nie był przeszukiwany lub wyszukiwanie wyczyszczono.
- Niebieski: folder.

Wpisanie w polu wyszukiwania czegokolwiek innego niż `Enter` czyści bieżący stan wyników wyszukiwania w treści.

## Obsługa myszą

- Kliknięcie, Ctrl+kliknięcie lub Shift+kliknięcie wyników zaznacza pojedyncze elementy lub zakresy.
- Dwukrotne kliknięcie nazwy pliku lub folderu filtruje do tego elementu.
- Dwukrotne kliknięcie kolumny folderu otwiera Eksploratora z zaznaczonym elementem.
- Ctrl + kliknięcie prawym przyciskiem otwiera wynik domyślną akcją systemu Windows.
- Kliknięcie nagłówka kolumny sortuje według tej kolumny.
- Kliknięcie zaznaczonych elementów prawym przyciskiem daje dostęp do otwierania, dynamicznego Otwórz za pomocą, schowka, zmiany nazwy, archiwizacji i usuwania.
- **Dodaj do koszyka celów** zależy od klikniętej kolumny: Nazwa dodaje zaznaczone elementy, a Folder ich foldery nadrzędne. Pozycja jest ukryta, gdy odpowiednie ścieżki nie są obsługiwanymi celami albo już znajdują się w koszyku.
- Przeciąganie z kolumny Nazwa wysyła zaznaczone pliki lub katalogi do innej aplikacji.
- Przeciąganie z kolumny Folder wysyła foldery nadrzędne zaznaczonych elementów.
- Upuść pliki na katalog w kolumnie Nazwa lub na ścieżkę nadrzędną w kolumnie Folder. Wybierz kopiowanie, przeniesienie, dowiązanie symboliczne lub twarde.
- Upuszczenie plików na komórkę Nazwa z plikiem wykonywalnym uruchamia go z upuszczonymi ścieżkami jako argumentami.
- Upuszczanie plików działa zgodnie z domyślnymi zasadami Eksploratora: jeden folder na tym samym woluminie oznacza przeniesienie, inny wolumin oznacza kopiowanie. Przytrzymaj `Ctrl`, aby kopiować, `Shift`, aby przenieść, lub `Alt`, aby utworzyć dowiązanie symboliczne. Wiele miejsc docelowych oznacza kopiowanie.

## Praktyczne przykłady

- **Zwalnianie miejsca na dysku** — wyczyść **Filtr**, aby wyświetlić wszystkie zindeksowane elementy. Kliknij nagłówek **Rozmiar** (w razie potrzeby ponownie), aż największe elementy znajdą się na górze, przejrzyj listę od góry i usuń niepotrzebne elementy za pomocą `Shift+Delete` (trwale, z pominięciem Kosza).
- **Obserwowanie, gdzie aplikacja zapisuje dane** — wyczyść **Filtr** i kliknij nagłówek **Zmieniono**, aż ostatnio zmienione elementy znajdą się na górze. Następnie uruchom lub używaj obserwowanej aplikacji. Pliki, do których aplikacja właśnie zapisuje, przesuwają się na górę po każdej zmianie, a kolumna **Folder** pokazuje ich położenie. Wpisz najpierw ścieżkę w polu **Filtr**, jeśli chcesz obserwować tylko część systemu plików.
- **Wyszukiwanie tekstu w plikach źródłowych** — użyj na przykład `C:\Projects\\ .cs:`, aby ograniczyć wyniki do plików `.cs` w projekcie. Wpisz szukany tekst w polu **Szukaj** i naciśnij `Enter`.

## Menu kontekstowe

Menu kontekstowe dostosowuje się do bieżącego zaznaczenia:

- **Otwórz za pomocą** wymienia tylko zainstalowane aplikacje zgodne z zaznaczeniem. Skonfigurowane narzędzie porównujące jest pierwsze przy dokładnie dwóch plikach lub katalogach.
- **Otwórz folder nadrzędny** otwiera Eksploratora w folderze nadrzędnym elementu.
- **Kopiuj ścieżkę** kopiuje pełne ścieżki zaznaczonych elementów.
- **7-Zip** pojawia się, gdy zainstalowany jest `7zFM.exe`.
- **Zip** tworzy archiwum ZIP.
- **Utwórz archiwum 7z** pojawia się tylko wtedy, gdy zainstalowany jest `7z.exe` lub `7zz.exe`.
- **Rozpakuj** pojawia się dla obsługiwanych archiwów.
- **Przenieś do Kosza** wykonuje usuwanie z możliwością przywrócenia.

### Usuwanie i przywracanie

Usuwanie najpierw próbuje objąć każdy zaznaczony plik lub folder jedną szybką operacją. Jeśli zablokowany, niedostępny lub w inny sposób nieusuwalny element uniemożliwia operację na całym folderze, File Search Manager kontynuuje od największych możliwych do usunięcia poddrzew, a następnie od pojedynczych plików. Elementy, których nadal nie da się usunąć — oraz foldery nadrzędne niezbędne do ich przechowywania — pozostają na miejscu. Błędy są zgłaszane po podjęciu próby dla wszystkich osiągalnych elementów równorzędnych.

`Delete` wysyła każdy pomyślnie przetworzony element do Kosza i zachowuje jego atrybuty oraz pierwotne położenie. Częściowa operacja może być widoczna jako kilka wpisów w Koszu, ponieważ nienaruszone podfoldery są w miarę możliwości trzymane razem, a dzielone są tylko zablokowane fragmenty. Aby przywrócić każdy element do pierwotnej ścieżki, użyj polecenia **Przywróć** w Koszu, a nie ręcznego kopiowania jego wpisów.

`Shift+Delete` używa tego samego podejścia, ale usuwa trwale. Tam, gdzie to konieczne, zdejmuje atrybut `Tylko do odczytu`; zachowanie to jest ogólne i nie ogranicza się do repozytoriów Git. Pliki zablokowane przed usunięciem oraz ścieżki odrzucone przez uprawnienia systemu plików pozostają i trafiają do końcowego raportu błędów.

Upuszczanie katalogów i `Ctrl+V` wyświetlają wybór akcji. Konflikty istniejących nazw oferują nadpisanie, pominięcie, automatyczną zmianę nazwy oraz zastosowanie do wszystkich. Transfery pokazują postęp poszczególnych elementów i można je anulować między operacjami najwyższego poziomu.

## Koszyk celów

Koszyk celów na dole przechowuje wielokrotnego użytku cele upuszczania:

- **+ Nazwa** dodaje zaznaczone elementy z kolumny Nazwa. Foldery, obsługiwane archiwa i pliki wykonywalne mają zachowanie właściwe dla danego typu celu.
- **+ Folder** dodaje foldery nadrzędne zaznaczonych elementów.
- W trakcie przeciągania pasek pokazuje dwie duże strefy upuszczania: **＋ Dodaj jako cel** i **📤 Wyślij do wszystkich celów (n)**. Pojawiają się, gdy tylko rozpocznie się przeciąganie na liście wyników albo gdy zewnętrzne przeciąganie znajdzie się nad paskiem, i znikają po zakończeniu.
- **Dodaj jako cel** dodaje dokładnie te ścieżki, które wynikają z przeciągania. Przeciąganie rozpoczęte w kolumnie Folder dodaje więc foldery nadrzędne, a rozpoczęte w kolumnie Nazwa — nazwane elementy. Upuszczenie na pustym miejscu za znacznikami celów również dodaje cele.
- **Wyślij do wszystkich celów** używa każdego dostępnego celu, w razie potrzeby po wyświetleniu podsumowania. Upuszczenie bezpośrednio na znaczniku używa tylko tego jednego celu.
- Cele będące folderami pokazują wybór kopiowania, przenoszenia, dowiązania symbolicznego i twardego.
- Cele będące archiwami dodają źródła do archiwum. Aktualizowanie formatów innych niż ZIP wymaga zainstalowanego `7z.exe` lub `7zz.exe`.
- Cele będące plikami wykonywalnymi są uruchamiane ze ścieżkami źródłowymi jako argumentami i wymagają potwierdzenia.
- **Wyślij schowek do wszystkich (n)…** to ta sama operacja ze schowkiem jako źródłem; jest wyłączona, dopóki nie ustawiono żadnych celów.
- Dwukrotne kliknięcie otwiera cel. Kliknięcie celu prawym przyciskiem pozwala przefiltrować do niego, otworzyć go, usunąć lub wyczyścić wszystkie cele. Kliknięcie tła paska prawym przyciskiem oferuje również **Wyczyść cele**.

Koszyk jest zapisywany automatycznie między uruchomieniami aplikacji. Brakujące cele pozostają w zapisanej konfiguracji, ale są pomijane, dopóki nie staną się znów dostępne.

Wszystkie polecenia celów i filtrów znajdują się pod `Alt` i działają tak samo w całym oknie: przytrzymaj `Alt`, aby zobaczyć je w panelu **Skróty**, a następnie naciśnij `N` (dodaj zaznaczone nazwy jako cele), `F` (dodaj foldery nadrzędne), `V` (wyślij schowek do wszystkich celów — trzymaj dalej i dodaj `L`/`H`/`O` dla dowiązania, dowiązania twardego, nadpisania), `C` (wyczyść cele), `P` (przypnij filtr), `I`/`E` (importuj/eksportuj przypięte filtry i cele). Sekwencje klawiszy przenoszą od razu z wybraną akcją; okno wyboru akcji otwiera tylko przycisk na pasku narzędzi oraz upuszczanie myszą.

Gdy sekwencja klawiszy jest aktywna, **Skróty** pokazują tylko prawidłowe kolejne klawisze. `Esc` jest oferowany tylko wtedy, gdy zwolnienie trzymanych klawiszy wykonałoby akcję, a `Alt` nie jest właśnie wciśnięty. Działa to również w sekwencji z `Alt` po jego zwolnieniu, gdy trzymany jest inny klawisz sekwencji. `Backspace` zawsze cofa o jeden krok. Elementy sterujące są oddzielone od wyborów poleceń linią.

Pozycje kończące się znakiem `›` mają kolejny poziom wyborów; przytrzymaj klawisz sekwencji i naciśnij ten klawisz, aby wyświetlić podmenu.

Gdy lista wyników ma fokus, celami zarządzają też sekwencje klawisza `T`: `T` dodaje zaznaczone elementy jako cele, `T` `F` dodaje ich foldery nadrzędne, `T` `V` wysyła schowek do wszystkich celów, a `T` `C` czyści cele. `T` `V` przyjmuje te same modyfikatory co `V`: `L` wysyła jako dowiązania symboliczne, `H` jako twarde, a `O` nadpisuje istniejące pliki.

## Polecenia klawiaturowe

Ustaw fokus na liście wyników i naciśnij klawisz pokazany w panelu **Skróty**. Niektóre polecenia trwają, dopóki klawisze są trzymane, i kończą się po zwolnieniu wszystkich.

Typowe polecenia:

- `Enter`: filtruj do zaznaczonych folderów.
- `Delete`: przenieś zaznaczone elementy do Kosza po potwierdzeniu.
- `Shift+Delete`: usuń zaznaczone elementy trwale bez potwierdzenia.
- `Ctrl+C`: standardowe kopiowanie zgodne z powłoką.
- `Ctrl+X`: standardowe wycinanie zgodne z powłoką.
- `Ctrl+V`: wklej po wybraniu kopiowania, przeniesienia, dowiązania symbolicznego lub twardego.
- `C`: skopiuj zaznaczone elementy do schowka. Dodanie `V`, `T`, `W` lub `A` kopiuje zamiast tego wersję pliku, czas utworzenia, czas ostatniego zapisu albo czas ostatniego dostępu; `+` na początku dopisuje do schowka zamiast go zastępować.
- `X`: wytnij zaznaczone elementy do schowka.
- `D`: porównaj w skonfigurowanym narzędziu porównującym. Dostępne tylko przy zaznaczeniu dokładnie dwóch elementów.
- `V`: wklej pliki ze schowka do zaznaczonych folderów lub folderów nadrzędnych.
- `O`: otwórz zaznaczone elementy w innej aplikacji.
- `A`: otwórz zaznaczone elementy jako administrator.
- `F2`: zmień nazwę jednego fizycznego pliku lub folderu bezpośrednio w komórce Nazwa. Przy wielu elementach użyj jednego z poleceń przekształcania poniżej.
- `F3`: wyświetl zaznaczone elementy.
- `F4`: edytuj zaznaczone elementy.
- `N`: skopiuj zaznaczone nazwy.
- `P`: skopiuj zaznaczone pełne ścieżki.
- `F`: skopiuj zaznaczone ścieżki folderów.
- `M`: pokaż wbudowany pasek do utworzenia katalogu w zaznaczonych folderach.
- `S`: polecenia zaznaczania.
- `T`: polecenia celów — dodaj zaznaczone jako cele; następnie `F` dla folderów nadrzędnych, `V` aby wysłać schowek do wszystkich celów (z `L`/`H`/`O` dla dowiązania, dowiązania twardego, nadpisania), `C` aby wyczyścić cele.
- `U`: rozpakuj zaznaczone archiwa. Dodanie `NumPad7` wywoła `7z.exe` zamiast wbudowanej obsługi.
- `Z`: spakuj zaznaczone elementy. Dodanie `NumPad7` wywoła `7z.exe`.
- `F1`: otwórz ten plik pomocy. Działa w całym oknie.
- `F12`: odśwież z NTFS. Działa w całym oknie i nie wymaga zaznaczenia; każdy dysk odświeża się niezależnie, więc wolny dysk sieciowy nigdy nie opóźnia pozostałych. `F1` i `F12` pozostają widoczne w panelu **Skróty**, gdy siatka wyników ma fokus.
- `Right Shift`: przenieś fokus z powrotem do pola filtru.

Polecenia pod `Ctrl`:

- `Ctrl+A`: zaznacz lub odznacz wszystko.
- `Ctrl+D`: przefiltruj do folderów nadrzędnych zaznaczonych elementów.
- `Ctrl+F`: przefiltruj do zaznaczonych folderów.
- `Ctrl+N`: utwórz nowe foldery w zaznaczonych folderach.
- `Ctrl+J`: przejdź do następnego zaznaczonego elementu; `Ctrl+Shift+J` do poprzedniego.

Cele otwierania po `O` lub `A`. Każda pozycja pojawia się tylko wtedy, gdy aplikacja została wykryta:

- `B`: Eksplorator plików.
- `W`: wykryta domyślna przeglądarka internetowa.
- `C`: Chrome.
- `F`: Firefox.
- `E`: Edge.
- `O`: Opera.
- `I`: Internet Explorer.
- `A`: Adobe Reader.
- `T`: podgląd tekstu.
- `D`: Visual Studio Code.
- `V`: Visual Studio.
- `Y`: Antigravity.
- `G`: Ghostscript.
- `P`: GhostPCL.
- `X`: GhostXPS.
- `R`: przeglądarka wykryta dla zawartości PRN.
- `S`, następnie `P`: PowerShell.
- Samo `S`: Wiersz polecenia.

`G`, `P`, `X` i `R` przed uruchomieniem pytają o wartość DPI.

Polecenia zaznaczania po `S`:

- `A`: zaznacz lub odznacz wszystko.
- `D`: zaznacz katalogi.
- `F`: zaznacz pliki.
- `I`: odwróć zaznaczenie.
- `G`: zaznacz zielone wiersze.
- `R`: zaznacz czerwone wiersze.
- `B`: zaznacz czarne wiersze.

Polecenia zmiany nazwy i atrybutów po `F2`:

- `V`: pobierz nową ścieżkę lub nazwę ze schowka.
- `N`: zmień nazwę.
- `E`: zmień rozszerzenie; `E`, a następnie `Delete` usuwa rozszerzenie.
- `.`: dodaj rozszerzenie.
- `Delete`: usuń tekst z nazwy.
- `F`: dodaj przedrostek.
- `L`: dodaj przyrostek.
- `Insert`: wstaw tekst w danym miejscu.
- `R`: zamień tekst.
- `C`: zmień czas utworzenia, a następnie `V` dla czasu ze schowka lub `C` dla bieżącego.
- `W`: zmień czas ostatniego zapisu, z tymi samymi opcjami `V` i `C`.
- Dodaj najpierw `O`, aby nadpisać istniejące cele, jeśli jest to obsługiwane.

## Dane i rozwiązywanie problemów

Stan użytkownika jest przechowywany w:

```text
%LOCALAPPDATA%\win-search
```

Jeśli wczytywanie NTFS jest wolne, zainstaluj usługę z instalatora — jest zaznaczona domyślnie — albo zatwierdź monit o podwyższenie uprawnień. Pasek stanu informuje, czy dany dysk użył usługi, dostępu bezpośredniego, pomocnika administratora czy przeglądania folderów.

Jeśli dysk zmapowany lub zewnętrzny jest niedostępny, File Search Manager pomija go po krótkim sprawdzeniu gotowości, aby uruchamianie nie zatrzymało się na nieosiągalnym magazynie.
