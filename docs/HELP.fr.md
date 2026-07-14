# Aide de File Search Manager

`F1` ouvre cette aide dans l’application ; `F12` actualise l’index NTFS. **Raccourcis** indique les touches disponibles.

## Syntaxe des filtres

Les termes sont séparés par des espaces et combinés avec **ET**. Employez des guillemets pour les termes ou chemins contenant des espaces.

- `report` contient le texte ; `:report` commence par lui ; `report:` finit par lui ; `:report:` est exact.
- `.pdf:|.docx:` correspond aux noms se terminant par l’une de ces extensions ; `report .pdf:` combine les deux conditions.
- `src\` vise le dossier parent immédiat ; `src\\` cherche dans le chemin complet.
- Les ancres de nom s’appliquent à chaque composant du chemin ; `:src:\\` trouve donc les éléments dont un dossier s’appelle exactement `src`, où qu’il soit dans le chemin.
- `"C:\Work"` vise directement ce dossier ; `"C:\Work\\"` vise tous ses descendants.

Exemples : `invoice .pdf:`, `:IMG_ .jpg:|.jpeg:`, `C:\Projects\\ .cs:`. `Ctrl+Left` et `Ctrl+Right` parcourent l’historique ; `Down` affiche les suggestions.

## Exemples pratiques

- **Libérer de l’espace disque** — effacez **Filtre** pour afficher tous les éléments indexés. Cliquez sur **Taille** (une seconde fois si nécessaire) jusqu’à ce que les plus gros éléments soient en haut, parcourez la liste depuis le début et supprimez les éléments inutiles avec `Shift+Delete` (définitivement, sans passer par la Corbeille).
- **Observer où une application écrit** — effacez **Filtre** et cliquez sur **Modifié** jusqu’à ce que les éléments les plus récemment modifiés soient en haut. Lancez ou utilisez ensuite l’application à observer. Les fichiers dans lesquels elle écrit remontent à chaque modification et la colonne **Dossier** indique leur emplacement. Saisissez d’abord un chemin dans **Filtre** pour limiter l’observation à une partie du système de fichiers.
- **Rechercher du texte dans des fichiers source** — utilisez par exemple `C:\Projects\\ .cs:` pour limiter les résultats aux fichiers `.cs` d’un projet. Saisissez le texte recherché dans **Rechercher** et appuyez sur `Enter`.

## Recherche et clavier

Saisissez un texte puis `Enter`. `UTF-8` et `UTF-16` cherchent du texte ; `HEX` cherche des octets séparés par des espaces, par exemple `48 65 6C 6C 6F`. Vert : trouvé ; rouge : absent ; noir : non recherché ; bleu : dossier.

- `Enter` filtre dans les dossiers ; `Delete` / `Shift+Delete` supprime vers la Corbeille / définitivement.
- `Ctrl+C`, `Ctrl+X`, `Ctrl+V` copient, coupent et collent ; `F2`, `F3`, `F4` renommnent, affichent et modifient.
- `O` / `A` ouvre / ouvre en administrateur ; `U` / `Z` extrait / crée une archive.

`›` indique un sous-menu, `Backspace` revient en arrière et `Esc` annule une séquence.
