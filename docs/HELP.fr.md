# Aide de File Search Manager

`F1` ouvre cette aide dans l’application ; `F12` actualise l’index NTFS. **Raccourcis** indique les touches disponibles.

## Syntaxe des filtres

Les termes sont séparés par des espaces et combinés avec **ET**. Employez des guillemets pour les termes ou chemins contenant des espaces.

- `report` contient le texte ; `:report` commence par lui ; `report:` finit par lui ; `:report:` est exact.
- `pdf|docx` accepte une alternative ; `report pdf:` combine les deux conditions.
- `src\` vise le dossier parent immédiat ; `src\\` cherche dans le chemin complet.
- `"C:\Work"` vise directement ce dossier ; `"C:\Work\\"` vise tous ses descendants.

Exemples : `invoice pdf:`, `:IMG_ jpg|jpeg`, `"C:\Projects\\" cs:`. `Ctrl+Left` et `Ctrl+Right` parcourent l’historique ; `Down` affiche les suggestions.

## Recherche et clavier

Saisissez un texte puis `Enter`. `UTF-8` et `UTF-16` cherchent du texte ; `HEX` cherche des octets séparés par des espaces, par exemple `48 65 6C 6C 6F`. Vert : trouvé ; rouge : absent ; noir : non recherché ; bleu : dossier.

- `Enter` filtre dans les dossiers ; `Delete` / `Shift+Delete` supprime vers la Corbeille / définitivement.
- `Ctrl+C`, `Ctrl+X`, `Ctrl+V` copient, coupent et collent ; `F2`, `F3`, `F4` renommnent, affichent et modifient.
- `O` / `A` ouvre / ouvre en administrateur ; `U` / `Z` extrait / crée une archive.

`›` indique un sous-menu, `Backspace` revient en arrière et `Esc` annule une séquence.
