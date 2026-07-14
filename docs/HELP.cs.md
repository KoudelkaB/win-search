# Nápověda File Search Manager

File Search Manager má dvě hlavní pole:

- **Filtr** omezuje seznam souborů podle názvu, složky, cesty nebo vybraných adresářů.
- **Hledat** prohledává obsah právě vyfiltrovaných souborů.

Stiskem `F1` tuto nápovědu otevřete odkudkoli. Panel **Nápověda** vpravo vždy ukazuje klávesy dostupné pro aktuální výběr. `F12` znovu načte index NTFS.

## Syntaxe filtrů

Výrazy oddělujte mezerami. Víceslovné názvy a cesty uzavřete do uvozovek. Více výrazů se kombinuje jako **AND** – položka musí vyhovět všem.

### Názvy

- `report` – název obsahuje `report`.
- `:report` – název začíná na `report`.
- `report:` – název končí na `report`.
- `:report:` – název je přesně `report`.
- `.pdf:|.docx:` – název končí na jednu z těchto přípon.
- `report .pdf:` – název obsahuje `report` a končí na `.pdf`.

Filtr nerozlišuje velikost písmen. Dvojtečka tedy určuje, ke kterému okraji názvu se výraz přichytí; není součástí hledaného textu.

### Složky a celé cesty

- `src\` – položky, jejichž bezprostřední nadřazená složka odpovídá `src`.
- `src\\` – položky s `src` kdekoli v celé cestě.
- `"C:\Work"` – položky přímo uvnitř `C:\Work`.
- `"C:\Work\\"` – všechny položky rekurzivně pod `C:\Work`.

Koncové jednoduché `\` tedy hledá v rodičovské složce, zatímco dvojité `\\` v celé cestě. U cesty s mezerami jsou uvozovky povinné.

## Praktické příklady

- **Uvolnění místa na disku** – vymažte pole **Filtr**, aby se zobrazily všechny indexované položky. Klikněte na záhlaví **Velikost** (v případě potřeby znovu), dokud nebudou největší položky nahoře, seznam procházejte shora a nepotřebné položky mažte pomocí `Shift+Delete` (trvale, bez přesunutí do koše).
- **Živé sledování zápisů aplikace** – vymažte pole **Filtr** a klikněte na záhlaví **Změněno**, dokud nebudou naposledy změněné položky nahoře. Potom spusťte nebo používejte sledovanou aplikaci. Soubory, do kterých právě zapisuje, se průběžně přesouvají nahoru a sloupec **Složka** ukazuje jejich umístění. Sledování můžete omezit zadáním cesty do pole **Filtr**.
- **Hledání textu ve zdrojových souborech** – například filtrem `C:\Projects\\ .cs:` nejprve omezte výsledky na soubory `.cs` v projektu. Do pole **Hledat** napište hledaný text a stiskněte `Enter`.

## Historie a připnuté filtry

- `Ctrl+Left` / `Ctrl+Right` – předchozí / následující filtr v historii.
- `Down` – zobrazit návrhy.
- `Del` – odstranit vybraný návrh.
- Přidržení `Ctrl` při otevření návrhů přepne z nejpoužívanějších na naposledy použité.
- **Připnout…** uloží aktuální výraz pod zvoleným názvem.
- Pravým tlačítkem na připnutém filtru jej lze aktualizovat, přejmenovat nebo odepnout.
- **Exportovat…** a **Importovat…** ukládají a obnovují připnuté filtry společně s cíli.

`Esc` přejde ve filtru o jednu úroveň výš. `Enter` nad vybranou složkou vytvoří odpovídající cestový filtr.

## Hledání v obsahu

Do pole **Hledat** napište text a stiskněte `Enter`. `UTF-8` a `UTF-16` hledají text; `HEX` hledá bajty zapsané po dvojicích oddělených mezerami, například `48 65 6C 6C 6F`. Volba **Nerozlišovat velikost písmen** platí pro textové hledání.

Zelená znamená nalezený obsah, červená nenalezený obsah, černá neprohledanou položku a modrá složku.

## Nejdůležitější klávesy

- `Enter` – filtrovat do vybraných složek.
- `Delete` / `Shift+Delete` – odstranit do koše / trvale.
- `Ctrl+C`, `Ctrl+X`, `Ctrl+V` – standardní práce se schránkou.
- `F2`, `F3`, `F4` – přejmenovat, zobrazit, upravit.
- `O` / `A` – otevřít / otevřít jako správce.
- `N`, `P`, `F` – kopírovat název, úplnou cestu, cestu složky.
- `U` / `Z` – rozbalit / zabalit archiv.
- `Right Shift` – vrátit fokus do filtru.

Podržte klávesu sekvence a sledujte panel **Nápověda**. Šipka `›` označuje další úroveň, `Backspace` se vrací o krok a `Esc` rozpracovanou sekvenci ruší.
