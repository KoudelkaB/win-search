# File Search Manager – Hilfe

File Search Manager hat zwei Hauptfelder:

- **Filter** grenzt die Dateiliste nach Name, Ordner, Pfad oder ausgewählten Verzeichnissen ein.
- **Suchen** durchsucht die Dateiinhalte innerhalb der aktuell gefilterten Dateien.

Die Ergebnisliste ist für die Tastaturbedienung ausgelegt. Hat die Liste den Fokus, zeigt das Feld **Hinweise** die Befehle, die für die Auswahl gerade verfügbar sind.

## Indizierung und Rechteerhöhung

Beim Start lädt File Search Manager die bereiten Laufwerke und überwacht Dateisystemänderungen. Jedes Laufwerk wird unabhängig geladen, sodass ein langsames Netzlaufwerk die anderen nie aufhält.

Standardmäßig werden nur NTFS-Laufwerke indiziert – andere Dateisysteme (Netzwerkeinbindungen, FAT-Sticks) können das schnelle MFT-Lesen nicht nutzen, und sie Datei für Datei zu durchlaufen kann den gesamten Ladevorgang dominieren. Über die Schaltfläche **💽 Laufwerke…** in der Statusleiste wählen Sie genau aus, welche Laufwerke indiziert werden; nicht markierte Laufwerke werden weder durchsucht noch überwacht.

Bei NTFS-Laufwerken ist das Lesen der NTFS-Master File Table der schnellste Weg. File Search Manager kann das auf eine dieser Arten tun:

- Windows-Dienst: überhaupt keine Abfrage. Im Installationsprogramm standardmäßig ausgewählt.
- Erhöhter Helfer: eine UAC-Abfrage pro Programmlauf. Ohne den Dienst wird sie beim Start angeboten, damit die Indizierung sofort beginnen kann. Mit dem Dienst wird sie erst bei der ersten Aktion angeboten, die tatsächlich Administratorrechte benötigt – die Taste `A` oder das Lesen von Dateien, die nur Administratoren zugänglich sind. Die Schaltfläche **🛡** neben **🌐** fragt stattdessen beim Start.
- Direkt erhöhte Anwendung: File Search Manager als Administrator ausführen.

Ist nichts davon verfügbar, weicht File Search Manager auf das Durchlaufen der Ordner aus. Die Anwendung funktioniert weiterhin, aber der erste Ladevorgang kann langsamer sein.

Der Dienst ist für die Indizierung schreibgeschützt. Er stellt der Desktopanwendung MFT-Daten bereit und heißt `WinSearchService`.

## Filter-Syntax

Filterbegriffe werden durch Leerzeichen getrennt. Begriffe oder Pfade mit Leerzeichen setzen Sie in Anführungszeichen.

Namensabgleich:

- `report` trifft auf Namen zu, die `report` enthalten.
- `:report` trifft auf Namen zu, die mit `report` beginnen.
- `report:` trifft auf Namen zu, die auf `report` enden.
- `:report:` trifft genau auf den Namen `report` zu.
- `.pdf:|.docx:` trifft auf Namen zu, die auf `.pdf` oder `.docx` enden.
- Mehrere Begriffe werden mit UND verknüpft, `report .pdf:` findet also Namen, die `report` enthalten und auf `.pdf` enden.

Ordnerabgleich:

