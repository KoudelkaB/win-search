# File Search Manager – Hilfe

`F1` öffnet diese Hilfe in der Anwendung, `F12` aktualisiert den NTFS-Index. **Hinweise** zeigt die gültigen Tasten für die Auswahl.

## Filter-Syntax

Filterbegriffe werden mit Leerzeichen getrennt und als **UND** verknüpft. Begriffe oder Pfade mit Leerzeichen stehen in Anführungszeichen.

- `report` – Name enthält `report`; `:report` beginnt damit; `report:` endet damit; `:report:` ist exakt.
- `.pdf:|.docx:` – Dateinamen mit einer dieser Erweiterungen; `report .pdf:` kombiniert beide Bedingungen.
- `src\` – unmittelbarer Elternordner heißt `src`; `src\\` sucht `src` im gesamten Pfad.
- Namensanker gelten für jede Pfadkomponente; `:src:\\` findet daher Elemente mit einem Ordner, der an beliebiger Stelle im Pfad genau `src` heißt.
- `"C:\Work"` – direkt in diesem Ordner; `"C:\Work\\"` – rekursiv darunter.

Beispiele: `invoice .pdf:`, `:IMG_ .jpg:|.jpeg:`, `C:\Projects\\ .cs:`. `Ctrl+Left` und `Ctrl+Right` wechseln die Filterhistorie; `Down` zeigt Vorschläge.

## Praktische Beispiele

- **Speicherplatz freigeben** – leeren Sie **Filter**, um alle indizierten Elemente anzuzeigen. Klicken Sie auf **Größe** (bei Bedarf erneut), bis die größten Elemente oben stehen, gehen Sie die Liste von oben nach unten durch und löschen Sie unnötige Elemente mit `Shift+Delete` (dauerhaft, ohne Papierkorb).
- **Überwachen, wohin eine Anwendung schreibt** – leeren Sie **Filter** und klicken Sie auf **Geändert**, bis die zuletzt geänderten Elemente oben stehen. Starten oder verwenden Sie dann die zu beobachtende Anwendung. Dateien, in die sie gerade schreibt, wandern bei jeder Änderung nach oben; die Spalte **Ordner** zeigt den Speicherort. Geben Sie zuerst einen Pfad in **Filter** ein, wenn Sie nur einen Teil des Dateisystems beobachten möchten.
- **Text in Quelldateien suchen** – verwenden Sie zum Beispiel `C:\Projects\\ .cs:`, um die Ergebnisse auf `.cs`-Dateien in einem Projekt zu begrenzen. Geben Sie den gesuchten Text in **Suche** ein und drücken Sie `Enter`.

## Inhaltssuche und Tastatur

Text eingeben und `Enter` drücken. `UTF-8` und `UTF-16` suchen Text, `HEX` sucht Leerzeichen-getrennte Bytes, etwa `48 65 6C 6C 6F`. Grün bedeutet gefunden, Rot nicht gefunden, Schwarz nicht durchsucht, Blau Ordner.

- `Enter`: in ausgewählte Ordner filtern; `Delete` / `Shift+Delete`: Papierkorb / endgültig löschen.
- `Ctrl+C`, `Ctrl+X`, `Ctrl+V`: Kopieren, Ausschneiden, Einfügen; `F2`, `F3`, `F4`: Umbenennen, anzeigen, bearbeiten.
- `O` / `A`: öffnen / als Administrator; `U` / `Z`: entpacken / Archiv erzeugen.

`›` bedeutet Untermenü; `Backspace` geht zurück und `Esc` bricht eine Tastensequenz ab.
