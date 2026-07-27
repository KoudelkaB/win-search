# Nápověda File Search Manager

File Search Manager má dvě hlavní pole:

- **Filtr** zužuje seznam souborů podle názvu, složky, cesty nebo vybraných adresářů.
- **Hledat** prohledává obsah souborů uvnitř právě vyfiltrovaných položek.

Seznam výsledků je navržen pro ovládání klávesnicí. Když má seznam fokus, panel **Nápověda** ukazuje příkazy, které jsou pro aktuální výběr dostupné.

## Indexování a elevace

Při spuštění File Search Manager načte připravené disky a sleduje změny v systému souborů. Každý disk se načítá nezávisle, takže pomalý síťový disk nikdy nezdržuje ostatní.

Ve výchozím stavu se indexují jen disky NTFS – ostatní systémy souborů (síťová připojení, FAT flash disky) nemohou využít rychlé čtení MFT a jejich procházení soubor po souboru může ovládnout celé načítání. Tlačítkem **💽 Disky…** ve stavovém řádku zvolíte přesně to, které disky se indexují; neoznačené disky se ani neprocházejí, ani nesledují.

U disků NTFS je nejrychlejší cestou čtení hlavní tabulky souborů (MFT). File Search Manager to zvládne jedním z těchto způsobů:

- Služba Windows: žádná výzva. V instalátoru je vybraná ve výchozím stavu. Spuštění instalátoru verze, kterou už máte nainstalovanou, nabídne volbu **Upravit nastavení**, kde lze službu přidat nebo odebrat bez přeinstalace.
- Elevovaný pomocník: jedna výzva UAC na spuštění aplikace. Bez služby se nabídne při startu, aby indexování mohlo začít okamžitě. Se službou se nenabídne dřív než při první akci, která práva správce skutečně potřebuje – klávesa `A`, čtení souborů přístupných jen správci nebo přejmenování a změna časů u položek chráněných proti zápisu. Tlačítko **🛡** vedle **🌐** se místo toho zeptá už při spuštění.
- Přímo elevovaná aplikace: spusťte File Search Manager jako správce.

Pokud není dostupné nic z toho, File Search Manager přejde na procházení složek. Aplikace funguje dál, ale úvodní načtení může být pomalejší.

Služba je pro indexování jen pro čtení. Zpřístupňuje data MFT desktopové aplikaci a jmenuje se `WinSearchService`.

## Syntaxe filtrů

Výrazy filtru se oddělují mezerami. Výrazy a cesty obsahující mezery uzavřete do uvozovek.

Porovnávání názvů:

- `report` odpovídá názvům obsahujícím `report`.
- `:report` odpovídá názvům začínajícím na `report`.
- `report:` odpovídá názvům končícím na `report`.
- `:report:` odpovídá přesnému názvu `report`.
- `.pdf:|.docx:` odpovídá názvům končícím na `.pdf` nebo `.docx`.
- Více výrazů se kombinuje jako AND, takže `report .pdf:` najde názvy obsahující `report` a končící na `.pdf`.

Porovnávání složek:

