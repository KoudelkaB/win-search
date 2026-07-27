# Guida di File Search Manager

File Search Manager ha due campi principali:

- **Filtro** restringe l’elenco dei file per nome, cartella, percorso o directory selezionate.
- **Cerca** cerca nel contenuto dei file attualmente filtrati.

L’elenco dei risultati è pensato per l’uso da tastiera. Quando ha lo stato attivo, il pannello **Suggerimenti** mostra i comandi disponibili per la selezione.

## Indicizzazione ed elevazione

All’avvio File Search Manager carica i dischi pronti e osserva le modifiche del file system. Ogni disco viene caricato in modo indipendente, quindi un disco di rete lento non rallenta mai gli altri.

Per impostazione predefinita vengono indicizzati solo i dischi NTFS: gli altri file system (condivisioni di rete, chiavette FAT) non possono usare la lettura rapida della MFT e percorrerli file per file può dominare l’intero caricamento. Con il pulsante **💽 Dischi…** nella barra di stato scegli esattamente quali dischi indicizzare; i dischi non selezionati non vengono né analizzati né osservati.

Per i dischi NTFS la via più rapida è leggere la Master File Table di NTFS. File Search Manager può farlo in uno di questi modi:

- Servizio di Windows: nessuna richiesta. Selezionato per impostazione predefinita nel programma di installazione. Eseguendo il programma di installazione della versione già presente viene proposto **Modifica le impostazioni**, che aggiunge o rimuove il servizio senza reinstallare il resto.
- Helper con privilegi elevati: una richiesta UAC per ogni esecuzione dell’applicazione. Senza il servizio viene proposta all’avvio, così l’indicizzazione può iniziare subito. Con il servizio non viene proposta finché non arriva la prima azione che richiede davvero i diritti di amministratore: il tasto `A`, la lettura di file riservati agli amministratori, oppure la rinomina e la modifica delle date su elementi protetti da scrittura. Il pulsante **🛡** accanto a **🌐** la richiede invece all’avvio.
- Applicazione elevata direttamente: esegui File Search Manager come amministratore.

Se nessuna di queste opzioni è disponibile, File Search Manager ripiega sulla scansione delle cartelle. L’applicazione funziona comunque, ma il caricamento iniziale può risultare più lento.

Il servizio è in sola lettura per l’indicizzazione. Espone i dati della MFT all’applicazione desktop e si chiama `WinSearchService`.

## Sintassi dei filtri

I termini del filtro si separano con spazi. Usa le virgolette per termini o percorsi che contengono spazi.

Corrispondenza dei nomi:

- `report` corrisponde ai nomi che contengono `report`.
- `:report` corrisponde ai nomi che iniziano con `report`.
- `report:` corrisponde ai nomi che terminano con `report`.
- `:report:` corrisponde esattamente al nome `report`.
- `.pdf:|.docx:` corrisponde ai nomi che terminano con `.pdf` o `.docx`.
- Più termini sono combinati in AND, quindi `report .pdf:` trova i nomi che contengono `report` e terminano con `.pdf`.

Corrispondenza delle cartelle:

