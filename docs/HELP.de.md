# File Search Manager – Hilfe

`F1` öffnet diese Hilfe in der Anwendung, `F12` aktualisiert den NTFS-Index. **Hinweise** zeigt die gültigen Tasten für die Auswahl.

## Filter-Syntax

Filterbegriffe werden mit Leerzeichen getrennt und als **UND** verknüpft. Begriffe oder Pfade mit Leerzeichen stehen in Anführungszeichen.

- `report` – Name enthält `report`; `:report` beginnt damit; `report:` endet damit; `:report:` ist exakt.
- `pdf|docx` – eine Alternative; `report pdf:` kombiniert beide Bedingungen.
- `src\` – unmittelbarer Elternordner heißt `src`; `src\\` sucht `src` im gesamten Pfad.
- `"C:\Work"` – direkt in diesem Ordner; `"C:\Work\\"` – rekursiv darunter.

Beispiele: `invoice pdf:`, `:IMG_ jpg|jpeg`, `"C:\Projects\\" cs:`. `Ctrl+Left` und `Ctrl+Right` wechseln die Filterhistorie; `Down` zeigt Vorschläge.

## Inhaltssuche und Tastatur

Text eingeben und `Enter` drücken. `UTF-8` und `UTF-16` suchen Text, `HEX` sucht Leerzeichen-getrennte Bytes, etwa `48 65 6C 6C 6F`. Grün bedeutet gefunden, Rot nicht gefunden, Schwarz nicht durchsucht, Blau Ordner.

- `Enter`: in ausgewählte Ordner filtern; `Delete` / `Shift+Delete`: Papierkorb / endgültig löschen.
- `Ctrl+C`, `Ctrl+X`, `Ctrl+V`: Kopieren, Ausschneiden, Einfügen; `F2`, `F3`, `F4`: Umbenennen, anzeigen, bearbeiten.
- `O` / `A`: öffnen / als Administrator; `U` / `Z`: entpacken / Archiv erzeugen.

`›` bedeutet Untermenü; `Backspace` geht zurück und `Esc` bricht eine Tastensequenz ab.