- `src\` trifft auf Elemente zu, deren unmittelbarer übergeordneter Ordner `src` heißt.
- `src\\` trifft auf Elemente zu, die `src` an beliebiger Stelle im vollständigen Pfad haben.
- Namensanker gelten je Pfadkomponente, `:src:\\` findet also Elemente mit einem Ordner, der an beliebiger Stelle im Pfad genau `src` heißt.
- `"C:\Work"` trifft auf Elemente direkt in `C:\Work` zu.
- `"C:\Work\\"` trifft rekursiv auf Elemente unterhalb von `C:\Work` zu.

Verlauf:

- `Ctrl+Left` und `Ctrl+Right` bewegen sich durch den Filterverlauf.
- `Down` öffnet die Vorschläge.
- `Del` entfernt den ausgewählten Vorschlag.
- Halten Sie beim Öffnen der Vorschläge `Ctrl`, um den neuesten statt des meistgenutzten Verlaufs zu verwenden.

### Angeheftete Filter

- Klicken Sie auf **📌 Anheften…**, um der Reihe der angehefteten Filter eine bearbeitbare Registerkarte hinzuzufügen. Geben Sie den Namen direkt ein und drücken Sie `Enter`; `Esc` bricht ab.
- Klicken Sie auf einen benannten Filter, um seinen gespeicherten Ausdruck wiederherzustellen. Der aktive benannte Filter ist hervorgehoben.
- Mit einem Rechtsklick auf einen benannten Filter aktualisieren Sie ihn aus dem aktuellen Ausdruck, benennen ihn direkt um oder lösen ihn.
- Angeheftete Filter werden beim Start der Anwendung wiederhergestellt.

**Exportieren…** speichert alle angehefteten Filter und Einträge des Zielkorbs in einer JSON-Einstellungsdatei. **Importieren…** prüft eine Einstellungsdatei und ersetzt nach Bestätigung die aktuellen angehefteten Filter und Ziele durch deren Inhalt.

## Inhaltssuche

Geben Sie einen Begriff in **Suchen** ein und drücken Sie `Enter`.

Kodierungsoptionen:

- `UTF-8` sucht als UTF-8 kodierten Text.
- `UTF-16` sucht als UTF-16 Little-Endian kodierten Text.
- `HEX` sucht Bytes in durch Leerzeichen getrennter Hexadezimalschreibweise, zum Beispiel `48 65 6C 6C 6F`.

Das Kontrollkästchen **Groß-/Kleinschreibung ignorieren** gilt für Textsuchen.

Ergebnisfarben nach einer Inhaltssuche:

- Grün: der Inhalt wurde gefunden.
- Rot: der Inhalt wurde nicht gefunden.
- Schwarz: das Element wurde nicht durchsucht oder die Suche wurde zurückgesetzt.
- Blau: Ordner.

Jede andere Eingabe als `Enter` im Suchfeld setzt den aktuellen Ergebniszustand der Inhaltssuche zurück.

## Mausaktionen

- Klick, Strg+Klick oder Umschalt+Klick auf Ergebnisse wählt einzelne Elemente oder Bereiche aus.
- Ein Doppelklick auf einen Datei- oder Ordnernamen filtert in dieses Element hinein.
- Ein Doppelklick auf die Ordnerspalte öffnet den Explorer mit ausgewähltem Element.
- Strg + Rechtsklick auf ein Ergebnis öffnet es mit der Windows-Standardaktion.
- Ein Klick auf eine Spaltenüberschrift sortiert nach dieser Spalte.
- Ein Rechtsklick auf ausgewählte Elemente bietet Öffnen, dynamisches Öffnen mit, Zwischenablage, Umbenennen, Archivieren und Löschen.
- **Zum Zielkorb hinzufügen** richtet sich nach der angeklickten Spalte: Name fügt die ausgewählten Elemente hinzu, Ordner deren übergeordnete Ordner. Es ist ausgeblendet, wenn die entsprechenden Pfade keine unterstützten Ziele sind oder bereits im Korb liegen.
- Ziehen aus der Spalte Name sendet ausgewählte Dateien oder Verzeichnisse an eine andere Anwendung.
- Ziehen aus der Spalte Ordner sendet die übergeordneten Ordner der ausgewählten Elemente.
- Legen Sie Dateien auf einem Verzeichnis in der Spalte Name oder einem übergeordneten Pfad in der Spalte Ordner ab. Wählen Sie Kopieren, Verschieben, symbolische Verknüpfung oder harte Verknüpfung.
- Werden Dateien auf einer ausführbaren Namenszelle abgelegt, wird diese mit den abgelegten Pfaden als Argumenten gestartet.
- Das Ablegen folgt den Explorer-Standards: ein Ordner auf demselben Volume bedeutet Verschieben, ein anderes Volume bedeutet Kopieren. Halten Sie `Ctrl` für Kopieren, `Shift` für Verschieben oder `Alt` für eine symbolische Verknüpfung. Mehrere Ziele bedeuten Kopieren.

## Praktische Beispiele

- **Speicherplatz freigeben** – leeren Sie **Filter**, um alle indizierten Elemente zu sehen. Klicken Sie auf die Überschrift **Größe** (nötigenfalls erneut), bis die größten Elemente oben stehen, prüfen Sie die Liste von oben nach unten und löschen Sie Überflüssiges mit `Shift+Delete` (endgültig, ohne Umweg über den Papierkorb).
- **Überwachen, wohin eine Anwendung schreibt** – leeren Sie **Filter** und klicken Sie auf die Überschrift **Geändert**, bis die zuletzt geänderten Elemente oben stehen. Starten oder benutzen Sie dann die zu beobachtende Anwendung. Dateien, in die sie gerade schreibt, wandern bei jeder Änderung nach oben, und die Spalte **Ordner** zeigt ihren Ort. Geben Sie zuerst einen Pfad in **Filter** ein, wenn Sie nur einen Teil des Dateisystems beobachten möchten.
- **Text in Quelldateien suchen** – mit `C:\Projects\\ .cs:` beschränken Sie die Ergebnisse etwa auf `.cs`-Dateien eines Projekts. Geben Sie den gewünschten Text in **Suchen** ein und drücken Sie `Enter`.

## Kontextmenü

Das Kontextmenü passt sich der aktuellen Auswahl an:

- **Öffnen mit** listet nur installierte Anwendungen auf, die zur Auswahl passen. Ein konfiguriertes Vergleichswerkzeug steht bei genau zwei Dateien oder Verzeichnissen an erster Stelle.
- **Enthaltenden Ordner öffnen** öffnet den Explorer im übergeordneten Ordner des Elements.
- **Pfad kopieren** kopiert die vollständigen Pfade der ausgewählten Elemente.
- **7-Zip** erscheint, wenn `7zFM.exe` installiert ist.
- **Zip** erstellt ein ZIP-Archiv.
- **7z-Archiv erstellen** erscheint nur, wenn `7z.exe` oder `7zz.exe` installiert ist.
- **Entpacken** erscheint bei unterstützten Archivauswahlen.
- **In den Papierkorb verschieben** führt ein wiederherstellbares Löschen aus.

### Löschen und Wiederherstellen

Beim Löschen wird zuerst jede ausgewählte Datei oder jeder Ordner als eine schnelle Operation versucht. Verhindert ein gesperrtes, unzugängliches oder anderweitig nicht löschbares Element die Operation über einen ganzen Ordner, macht File Search Manager mit den größten entfernbaren Teilbäumen und danach mit einzelnen Dateien weiter. Elemente, die sich weiterhin nicht entfernen lassen – und die übergeordneten Ordner, die sie enthalten müssen – bleiben bestehen. Fehler werden gemeldet, nachdem alle erreichbaren Geschwister versucht wurden.

`Delete` schickt jedes erfolgreich verarbeitete Element in den Papierkorb und bewahrt seine Attribute und den ursprünglichen Ort. Eine Teiloperation kann als mehrere Papierkorbeinträge erscheinen, weil unversehrte Unterordner nach Möglichkeit zusammengehalten und nur blockierte Teile weiter aufgeteilt werden. Verwenden Sie den Befehl **Wiederherstellen** des Papierkorbs statt manuellen Kopierens, um jedes Element an seinen ursprünglichen Pfad zurückzubringen.

`Shift+Delete` nutzt denselben Ablauf, löscht aber endgültig. Wo nötig, entfernt es das Attribut `Schreibgeschützt`; dieses Verhalten ist allgemein und nicht auf Git-Repositorys beschränkt. Gegen Löschen gesperrte Dateien und durch Dateisystemrechte verwehrte Pfade bleiben bestehen und stehen im abschließenden Fehlerbericht.

Das Ablegen von Verzeichnissen und `Ctrl+V` zeigen eine Aktionsauswahl. Bei Namenskonflikten stehen Überschreiben, Überspringen, automatisches Umbenennen und Auf alle anwenden zur Verfügung. Übertragungen zeigen den Fortschritt je Element und lassen sich zwischen Operationen der obersten Ebene abbrechen.

## Zielkorb

Der Zielkorb am unteren Rand hält wiederverwendbare Ablageziele bereit:

- **+ Name** fügt ausgewählte Elemente der Spalte Name hinzu. Ordner, unterstützte Archive und ausführbare Dateien haben zielspezifisches Verhalten.
- **+ Ordner** fügt die übergeordneten Ordner der ausgewählten Elemente hinzu.
- Während eines Ziehvorgangs zeigt die Leiste zwei große Ablagezonen: **＋ Als Ziel hinzufügen** und **📤 An alle (n) Ziele senden**. Sie erscheinen, sobald ein Ziehvorgang in der Ergebnisliste beginnt oder ein externer Ziehvorgang über der Leiste schwebt, und verschwinden am Ende.
- **Als Ziel hinzufügen** fügt genau die Pfade hinzu, die der Ziehvorgang erzeugt hat. Ein in der Spalte Ordner begonnener Ziehvorgang fügt daher übergeordnete Ordner hinzu, ein in der Spalte Name begonnener die benannten Elemente. Auch das Ablegen auf leerem Raum hinter den Ziel-Chips fügt Ziele hinzu.
- **An alle senden** nutzt jedes verfügbare Ziel und zeigt bei Bedarf zuvor eine Zusammenfassung. Das Ablegen direkt auf einem Chip nutzt stattdessen nur dieses Ziel.
- Ordnerziele zeigen die Auswahl aus Kopieren, Verschieben, symbolischer und harter Verknüpfung.
- Archivziele fügen die Quellen dem Archiv hinzu. Das Aktualisieren anderer Formate als ZIP erfordert ein installiertes `7z.exe` oder `7zz.exe`.
- Ausführbare Ziele werden mit den Quellpfaden als Argumenten gestartet und erfordern eine Bestätigung.
- **Zwischenablage an alle (n) senden…** ist dieselbe Operation mit der Zwischenablage als Quelle; sie ist deaktiviert, solange keine Ziele gesetzt sind.
- Ein Doppelklick öffnet ein Ziel. Ein Rechtsklick auf ein Ziel filtert dorthin, öffnet es, entfernt es oder löscht alle Ziele. Ein Rechtsklick auf den Leistenhintergrund bietet ebenfalls **Ziele löschen**.

Der Korb wird zwischen Programmläufen automatisch gespeichert. Fehlende Ziele bleiben in der gespeicherten Konfiguration, werden aber übersprungen, bis sie wieder verfügbar sind.

Alle Ziel- und Filterbefehle liegen unter `Alt` und verhalten sich überall im Fenster gleich: Halten Sie `Alt`, um sie im Feld **Hinweise** zu sehen, und drücken Sie dann `N` (ausgewählte Namen als Ziele hinzufügen), `F` (übergeordnete Ordner hinzufügen), `V` (Zwischenablage an alle Ziele senden – weiter halten und `L`/`H`/`O` für Verknüpfung, harte Verknüpfung, Überschreiben ergänzen), `C` (Ziele löschen), `P` (Filter anheften), `I`/`E` (angeheftete Filter und Ziele importieren/exportieren). Tastenfolgen übertragen direkt mit der gewählten Aktion; nur die Symbolleisten-Schaltfläche und Mausablagen öffnen den Aktionsauswahldialog.

Während eine Tastenfolge aktiv ist, zeigt **Hinweise** nur gültige Folgetasten. `Esc` wird nur angeboten, wenn das Loslassen der gehaltenen Tasten eine Aktion ausführen würde und `Alt` gerade nicht gehalten wird. Das gilt auch in einer `Alt`-Folge, nachdem `Alt` losgelassen wurde, während eine andere Folgetaste gehalten bleibt. `Backspace` geht immer einen Schritt zurück. Ablaufsteuerungen sind von den Befehlsoptionen durch eine Linie getrennt.

Einträge, die auf `›` enden, haben eine weitere Ebene von Befehlsoptionen; halten Sie eine Folgetaste und drücken Sie diese Taste, um das Untermenü anzuzeigen.

Bei fokussierter Ergebnisliste verwalten auch die `T`-Tastenfolgen Ziele: `T` fügt die ausgewählten Elemente als Ziele hinzu, `T` `F` deren übergeordnete Ordner, `T` `V` sendet die Zwischenablage an alle Ziele und `T` `C` löscht die Ziele. `T` `V` nimmt dieselben Zusatztasten wie `V`: `L` sendet als symbolische Verknüpfungen, `H` als harte Verknüpfungen und `O` überschreibt vorhandene Dateien.

## Tastaturbefehle

Fokussieren Sie die Ergebnisliste und drücken Sie eine im Feld **Hinweise** gezeigte Taste. Manche Befehle laufen weiter, solange Tasten gehalten werden, und schließen ab, wenn alle losgelassen sind.

Häufige Befehle:

- `Enter`: in ausgewählte Ordner filtern.
- `Delete`: ausgewählte Elemente nach Bestätigung in den Papierkorb verschieben.
- `Shift+Delete`: ausgewählte Elemente ohne Bestätigung endgültig löschen.
- `Ctrl+C`: standardmäßiges, shell-kompatibles Kopieren.
- `Ctrl+X`: standardmäßiges, shell-kompatibles Ausschneiden.
- `Ctrl+V`: einfügen nach Wahl von Kopieren, Verschieben, symbolischer oder harter Verknüpfung.
- `C`: ausgewählte Elemente in die Zwischenablage kopieren. Mit `V`, `T`, `W` oder `A` werden stattdessen Dateiversion, Erstellungszeit, letzte Änderungszeit oder letzte Zugriffszeit kopiert; ein vorangestelltes `+` hängt an die Zwischenablage an, statt sie zu ersetzen.
- `X`: ausgewählte Elemente in die Zwischenablage ausschneiden.
- `D`: im konfigurierten Vergleichswerkzeug vergleichen. Nur verfügbar, wenn genau zwei Elemente ausgewählt sind.
- `V`: Dateien aus der Zwischenablage in die ausgewählten oder übergeordneten Ordner einfügen.
- `O`: ausgewählte Elemente in einer anderen Anwendung öffnen.
- `A`: ausgewählte Elemente als Administrator öffnen.
- `F2`: eine einzelne physische Datei oder einen Ordner direkt in der Namenszelle umbenennen. Bei mehreren Elementen verwenden Sie einen der Umwandlungsbefehle unten.
- `F3`: ausgewählte Elemente anzeigen.
- `F4`: ausgewählte Elemente bearbeiten.
- `N`: ausgewählte Namen kopieren.
- `P`: ausgewählte vollständige Pfade kopieren.
- `F`: ausgewählte Ordnerpfade kopieren.
- `M`: eine eingebettete Leiste zum Anlegen eines Verzeichnisses in den ausgewählten Ordnern anzeigen.
- `S`: Auswahlbefehle.
- `T`: Zielbefehle – Auswahl als Ziele hinzufügen; danach `F` für übergeordnete Ordner, `V` zum Senden der Zwischenablage an alle Ziele (mit `L`/`H`/`O` für Verknüpfung, harte Verknüpfung, Überschreiben), `C` zum Löschen der Ziele.
- `U`: ausgewählte Archive entpacken. Mit `NumPad7` wird `7z.exe` statt der integrierten Verarbeitung aufgerufen.
- `Z`: ausgewählte Elemente packen. Mit `NumPad7` wird `7z.exe` aufgerufen.
- `F1`: diese Hilfedatei öffnen. Funktioniert überall im Fenster.
- `F12`: aus NTFS aktualisieren. Funktioniert überall im Fenster und braucht keine Auswahl; jedes Laufwerk wird unabhängig aktualisiert, sodass ein langsames Netzlaufwerk die anderen nie aufhält. `F1` und `F12` bleiben in **Hinweise** sichtbar, wenn das Ergebnisraster den Fokus hat.
- `Right Shift`: Fokus zurück in das Filterfeld setzen.

Befehle unter `Ctrl`:

- `Ctrl+A`: alles aus- oder abwählen.
- `Ctrl+D`: in die übergeordneten Ordner der ausgewählten Elemente filtern.
- `Ctrl+F`: in die ausgewählten Ordner filtern.
- `Ctrl+N`: neue Ordner in den ausgewählten Ordnern anlegen.
- `Ctrl+J`: zum nächsten ausgewählten Element springen; `Ctrl+Shift+J` zum vorherigen.

Öffnen-Ziele nach `O` oder `A`. Jeder Eintrag erscheint nur, wenn die Anwendung erkannt wurde:

- `B`: Explorer.
- `W`: erkannter Standard-Webbrowser.
- `C`: Chrome.
- `F`: Firefox.
- `E`: Edge.
- `O`: Opera.
- `I`: Internet Explorer.
- `A`: Adobe Reader.
- `T`: Textanzeige.
- `D`: Visual Studio Code.
- `V`: Visual Studio.
- `Y`: Antigravity.
- `G`: Ghostscript.
- `P`: GhostPCL.
- `X`: GhostXPS.
- `R`: für PRN-Inhalte erkannte Anzeige.
- `S`, dann `P`: PowerShell.
- `S` allein: Eingabeaufforderung.

`G`, `P`, `X` und `R` fragen vor dem Start nach einem DPI-Wert.

Auswahlbefehle nach `S`:

- `A`: alles aus- oder abwählen.
- `D`: Verzeichnisse auswählen.
- `F`: Dateien auswählen.
- `I`: Auswahl umkehren.
- `G`: grüne Zeilen auswählen.
- `R`: rote Zeilen auswählen.
- `B`: schwarze Zeilen auswählen.

Umbenennen-/Änderungsbefehle nach `F2`:

- `V`: neuen Pfad oder Namen aus der Zwischenablage übernehmen.
- `N`: Namen ändern.
- `E`: Erweiterung ändern; `E` und dann `Delete` entfernt die Erweiterung.
- `.`: Erweiterung hinzufügen.
- `Delete`: Text aus dem Namen löschen.
- `F`: Präfix hinzufügen.
- `L`: Suffix hinzufügen.
- `Insert`: Text an einer Position einfügen.
- `R`: Text ersetzen.
- `C`: Erstellungszeit ändern, danach `V` für die Zeit aus der Zwischenablage oder `C` für die aktuelle Zeit.
- `W`: letzte Änderungszeit ändern, mit denselben Auswahlmöglichkeiten `V` und `C`.
- Stellen Sie `O` voran, um vorhandene Ziele zu überschreiben, sofern unterstützt.

## Daten und Fehlerbehebung

Der Benutzerzustand wird gespeichert unter:

```text
%LOCALAPPDATA%\win-search
```

Ist das NTFS-Laden langsam, installieren Sie den Dienst aus dem Installationsprogramm – er ist standardmäßig ausgewählt – oder bestätigen Sie die Abfrage zur Rechteerhöhung. Die Statusleiste meldet, ob ein Laufwerk den Dienst, direkten Zugriff, den Administrator-Helfer oder das Durchlaufen der Ordner genutzt hat.

Ist ein verbundenes oder externes Laufwerk nicht verfügbar, überspringt File Search Manager es nach einer kurzen Bereitschaftsprüfung, damit der Start nicht an unerreichbarem Speicher hängen bleibt.