- `src\` corrisponde agli elementi la cui cartella padre immediata si chiama `src`.
- `src\\` corrisponde agli elementi che hanno `src` in qualsiasi punto del percorso completo.
- Gli ancoraggi del nome si applicano a ogni componente del percorso, quindi `:src:\\` trova gli elementi con una cartella chiamata esattamente `src` in qualsiasi punto del percorso.
- `"C:\Work"` corrisponde agli elementi direttamente dentro `C:\Work`.
- `"C:\Work\\"` corrisponde in modo ricorsivo agli elementi sotto `C:\Work`.

Cronologia:

- `Ctrl+Left` e `Ctrl+Right` percorrono la cronologia dei filtri.
- `Down` apre i suggerimenti.
- `Del` rimuove il suggerimento selezionato.
- Tieni premuto `Ctrl` mentre apri i suggerimenti per usare la cronologia più recente invece di quella più usata.

### Filtri bloccati

- Fai clic su **📌 Blocca…** per aggiungere una scheda modificabile alla riga dei filtri bloccati. Digita il nome sul posto e premi `Enter`; `Esc` annulla.
- Fai clic su un filtro con nome per ripristinarne l’espressione salvata. Il filtro con nome attivo è evidenziato.
- Fai clic con il pulsante destro su un filtro con nome per aggiornarlo dall’espressione corrente, rinominarlo sul posto o sbloccarlo.
- I filtri bloccati vengono ripristinati all’avvio dell’applicazione.

**Esporta…** salva tutti i filtri bloccati e le voci del cesto delle destinazioni in un unico file di impostazioni JSON. **Importa…** convalida un file di impostazioni e, dopo conferma, sostituisce i filtri bloccati e le destinazioni correnti con il suo contenuto.

## Ricerca nei contenuti

Digita un termine in **Cerca** e premi `Enter`.

Opzioni di codifica:

- `UTF-8` cerca testo codificato in UTF-8.
- `UTF-16` cerca testo codificato in UTF-16 little-endian.
- `HEX` cerca byte scritti in esadecimale separato da spazi, ad esempio `48 65 6C 6C 6F`.

La casella **Maiuscole/minuscole ignorate** si applica alle ricerche di testo.

Colori dei risultati dopo una ricerca nei contenuti:

- Verde: il contenuto è stato trovato.
- Rosso: il contenuto non è stato trovato.
- Nero: l’elemento non è stato cercato o la ricerca è stata azzerata.
- Blu: cartella.

Digitare qualcosa di diverso da `Enter` nel campo di ricerca azzera lo stato corrente dei risultati della ricerca nei contenuti.

## Azioni con il mouse

- Clic, Ctrl+clic o Maiusc+clic sui risultati per selezionare singoli elementi o intervalli.
- Doppio clic sul nome di un file o di una cartella per filtrare dentro quell’elemento.
- Doppio clic sulla colonna della cartella per aprire Esplora file con l’elemento selezionato.
- Ctrl + clic destro su un risultato lo apre con l’azione predefinita di Windows.
- Clic sull’intestazione di una colonna per ordinare in base a essa.
- Clic destro sugli elementi selezionati per Apri, Apri con dinamico, appunti, rinomina, archiviazione ed eliminazione.
- **Aggiungi al cesto destinazioni** segue la colonna su cui hai fatto clic destro: Nome aggiunge gli elementi selezionati, Cartella aggiunge le loro cartelle padre. La voce è nascosta quando i percorsi corrispondenti non sono destinazioni supportate o sono già nel cesto.
- Trascina dalla colonna Nome per inviare file o directory selezionati a un’altra applicazione.
- Trascina dalla colonna Cartella per inviare le cartelle padre degli elementi selezionati.
- Rilascia i file su una directory nella colonna Nome o su un percorso padre nella colonna Cartella. Scegli copia, spostamento, collegamento simbolico o collegamento fisico.
- Rilascia i file su una cella Nome eseguibile per avviarla con i percorsi rilasciati come argomenti.
- Il rilascio segue le impostazioni predefinite di Esplora file: una cartella sullo stesso volume significa spostamento, un volume diverso significa copia. Tieni premuto `Ctrl` per copiare, `Shift` per spostare o `Alt` per un collegamento simbolico. Più destinazioni significano copia.

## Esempi pratici

- **Liberare spazio sul disco** — svuota **Filtro** per mostrare tutti gli elementi indicizzati. Fai clic sull’intestazione **Dimensione** (di nuovo se necessario) finché gli elementi più grandi non sono in alto, esamina l’elenco dall’inizio ed elimina quelli inutili con `Shift+Delete` (in modo permanente, senza passare dal Cestino).
- **Controllare dove scrive un’applicazione** — svuota **Filtro** e fai clic sull’intestazione **Modificato** finché gli elementi modificati più di recente non sono in alto. Avvia o usa quindi l’applicazione da osservare. I file in cui sta scrivendo salgono in cima a ogni modifica e la colonna **Cartella** ne mostra la posizione. Inserisci prima un percorso in **Filtro** se vuoi osservare solo una parte del file system.
- **Cercare testo nei file sorgente** — usa ad esempio `C:\Projects\\ .cs:` per limitare i risultati ai file `.cs` di un progetto. Inserisci il testo cercato in **Cerca** e premi `Enter`.

## Menu contestuale

Il menu contestuale si adatta alla selezione corrente:

- **Apri con** elenca solo le applicazioni installate compatibili con la selezione. Uno strumento di confronto configurato compare per primo con esattamente due file o directory.
- **Apri cartella contenente** apre Esplora file nella cartella padre dell’elemento.
- **Copia percorso** copia i percorsi completi degli elementi selezionati.
- **7-Zip** compare quando `7zFM.exe` è installato.
- **Zip** crea un archivio ZIP.
- **Crea archivio 7z** compare solo quando `7z.exe` o `7zz.exe` è installato.
- **Estrai** compare per le selezioni di archivi supportati.
- **Sposta nel Cestino** esegue un’eliminazione recuperabile.

### Eliminazione e ripristino

L’eliminazione tenta prima ogni file o cartella selezionata come una singola operazione rapida. Se un elemento bloccato, inaccessibile o comunque non eliminabile impedisce l’operazione su un’intera cartella, File Search Manager prosegue con i sottoalberi rimovibili più grandi e poi con i singoli file. Gli elementi che restano non rimovibili — e le cartelle padre necessarie a contenerli — rimangono al loro posto. Gli errori vengono segnalati dopo che sono stati tentati tutti gli elementi di pari livello raggiungibili.

`Delete` invia nel Cestino ogni elemento elaborato correttamente e ne conserva attributi e posizione originale. Un’operazione parziale può comparire come più voci del Cestino, perché le sottocartelle intatte vengono tenute insieme quando possibile e solo le parti bloccate vengono suddivise ulteriormente. Usa il comando **Ripristina** del Cestino, anziché copiarne manualmente le voci, per riportare ogni elemento al percorso originale.

`Shift+Delete` usa lo stesso percorso, ma elimina in modo permanente. Rimuove l’attributo `Sola lettura` dove necessario; questo comportamento è generale e non si limita ai repository Git. I file bloccati contro l’eliminazione e i percorsi negati dalle autorizzazioni del file system rimangono e vengono inclusi nel rapporto finale degli errori.

Il rilascio di directory e `Ctrl+V` mostrano un selettore di azione. I conflitti di nomi esistenti offrono sovrascrittura, salto, rinomina automatica e applica a tutti. I trasferimenti mostrano l’avanzamento per elemento e possono essere annullati tra le operazioni di primo livello.

## Cesto destinazioni

Il cesto destinazioni in basso conserva destinazioni di rilascio riutilizzabili:

- **+ Nome** aggiunge gli elementi selezionati della colonna Nome. Cartelle, archivi supportati ed eseguibili hanno un comportamento specifico per tipo di destinazione.
- **+ Cartella** aggiunge le cartelle padre degli elementi selezionati.
- Durante un trascinamento la barra mostra due grandi aree di rilascio: **＋ Aggiungi come destinazione** e **📤 Invia a tutte le destinazioni (n)**. Compaiono appena inizia un trascinamento nell’elenco dei risultati, o quando un trascinamento esterno passa sopra la barra, e scompaiono alla fine.
- **Aggiungi come destinazione** aggiunge esattamente i percorsi prodotti dal trascinamento. Un trascinamento iniziato nella colonna Cartella aggiunge quindi le cartelle padre; uno iniziato nella colonna Nome aggiunge gli elementi indicati. Anche il rilascio nello spazio vuoto dopo i chip delle destinazioni aggiunge destinazioni.
- **Invia a tutte le destinazioni** usa ogni destinazione disponibile, mostrando prima un riepilogo quando serve. Il rilascio direttamente su un chip usa invece solo quella destinazione.
- Le destinazioni cartella mostrano il selettore di copia, spostamento, collegamento simbolico e collegamento fisico.
- Le destinazioni archivio aggiungono le origini all’archivio. L’aggiornamento di formati diversi da ZIP richiede `7z.exe` o `7zz.exe` installato.
- Le destinazioni eseguibili vengono avviate con i percorsi di origine come argomenti e richiedono conferma.
- **Invia gli appunti a tutte (n)…** è la stessa operazione con gli appunti come origine; è disattivata finché non sono impostate destinazioni.
- Fai doppio clic su una destinazione per aprirla. Con il clic destro su una destinazione puoi filtrare su di essa, aprirla, rimuoverla o cancellare tutte le destinazioni. Il clic destro sullo sfondo della barra offre anche **Cancella destinazioni**.

Il cesto viene salvato automaticamente tra le esecuzioni dell’applicazione. Le destinazioni mancanti restano nella configurazione salvata, ma vengono ignorate finché non tornano disponibili.

Tutti i comandi di destinazioni e filtri stanno sotto `Alt` e si comportano allo stesso modo ovunque nella finestra: tieni premuto `Alt` per vederli nel pannello **Suggerimenti**, poi premi `N` (aggiungi i nomi selezionati come destinazioni), `F` (aggiungi le cartelle padre), `V` (invia gli appunti a tutte le destinazioni — continua a tenere premuto e aggiungi `L`/`H`/`O` per collegamento, collegamento fisico, sovrascrittura), `C` (cancella destinazioni), `P` (blocca il filtro), `I`/`E` (importa/esporta filtri bloccati e destinazioni). Le sequenze da tastiera trasferiscono direttamente con l’azione scelta; solo il pulsante della barra degli strumenti e i rilasci con il mouse aprono la finestra di scelta dell’azione.

Mentre una sequenza da tastiera è attiva, **Suggerimenti** mostra solo i tasti successivi validi. `Esc` viene offerto solo quando rilasciare i tasti premuti eseguirebbe un’azione e `Alt` non è premuto in quel momento. Vale anche in una sequenza con `Alt` dopo averlo rilasciato tenendo premuto un altro tasto della sequenza. `Backspace` torna sempre indietro di un passo. I controlli di flusso sono separati dalle scelte di comando da una linea.

Le voci che terminano con `›` hanno un ulteriore livello di scelte; tieni premuto un tasto della sequenza e premi quel tasto per mostrare il sottomenu.

Con l’elenco dei risultati attivo, anche le sequenze del tasto `T` gestiscono le destinazioni: `T` aggiunge gli elementi selezionati come destinazioni, `T` `F` aggiunge le loro cartelle padre, `T` `V` invia gli appunti a tutte le destinazioni e `T` `C` cancella le destinazioni. `T` `V` accetta gli stessi modificatori di `V`: `L` invia come collegamenti simbolici, `H` come collegamenti fisici e `O` sovrascrive i file esistenti.

## Comandi da tastiera

Porta lo stato attivo sull’elenco dei risultati e premi un tasto mostrato nel pannello **Suggerimenti**. Alcuni comandi proseguono finché i tasti restano premuti e terminano quando vengono tutti rilasciati.

Comandi comuni:

- `Enter`: filtra dentro le cartelle selezionate.
- `Delete`: sposta gli elementi selezionati nel Cestino dopo conferma.
- `Shift+Delete`: elimina definitivamente gli elementi selezionati senza conferma.
- `Ctrl+C`: copia standard compatibile con la shell.
- `Ctrl+X`: taglia standard compatibile con la shell.
- `Ctrl+V`: incolla dopo aver scelto copia, spostamento, collegamento simbolico o fisico.
- `C`: copia gli elementi selezionati negli appunti. Aggiungendo `V`, `T`, `W` o `A` si copiano invece la versione del file, la data di creazione, quella di ultima scrittura o quella di ultimo accesso; un `+` iniziale accoda agli appunti anziché sostituirli.
- `X`: taglia gli elementi selezionati negli appunti.
- `D`: confronta nello strumento di confronto configurato. Disponibile solo con esattamente due elementi selezionati.
- `V`: incolla i file degli appunti nelle cartelle selezionate o nelle cartelle padre.
- `O`: apri gli elementi selezionati in un’altra applicazione.
- `A`: apri gli elementi selezionati come amministratore.
- `F2`: rinomina un singolo file o cartella fisica direttamente nella cella Nome. Con più elementi usa uno dei comandi di trasformazione qui sotto.
- `F3`: visualizza gli elementi selezionati.
- `F4`: modifica gli elementi selezionati.
- `N`: copia i nomi selezionati.
- `P`: copia i percorsi completi selezionati.
- `F`: copia i percorsi delle cartelle selezionate.
- `M`: mostra una barra integrata per creare una directory nelle cartelle selezionate.
- `S`: comandi di selezione.
- `T`: comandi delle destinazioni — aggiungi la selezione come destinazioni; poi `F` per le cartelle padre, `V` per inviare gli appunti a tutte le destinazioni (con `L`/`H`/`O` per collegamento, collegamento fisico, sovrascrittura), `C` per cancellare le destinazioni.
- `U`: estrai gli archivi selezionati. Aggiungi `NumPad7` per chiamare `7z.exe` invece della gestione integrata.
- `Z`: comprimi gli elementi selezionati. Aggiungi `NumPad7` per chiamare `7z.exe`.
- `F1`: apri questo file della guida. Funziona ovunque nella finestra.
- `F12`: aggiorna da NTFS. Funziona ovunque nella finestra e non richiede una selezione; ogni disco si aggiorna in modo indipendente, quindi un disco di rete lento non rallenta mai gli altri. `F1` e `F12` restano visibili in **Suggerimenti** quando la griglia dei risultati ha lo stato attivo.
- `Right Shift`: riporta lo stato attivo al campo del filtro.

Comandi sotto `Ctrl`:

- `Ctrl+A`: seleziona o deseleziona tutto.
- `Ctrl+D`: filtra nelle cartelle padre degli elementi selezionati.
- `Ctrl+F`: filtra nelle cartelle selezionate.
- `Ctrl+N`: crea nuove cartelle nelle cartelle selezionate.
- `Ctrl+J`: vai all’elemento selezionato successivo; `Ctrl+Shift+J` al precedente.

Destinazioni di apertura dopo `O` o `A`. Ogni voce compare solo se l’applicazione è stata rilevata:

- `B`: Esplora file.
- `W`: browser web predefinito rilevato.
- `C`: Chrome.
- `F`: Firefox.
- `E`: Edge.
- `O`: Opera.
- `I`: Internet Explorer.
- `A`: Adobe Reader.
- `T`: visualizzatore di testo.
- `D`: Visual Studio Code.
- `V`: Visual Studio.
- `Y`: Antigravity.
- `G`: Ghostscript.
- `P`: GhostPCL.
- `X`: GhostXPS.
- `R`: visualizzatore rilevato per i contenuti PRN.
- `S`, poi `P`: PowerShell.
- `S` da solo: prompt dei comandi.

`G`, `P`, `X` e `R` chiedono un valore DPI prima di essere eseguiti.

Comandi di selezione dopo `S`:

- `A`: seleziona o deseleziona tutto.
- `D`: seleziona le directory.
- `F`: seleziona i file.
- `I`: inverti la selezione.
- `G`: seleziona le righe verdi.
- `R`: seleziona le righe rosse.
- `B`: seleziona le righe nere.

Comandi di rinomina e modifica dopo `F2`:

- `V`: prendi il nuovo percorso o nome dagli appunti.
- `N`: cambia nome.
- `E`: cambia estensione; `E` e poi `Delete` la rimuove.
- `.`: aggiungi estensione.
- `Delete`: elimina testo dal nome.
- `F`: aggiungi prefisso.
- `L`: aggiungi suffisso.
- `Insert`: inserisci testo in una posizione.
- `R`: sostituisci testo.
- `C`: cambia data di creazione, poi `V` per la data dagli appunti o `C` per quella corrente.
- `W`: cambia data ultima scrittura, con le stesse scelte `V` e `C`.
- `A`: cambia data ultimo accesso, con le stesse scelte `V` e `C`.
- Anteponi `O` per sovrascrivere le destinazioni esistenti quando è supportato.

Le rinomine e le modifiche delle date su elementi protetti da scrittura (ad esempio tutto ciò che si trova sotto `Program Files` o `Windows`) vengono ritentate automaticamente tramite l’helper con privilegi elevati. Se non è ancora stato avviato, è in quel momento che compare la sua richiesta UAC.

## Dati e risoluzione dei problemi

Lo stato utente è salvato in:

```text
%LOCALAPPDATA%\win-search
```

Se il caricamento NTFS è lento, installa il servizio dal programma di installazione — è selezionato per impostazione predefinita — oppure approva la richiesta di elevazione. La barra di stato indica se ogni disco ha usato il servizio, l’accesso diretto, l’helper amministratore o la scansione delle cartelle.

Se un disco mappato o esterno non è disponibile, File Search Manager lo salta dopo un breve controllo di prontezza, così l’avvio non si blocca su un’unità irraggiungibile.
