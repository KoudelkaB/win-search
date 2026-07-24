# Pomoc File Search Manager

`F1` otwiera tę pomoc w aplikacji, a `F12` odświeża indeks NTFS. **Skróty** pokazują klawisze dostępne dla bieżącego wyboru.

## Składnia filtrów

Terminy są oddzielane spacjami i łączone jako **AND**. Terminy lub ścieżki ze spacjami należy ująć w cudzysłów.

- `report` zawiera tekst; `:report` zaczyna się od niego; `report:` kończy się nim; `:report:` jest dokładny.
- `.pdf:|.docx:` oznacza nazwę kończącą się jednym z tych rozszerzeń; `report .pdf:` łączy warunki.
- `src\` wskazuje bezpośredni folder nadrzędny; `src\\` wyszukuje w całej ścieżce.
- Kotwice nazwy działają dla każdego składnika ścieżki, więc `:src:\\` znajduje elementy z folderem o dokładnej nazwie `src` w dowolnym miejscu ścieżki.
- `"C:\Work"` wskazuje elementy bezpośrednio w folderze; `"C:\Work\\"` wskazuje elementy rekurencyjnie poniżej.

Przykłady: `invoice .pdf:`, `:IMG_ .jpg:|.jpeg:`, `C:\Projects\\ .cs:`. `Ctrl+Left` i `Ctrl+Right` przechodzą po historii, a `Down` pokazuje podpowiedzi.

## Praktyczne przykłady

- **Zwalnianie miejsca na dysku** — wyczyść **Filtr**, aby wyświetlić wszystkie zindeksowane elementy. Kliknij nagłówek **Rozmiar** (w razie potrzeby ponownie), aż największe elementy znajdą się na górze, przejrzyj listę od góry i usuń niepotrzebne elementy za pomocą `Shift+Delete` (trwale, z pominięciem Kosza).
- **Obserwowanie, gdzie aplikacja zapisuje dane** — wyczyść **Filtr** i kliknij nagłówek **Zmieniono**, aż ostatnio zmienione elementy znajdą się na górze. Następnie uruchom lub używaj obserwowanej aplikacji. Pliki, do których aplikacja właśnie zapisuje, przesuwają się na górę po każdej zmianie, a kolumna **Folder** pokazuje ich położenie. Wpisz najpierw ścieżkę w **Filtrze**, jeśli chcesz obserwować tylko część systemu plików.
- **Wyszukiwanie tekstu w plikach źródłowych** — użyj na przykład `C:\Projects\\ .cs:`, aby ograniczyć wyniki do plików `.cs` w projekcie. Wpisz szukany tekst w polu **Szukaj** i naciśnij `Enter`.

## Szukanie i klawiatura

Wpisz tekst i naciśnij `Enter`. `UTF-8` i `UTF-16` szukają tekstu; `HEX` szuka bajtów oddzielonych spacjami, np. `48 65 6C 6C 6F`. Zielony oznacza znaleziono, czerwony nie znaleziono, czarny nie przeszukano, a niebieski folder.

- `Enter` filtruje foldery; `Delete` / `Shift+Delete` przenosi do Kosza / usuwa trwale.
- `Ctrl+C`, `Ctrl+X`, `Ctrl+V` kopiują, wycinają i wklejają; `F2`, `F3`, `F4` zmieniają nazwę, wyświetlają i edytują.
- `O` / `A` otwiera / otwiera jako administrator; `U` / `Z` rozpakowuje / tworzy archiwum.

## Usuwanie i przywracanie

`Delete` najpierw próbuje przenieść każdy wybrany folder do Kosza jako całość. Jeśli uniemożliwia to zablokowany lub niedostępny element, aplikacja przechodzi do możliwie największych podfolderów, a na końcu do pojedynczych plików. Elementy, których nie można usunąć, oraz niezbędne foldery nadrzędne pozostają na miejscu.

Elementy w Koszu zachowują atrybuty i pierwotne położenie. Użyj **Przywróć** zamiast ręcznego kopiowania; po częściowej operacji drzewo może być widoczne jako kilka wpisów. `Shift+Delete` działa tak samo, ale usuwa trwale i w razie potrzeby zdejmuje atrybut `Tylko do odczytu`.

`›` oznacza podmenu, `Backspace` wraca, a `Esc` anuluje sekwencję.
