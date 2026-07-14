# Pomoc File Search Manager

`F1` otwiera tę pomoc w aplikacji, a `F12` odświeża indeks NTFS. **Skróty** pokazują klawisze dostępne dla bieżącego wyboru.

## Składnia filtrów

Terminy są oddzielane spacjami i łączone jako **AND**. Terminy lub ścieżki ze spacjami należy ująć w cudzysłów.

- `report` zawiera tekst; `:report` zaczyna się od niego; `report:` kończy się nim; `:report:` jest dokładny.
- `pdf|docx` oznacza jedną z alternatyw; `report pdf:` łączy warunki.
- `src\` wskazuje bezpośredni folder nadrzędny; `src\\` wyszukuje w całej ścieżce.
- `"C:\Work"` wskazuje elementy bezpośrednio w folderze; `"C:\Work\\"` wskazuje elementy rekurencyjnie poniżej.

Przykłady: `invoice pdf:`, `:IMG_ jpg|jpeg`, `"C:\Projects\\" cs:`. `Ctrl+Left` i `Ctrl+Right` przechodzą po historii, a `Down` pokazuje podpowiedzi.

## Szukanie i klawiatura

Wpisz tekst i naciśnij `Enter`. `UTF-8` i `UTF-16` szukają tekstu; `HEX` szuka bajtów oddzielonych spacjami, np. `48 65 6C 6C 6F`. Zielony oznacza znaleziono, czerwony nie znaleziono, czarny nie przeszukano, a niebieski folder.

- `Enter` filtruje foldery; `Delete` / `Shift+Delete` przenosi do Kosza / usuwa trwale.
- `Ctrl+C`, `Ctrl+X`, `Ctrl+V` kopiują, wycinają i wklejają; `F2`, `F3`, `F4` zmieniają nazwę, wyświetlają i edytują.
- `O` / `A` otwiera / otwiera jako administrator; `U` / `Z` rozpakowuje / tworzy archiwum.

`›` oznacza podmenu, `Backspace` wraca, a `Esc` anuluje sekwencję.
