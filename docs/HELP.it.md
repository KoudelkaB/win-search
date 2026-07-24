# Guida di File Search Manager

`F1` apre questa guida nell’applicazione; `F12` aggiorna l’indice NTFS.

## Filtri

- `report` contiene il testo; `:report` inizia con esso; `report:` termina con esso; `:report:` è esatto.
- `.pdf:|.docx:` cerca nomi che terminano con una di queste estensioni; i termini separati da spazi sono in **AND**.
- `src\` cerca la cartella padre; `src\\` cerca nell’intero percorso.
- Gli ancoraggi del nome si applicano a ogni componente del percorso, quindi `:src:\\` trova gli elementi con una cartella chiamata esattamente `src` in qualsiasi punto del percorso.
- `"C:\Work"` cerca direttamente nella cartella; `"C:\Work\\"` cerca ricorsivamente sotto di essa.

Usare le virgolette per percorsi con spazi. `Ctrl+Left` e `Ctrl+Right` percorrono la cronologia; `Down` mostra i suggerimenti.

## Esempi pratici

- **Liberare spazio sul disco** — svuota **Filtro** per mostrare tutti gli elementi indicizzati. Fai clic sull’intestazione **Dimensione** (di nuovo se necessario) finché gli elementi più grandi non sono in alto, esamina l’elenco dall’inizio ed elimina quelli inutili con `Shift+Delete` (in modo permanente, senza passare dal Cestino).
- **Controllare dove scrive un’applicazione** — svuota **Filtro** e fai clic su **Modificato** finché gli elementi modificati più di recente non sono in alto. Avvia o usa quindi l’applicazione da osservare. I file in cui sta scrivendo salgono in cima a ogni modifica e la colonna **Cartella** ne mostra la posizione. Inserisci prima un percorso in **Filtro** se vuoi osservare solo una parte del file system.
- **Cercare testo nei file sorgente** — usa ad esempio `C:\Projects\\ .cs:` per limitare i risultati ai file `.cs` di un progetto. Inserisci il testo cercato in **Cerca** e premi `Enter`.

## Ricerca e tasti

Inserire testo e premere `Enter`. `UTF-8` e `UTF-16` cercano testo; `HEX` cerca byte come `48 65 6C 6C 6F`. `Delete` elimina, `F2` rinomina, `O` apre, `U` estrae e `Z` crea un archivio. `›` apre un sottomenu, `Backspace` torna indietro e `Esc` annulla.

## Eliminazione e ripristino

`Delete` tenta prima di spostare ogni cartella selezionata interamente nel Cestino. Se un elemento bloccato o inaccessibile lo impedisce, l’applicazione continua con le sottocartelle più grandi possibili e infine con i singoli file. Gli elementi non eliminabili e le cartelle superiori necessarie rimangono al loro posto.

Gli elementi nel Cestino conservano attributi e posizione originale. Usa **Ripristina** invece della copia manuale; dopo un’operazione parziale l’albero può comparire come più elementi. `Shift+Delete` usa lo stesso metodo in modo permanente e rimuove l’attributo `Sola lettura` quando necessario.