- `src\` odpovídá položkám, jejichž bezprostřední nadřazená složka se jmenuje `src`.
- `src\\` odpovídá položkám, které mají `src` kdekoli v celé cestě.
- Kotvy názvu platí pro každou složku cesty, takže `:src:\\` najde položky se složkou pojmenovanou přesně `src` kdekoli v cestě.
- `"C:\Work"` odpovídá položkám přímo v `C:\Work`.
- `"C:\Work\\"` odpovídá položkám rekurzivně pod `C:\Work`.

Historie:

- `Ctrl+Left` a `Ctrl+Right` procházejí historii filtrů.
- `Down` otevře návrhy.
- `Del` odebere vybraný návrh.
- Podržte `Ctrl` při otevírání návrhů, chcete-li místo nejčastěji použité historie použít nejnovější.

### Připnuté filtry

- Klepnutím na **📌 Připnout…** přidáte upravitelnou kartu do řady připnutých filtrů. Název napište přímo na místě a stiskněte `Enter`; `Esc` akci zruší.
- Klepnutím na pojmenovaný filtr obnovíte jeho uložený výraz. Aktivní pojmenovaný filtr je zvýrazněn.
- Klepnutím pravým tlačítkem na pojmenovaný filtr jej aktualizujete podle aktuálního výrazu, přejmenujete na místě nebo odepnete.
- Připnuté filtry se obnoví při spuštění aplikace.

**Exportovat…** uloží všechny připnuté filtry a položky košíku cílů do jednoho souboru nastavení JSON. **Importovat…** soubor nastavení ověří a po potvrzení nahradí aktuální připnuté filtry a cíle jeho obsahem.

## Hledání v obsahu

Zadejte výraz do pole **Hledat** a stiskněte `Enter`.

Volby kódování:

- `UTF-8` hledá text kódovaný v UTF-8.
- `UTF-16` hledá text kódovaný v UTF-16 little-endian.
- `HEX` hledá bajty zapsané šestnáctkově a oddělené mezerami, například `48 65 6C 6C 6F`.

Zaškrtávací pole **Nerozlišovat velikost písmen** platí pro textová hledání.

Barvy výsledků po hledání v obsahu:

- Zelená: obsah byl nalezen.
- Červená: obsah nebyl nalezen.
- Černá: položka nebyla prohledána nebo bylo hledání vymazáno.
- Modrá: složka.

Napsání čehokoli jiného než `Enter` do pole hledání vymaže aktuální stav výsledků hledání v obsahu.

## Práce s myší

- Klepnutím, Ctrl+klepnutím nebo Shift+klepnutím na výsledky vyberete jednotlivé položky nebo rozsahy.
- Dvojitým klepnutím na název souboru nebo složky se do dané položky zafiltrujete.
- Dvojitým klepnutím na sloupec složky otevřete Průzkumníka s vybranou položkou.
- Ctrl + klepnutí pravým tlačítkem na výsledek jej otevře výchozí akcí Windows.
- Klepnutím na záhlaví sloupce seřadíte podle daného sloupce.
- Klepnutím pravým tlačítkem na vybrané položky vyvoláte akce Otevřít, dynamické Otevřít v programu, schránku, přejmenování, archivaci a mazání.
- **Přidat do košíku cílů** se řídí sloupcem, na který jste klepli pravým tlačítkem: Název přidá vybrané položky, zatímco Složka přidá jejich nadřazené složky. Skryje se, pokud odpovídající cesty nejsou podporované cíle nebo už v košíku jsou.
- Tažením ze sloupce Název odešlete vybrané soubory nebo adresáře do jiné aplikace.
- Tažením ze sloupce Složka odešlete nadřazené složky vybraných položek.
- Soubory můžete pustit na adresář ve sloupci Název nebo na nadřazenou cestu ve sloupci Složka. Zvolte kopírování, přesun, symbolický odkaz nebo pevný odkaz.
- Puštěním souborů na buňku Název se spustitelným souborem jej spustíte s puštěnými cestami jako argumenty.
- Puštění souborů se řídí výchozím chováním Průzkumníka: jedna složka na stejném svazku znamená přesun, jiný svazek znamená kopírování. Podržte `Ctrl` pro kopii, `Shift` pro přesun nebo `Alt` pro symbolický odkaz. Více cílů znamená kopírování.

## Praktické příklady

- **Uvolnění místa na disku** – vymažte **Filtr**, aby se zobrazily všechny indexované položky. Klepejte na záhlaví **Velikost** (v případě potřeby opakovaně), dokud nebudou největší položky nahoře, procházejte seznam shora dolů a nepotřebné položky mažte klávesou `Shift+Delete` (trvale, bez přesunu do koše).
- **Sledování, kam aplikace zapisuje** – vymažte **Filtr** a klepejte na záhlaví **Změněno**, dokud nebudou nahoře naposledy změněné položky. Poté spusťte nebo použijte aplikaci, kterou chcete sledovat. Soubory, do kterých právě zapisuje, se při změně přesouvají nahoru a sloupec **Složka** ukazuje jejich umístění. Chcete-li sledovat jen část systému souborů, zadejte nejprve cestu do pole **Filtr**.
- **Hledání textu ve zdrojových souborech** – například výrazem `C:\Projects\\ .cs:` omezíte výsledky na soubory `.cs` v projektu. Do pole **Hledat** zadejte požadovaný text a stiskněte `Enter`.

## Místní nabídka

Místní nabídka se přizpůsobuje aktuálnímu výběru:

- **Otevřít v programu** uvádí jen nainstalované aplikace kompatibilní s výběrem. Nastavený porovnávací nástroj je uveden první při přesně dvou souborech nebo adresářích.
- **Otevřít nadřazenou složku** otevře Průzkumníka v nadřazené složce položky.
- **Kopírovat cestu** zkopíruje úplné cesty vybraných položek.
- **7-Zip** se zobrazí, je-li nainstalován `7zFM.exe`.
- **Komprimovat do ZIP** vytvoří archiv ZIP.
- **Vytvořit archiv 7z** se zobrazí jen tehdy, je-li nainstalován `7z.exe` nebo `7zz.exe`.
- **Rozbalit** se zobrazí u podporovaných archivů.
- **Přesunout do koše** provede obnovitelné smazání.

### Mazání a obnovení

Mazání nejprve zkusí každý vybraný soubor či složku jako jednu rychlou operaci. Pokud zamčená, nepřístupná nebo jinak nesmazatelná položka brání operaci nad celou složkou, File Search Manager pokračuje po největších odstranitelných podstromech a poté po jednotlivých souborech. Položky, které odstranit nelze – a nadřazené složky nutné k jejich udržení – zůstanou na místě. Chyby se ohlásí až po zpracování všech dostupných sourozenců.

`Delete` odešle každou úspěšně zpracovanou položku do koše a zachová její atributy i původní umístění. Částečná operace se může projevit jako několik položek v koši, protože neporušené podsložky se pokud možno drží pohromadě a dělí se jen zablokované části. K vrácení každé položky na původní cestu použijte příkaz **Obnovit** v koši, nikoli ruční kopírování jeho položek.

`Shift+Delete` používá stejný postup, ale maže trvale. Kde je to nutné, odstraní atribut `Jen pro čtení`; toto chování je obecné a neomezuje se na repozitáře Git. Soubory zamčené proti smazání a cesty odepřené oprávněními systému souborů zůstanou a jsou zahrnuty do závěrečného hlášení chyb.

Puštění adresářů a `Ctrl+V` zobrazí volbu akce. Konflikty existujících názvů nabízejí přepsání, přeskočení, automatické přejmenování a použití na vše. Přenosy zobrazují průběh položek a lze je zrušit mezi operacemi nejvyšší úrovně.

## Košík cílů

Košík cílů v dolní části uchovává znovupoužitelné cíle pro puštění:

- **+ Název** přidá vybrané položky ze sloupce Název. Složky, podporované archivy a spustitelné soubory mají chování specifické pro daný cíl.
- **+ Složka** přidá nadřazené složky vybraných položek.
- Během tažení lišta zobrazuje dvě velké zóny pro puštění: **＋ Přidat jako cíl** a **📤 Odeslat všem cílům (n)**. Objeví se, jakmile tažení začne v seznamu výsledků nebo když nad lištu najede vnější tažení, a po skončení tažení zmizí.
- **Přidat jako cíl** přidá přesně ty cesty, které tažení vytvořilo. Tažení začaté ve sloupci Složka tedy přidá nadřazené složky; tažení začaté ve sloupci Název přidá pojmenované položky. Puštění na prázdné místo za štítky cílů také přidá cíle.
- **Odeslat všem cílům** použije každý dostupný cíl a v případě potřeby nejprve zobrazí souhrn. Puštění přímo na štítek použije jen tento jeden cíl.
- Cíle typu složka zobrazí volbu kopírování, přesunu, symbolického a pevného odkazu.
- Cíle typu archiv přidají zdroje do archivu. Aktualizace formátů jiných než ZIP vyžaduje nainstalovaný `7z.exe` nebo `7zz.exe`.
- Cíle typu spustitelný soubor se spustí se zdrojovými cestami jako argumenty a vyžadují potvrzení.
- **Odeslat schránku všem (n)…** je táž operace se schránkou jako zdrojem; je nedostupná, dokud nejsou nastaveny žádné cíle.
- Dvojitým klepnutím cíl otevřete. Klepnutím pravým tlačítkem na cíl se na něj zafiltrujete, otevřete jej, odeberete nebo vymažete všechny cíle. Klepnutí pravým tlačítkem na pozadí lišty nabízí také **Vymazat cíle**.

Košík se automaticky ukládá mezi spuštěními aplikace. Chybějící cíle zůstávají v uložené konfiguraci, ale přeskakují se, dokud nebudou opět dostupné.

Všechny příkazy pro cíle a filtry jsou pod klávesou `Alt` a chovají se stejně kdekoli v okně: podržte `Alt`, abyste je viděli v panelu **Nápověda**, a pak stiskněte `N` (přidat vybrané názvy jako cíle), `F` (přidat nadřazené složky), `V` (odeslat schránku všem cílům – držte dál a přidejte `L`/`H`/`O` pro odkaz, pevný odkaz, přepsání), `C` (vymazat cíle), `P` (připnout filtr), `I`/`E` (importovat/exportovat připnuté filtry a cíle). Klávesové sekvence přenášejí přímo se zvolenou akcí; dialog volby akce otevírá jen tlačítko na panelu nástrojů a puštění myší.

Když je aktivní klávesová sekvence, **Nápověda** zobrazuje jen platné následující klávesy. `Esc` se nabízí jen tehdy, když by uvolnění držených kláves provedlo akci a `Alt` právě není držen. Funguje to i v sekvenci s `Alt` po jeho uvolnění, pokud držíte jinou klávesu sekvence. `Backspace` se vždy vrátí o krok zpět. Ovládání toku je od voleb příkazů odděleno čárou.

Položky končící znakem `›` mají další úroveň voleb; držte klávesu sekvence a stiskněte tuto klávesu, aby se podnabídka zobrazila.

Když má fokus seznam výsledků, cíle spravují i sekvence klávesy `T`: `T` přidá vybrané položky jako cíle, `T` `F` přidá jejich nadřazené složky, `T` `V` odešle schránku všem cílům a `T` `C` cíle vymaže. `T` `V` přijímá stejné modifikátory jako `V`: `L` odesílá jako symbolické odkazy, `H` jako pevné odkazy a `O` přepisuje existující soubory.

## Klávesové příkazy

Přepněte fokus na seznam výsledků a stiskněte klávesu zobrazenou v panelu **Nápověda**. Některé příkazy pokračují, dokud jsou klávesy drženy, a dokončí se po uvolnění všech.

Běžné příkazy:

- `Enter`: zafiltrovat do vybraných složek.
- `Delete`: přesunout vybrané položky do koše po potvrzení.
- `Shift+Delete`: trvale odstranit vybrané položky bez potvrzení.
- `Ctrl+C`: standardní kopírování kompatibilní se shellem.
- `Ctrl+X`: standardní vyjmutí kompatibilní se shellem.
- `Ctrl+V`: vložit po volbě kopírování, přesunu, symbolického nebo pevného odkazu.
- `C`: zkopírovat vybrané položky do schránky. Přidáním `V`, `T`, `W` nebo `A` zkopírujete místo toho verzi souboru, čas vytvoření, čas poslední změny nebo čas posledního přístupu; `+` na začátku obsah schránky doplní, místo aby jej nahradil.
- `X`: vyjmout vybrané položky do schránky.
- `D`: porovnat v nastaveném porovnávacím nástroji. Dostupné jen při výběru přesně dvou položek.
- `V`: vložit soubory ze schránky do vybraných složek nebo nadřazených složek.
- `O`: otevřít vybrané položky v jiné aplikaci.
- `A`: otevřít vybrané položky jako správce.
- `F2`: přejmenovat jeden fyzický soubor nebo složku přímo v buňce Název. U více položek použijte některý z transformačních příkazů níže.
- `F3`: zobrazit vybrané položky.
- `F4`: upravit vybrané položky.
- `N`: zkopírovat vybrané názvy.
- `P`: zkopírovat vybrané úplné cesty.
- `F`: zkopírovat vybrané cesty složek.
- `M`: zobrazit vloženou lištu pro vytvoření adresáře ve vybraných složkách.
- `S`: příkazy výběru.
- `T`: příkazy cílů – přidat vybrané jako cíle; poté `F` pro nadřazené složky, `V` pro odeslání schránky všem cílům (s `L`/`H`/`O` pro odkaz, pevný odkaz, přepsání), `C` pro vymazání cílů.
- `U`: rozbalit vybrané archivy. Přidáním `NumPad7` použijete `7z.exe` místo vestavěné obsluhy.
- `Z`: zabalit vybrané položky. Přidáním `NumPad7` použijete `7z.exe`.
- `F1`: otevřít tento soubor nápovědy. Funguje kdekoli v okně.
- `F12`: obnovit z NTFS. Funguje kdekoli v okně a nevyžaduje výběr; každý disk se obnovuje nezávisle, takže pomalý síťový disk nikdy nezdržuje ostatní. `F1` i `F12` zůstávají viditelné v panelu **Nápověda**, když má fokus mřížka výsledků.
- `Right Shift`: vrátit fokus zpět do pole filtru.

Příkazy pod `Ctrl`:

- `Ctrl+A`: vybrat nebo zrušit výběr všeho.
- `Ctrl+D`: zafiltrovat do nadřazených složek vybraných položek.
- `Ctrl+F`: zafiltrovat do vybraných složek.
- `Ctrl+N`: vytvořit nové složky ve vybraných složkách.
- `Ctrl+J`: přejít na další vybranou položku; `Ctrl+Shift+J` na předchozí.

Cíle otevření po `O` nebo `A`. Každá položka se zobrazí, jen když byla aplikace nalezena:

- `B`: Průzkumník souborů.
- `W`: výchozí zjištěný webový prohlížeč.
- `C`: Chrome.
- `F`: Firefox.
- `E`: Edge.
- `O`: Opera.
- `I`: Internet Explorer.
- `A`: Adobe Reader.
- `T`: prohlížeč textu.
- `D`: Visual Studio Code.
- `V`: Visual Studio.
- `Y`: Antigravity.
- `G`: Ghostscript.
- `P`: GhostPCL.
- `X`: GhostXPS.
- `R`: prohlížeč zjištěný pro obsah PRN.
- `S`, poté `P`: PowerShell.
- `S` samotné: Příkazový řádek.

`G`, `P`, `X` a `R` se před spuštěním zeptají na hodnotu DPI.

Příkazy výběru po `S`:

- `A`: vybrat nebo zrušit výběr všeho.
- `D`: vybrat adresáře.
- `F`: vybrat soubory.
- `I`: invertovat výběr.
- `G`: vybrat zelené řádky.
- `R`: vybrat červené řádky.
- `B`: vybrat černé řádky.

Příkazy přejmenování a změn po `F2`:

- `V`: převzít novou cestu nebo název ze schránky.
- `N`: změnit název.
- `E`: změnit příponu; `E` a poté `Delete` příponu odstraní.
- `.`: přidat příponu.
- `Delete`: odstranit text z názvu.
- `F`: přidat předponu.
- `L`: přidat příponu na konec.
- `Insert`: vložit text na pozici.
- `R`: nahradit text.
- `C`: změnit čas vytvoření, poté `V` pro čas ze schránky nebo `C` pro aktuální čas.
- `W`: změnit čas poslední změny se stejnou volbou `V` a `C`.
- `A`: změnit čas posledního přístupu se stejnou volbou `V` a `C`.
- Přidáním `O` na začátku přepíšete existující cíle, je-li to podporováno.

Přejmenování a změny časů u položek chráněných proti zápisu (například cokoli pod `Program Files` nebo `Windows`) se automaticky zopakují přes elevovaného pomocníka. Pokud ještě nebyl spuštěn, právě v tu chvíli se objeví jeho výzva UAC.

## Data a řešení potíží

Uživatelský stav se ukládá do:

```text
%LOCALAPPDATA%\win-search
```

Pokud je načítání NTFS pomalé, nainstalujte službu z instalátoru – ve výchozím stavu je vybraná – nebo potvrďte výzvu k elevaci. Stavový řádek hlásí, zda daný disk použil službu, přímý přístup, elevovaného pomocníka, nebo procházení složek.

Pokud je připojený nebo externí disk nedostupný, File Search Manager jej po krátké kontrole připravenosti přeskočí, aby se start nezasekl na nedosažitelném úložišti.
