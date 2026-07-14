# Guida di File Search Manager

`F1` apre questa guida nell’applicazione; `F12` aggiorna l’indice NTFS.

## Filtri

- `report` contiene il testo; `:report` inizia con esso; `report:` termina con esso; `:report:` è esatto.
- `pdf|docx` accetta un’alternativa; i termini separati da spazi sono in **AND**.
- `src\` cerca la cartella padre; `src\\` cerca nell’intero percorso.
- `"C:\Work"` cerca direttamente nella cartella; `"C:\Work\\"` cerca ricorsivamente sotto di essa.

Usare le virgolette per percorsi con spazi. `Ctrl+Left` e `Ctrl+Right` percorrono la cronologia; `Down` mostra i suggerimenti.

## Ricerca e tasti

Inserire testo e premere `Enter`. `UTF-8` e `UTF-16` cercano testo; `HEX` cerca byte come `48 65 6C 6C 6F`. `Delete` elimina, `F2` rinomina, `O` apre, `U` estrae e `Z` crea un archivio. `›` apre un sottomenu, `Backspace` torna indietro e `Esc` annulla.
