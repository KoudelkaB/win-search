# Aide de File Search Manager

File Search Manager comporte deux champs principaux :

- **Filtre** restreint la liste des fichiers par nom, dossier, chemin ou répertoires sélectionnés.
- **Rechercher** recherche dans le contenu des fichiers actuellement filtrés.

La liste de résultats est conçue pour le clavier. Lorsqu’elle a le focus, le panneau **Raccourcis** affiche les commandes disponibles pour la sélection.

## Indexation et élévation

Au démarrage, File Search Manager charge les lecteurs prêts et surveille les modifications du système de fichiers. Chaque lecteur se charge indépendamment, de sorte qu’un lecteur réseau lent ne retarde jamais les autres.

Par défaut, seuls les lecteurs NTFS sont indexés : les autres systèmes de fichiers (montages réseau, clés FAT) ne peuvent pas utiliser la lecture rapide de la MFT, et les parcourir fichier par fichier peut dominer tout le chargement. Le bouton **💽 Lecteurs…** de la barre d’état permet de choisir précisément les lecteurs indexés ; les lecteurs décochés ne sont ni analysés ni surveillés.

Pour les lecteurs NTFS, la voie la plus rapide est la lecture de la Master File Table NTFS. File Search Manager peut le faire de l’une de ces manières :

- Service Windows : aucune demande. Sélectionné par défaut dans le programme d’installation.
- Assistant élevé : une demande UAC par exécution de l’application. Sans le service, elle est proposée au démarrage afin que l’indexation puisse commencer immédiatement. Avec le service, elle n’est proposée qu’à la première action nécessitant réellement des droits administrateur : la touche `A` ou la lecture de fichiers réservés aux administrateurs. Le bouton **🛡** situé à côté de **🌐** la demande au lancement à la place.
- Application élevée directement : exécutez File Search Manager en tant qu’administrateur.

Si rien de tout cela n’est disponible, File Search Manager se rabat sur le parcours des dossiers. L’application fonctionne toujours, mais le chargement initial peut être plus lent.

Le service est en lecture seule pour l’indexation. Il expose les données MFT à l’application de bureau et se nomme `WinSearchService`.

## Syntaxe des filtres

Les termes du filtre sont séparés par des espaces. Employez des guillemets pour les termes ou chemins contenant des espaces.

Correspondance des noms :

- `report` correspond aux noms contenant `report`.
- `:report` correspond aux noms commençant par `report`.
- `report:` correspond aux noms se terminant par `report`.
- `:report:` correspond exactement au nom `report`.
- `.pdf:|.docx:` correspond aux noms se terminant par `.pdf` ou `.docx`.
- Plusieurs termes sont combinés avec ET, donc `report .pdf:` trouve les noms contenant `report` et se terminant par `.pdf`.

Correspondance des dossiers :

- `src\` correspond aux éléments dont le dossier parent immédiat s’appelle `src`.
- `src\\` correspond aux éléments ayant `src` n’importe où dans le chemin complet.
- Les ancres de nom s’appliquent à chaque composant du chemin ; `:src:\\` trouve donc les éléments dont un dossier s’appelle exactement `src`, où qu’il soit dans le chemin.
- `"C:\Work"` correspond aux éléments directement dans `C:\Work`.
- `"C:\Work\\"` correspond aux éléments situés récursivement sous `C:\Work`.

Historique :

- `Ctrl+Left` et `Ctrl+Right` parcourent l’historique des filtres.
- `Down` ouvre les suggestions.
- `Del` supprime la suggestion sélectionnée.
- Maintenez `Ctrl` en ouvrant les suggestions pour utiliser l’historique le plus récent plutôt que le plus utilisé.

### Filtres épinglés

- Cliquez sur **📌 Épingler…** pour ajouter un onglet modifiable à la rangée des filtres épinglés. Saisissez son nom sur place et appuyez sur `Enter` ; `Esc` annule.
- Cliquez sur un filtre nommé pour restaurer son expression enregistrée. Le filtre nommé actif est mis en évidence.
- Un clic droit sur un filtre nommé permet de le mettre à jour depuis l’expression courante, de le renommer sur place ou de le désépingler.
- Les filtres épinglés sont restaurés au démarrage de l’application.

**Exporter…** enregistre tous les filtres épinglés et les entrées du panier de cibles dans un seul fichier de paramètres JSON. **Importer…** valide un fichier de paramètres puis, après confirmation, remplace les filtres épinglés et les cibles actuels par son contenu.

## Recherche dans le contenu

Saisissez un terme dans **Rechercher** et appuyez sur `Enter`.

Options d’encodage :

- `UTF-8` recherche du texte encodé en UTF-8.
- `UTF-16` recherche du texte encodé en UTF-16 little-endian.
- `HEX` recherche des octets écrits en hexadécimal séparé par des espaces, par exemple `48 65 6C 6C 6F`.

La case **Insensible à la casse** s’applique aux recherches de texte.

Couleurs des résultats après une recherche dans le contenu :

- Vert : le contenu a été trouvé.
- Rouge : le contenu n’a pas été trouvé.
- Noir : l’élément n’a pas été recherché ou la recherche a été effacée.
- Bleu : dossier.

Saisir autre chose que `Enter` dans le champ de recherche efface l’état actuel des résultats de la recherche dans le contenu.

## Actions à la souris

- Cliquez, Ctrl+cliquez ou Maj+cliquez sur les résultats pour sélectionner des éléments individuels ou des plages.
- Double-cliquez sur un nom de fichier ou de dossier pour filtrer dans cet élément.
- Double-cliquez sur la colonne du dossier pour ouvrir l’Explorateur avec l’élément sélectionné.
- Ctrl + clic droit sur un résultat l’ouvre avec l’action Windows par défaut.
- Cliquez sur un en-tête de colonne pour trier selon cette colonne.
- Un clic droit sur les éléments sélectionnés propose Ouvrir, Ouvrir avec dynamique, presse-papiers, renommage, archivage et suppression.
- **Ajouter au panier de cibles** suit la colonne où le clic droit a eu lieu : Nom ajoute les éléments sélectionnés, tandis que Dossier ajoute leurs dossiers parents. L’option est masquée lorsque les chemins correspondants ne sont pas des cibles prises en charge ou figurent déjà dans le panier.
- Faites glisser depuis la colonne Nom pour envoyer les fichiers ou répertoires sélectionnés vers une autre application.
- Faites glisser depuis la colonne Dossier pour envoyer les dossiers parents des éléments sélectionnés.
- Déposez des fichiers sur un répertoire dans la colonne Nom ou sur un chemin parent dans la colonne Dossier. Choisissez copier, déplacer, lien symbolique ou lien physique.
- Déposez des fichiers sur une cellule Nom exécutable pour la lancer avec les chemins déposés comme arguments.
- Les dépôts suivent les valeurs par défaut de l’Explorateur : un dossier sur le même volume donne un déplacement, un autre volume donne une copie. Maintenez `Ctrl` pour copier, `Shift` pour déplacer ou `Alt` pour un lien symbolique. Plusieurs destinations donnent une copie.

## Exemples pratiques

- **Libérer de l’espace disque** — effacez **Filtre** pour afficher tous les éléments indexés. Cliquez sur l’en-tête **Taille** (une seconde fois si nécessaire) jusqu’à ce que les plus gros éléments soient en haut, parcourez la liste depuis le début et supprimez les éléments inutiles avec `Shift+Delete` (définitivement, sans passer par la Corbeille).
- **Observer où une application écrit** — effacez **Filtre** et cliquez sur l’en-tête **Modifié** jusqu’à ce que les éléments les plus récemment modifiés soient en haut. Lancez ou utilisez ensuite l’application à observer. Les fichiers dans lesquels elle écrit remontent à chaque modification et la colonne **Dossier** indique leur emplacement. Saisissez d’abord un chemin dans **Filtre** pour limiter l’observation à une partie du système de fichiers.
- **Rechercher du texte dans des fichiers source** — utilisez par exemple `C:\Projects\\ .cs:` pour limiter les résultats aux fichiers `.cs` d’un projet. Saisissez le texte recherché dans **Rechercher** et appuyez sur `Enter`.

## Menu contextuel

Le menu contextuel s’adapte à la sélection courante :

- **Ouvrir avec** ne liste que les applications installées compatibles avec la sélection. Un outil de comparaison configuré apparaît en premier pour exactement deux fichiers ou répertoires.
- **Ouvrir le dossier contenant** ouvre l’Explorateur sur le dossier parent de l’élément.
- **Copier le chemin** copie les chemins complets des éléments sélectionnés.
- **7-Zip** apparaît lorsque `7zFM.exe` est installé.
- **Zip** crée une archive ZIP.
- **Créer une archive 7z** n’apparaît que si `7z.exe` ou `7zz.exe` est installé.
- **Décompresser** apparaît pour les archives prises en charge.
- **Déplacer vers la Corbeille** effectue une suppression récupérable.

### Suppression et restauration

La suppression tente d’abord chaque fichier ou dossier sélectionné en une seule opération rapide. Si un élément verrouillé, inaccessible ou autrement indestructible empêche l’opération sur un dossier entier, File Search Manager poursuit avec les plus grandes sous-arborescences supprimables, puis avec les fichiers individuels. Les éléments qui restent impossibles à supprimer — et les dossiers parents nécessaires pour les contenir — demeurent en place. Les erreurs sont signalées après que tous les éléments frères accessibles ont été tentés.

`Delete` envoie chaque élément traité avec succès à la Corbeille en conservant ses attributs et son emplacement d’origine. Une opération partielle peut apparaître sous forme de plusieurs entrées de la Corbeille, car les sous-dossiers intacts sont gardés ensemble autant que possible et seules les parties bloquées sont scindées. Utilisez la commande **Restaurer** de la Corbeille, plutôt qu’une copie manuelle, pour ramener chaque élément à son chemin d’origine.

`Shift+Delete` suit le même parcours au mieux, mais supprime définitivement. Il retire l’attribut `Lecture seule` là où c’est nécessaire ; ce comportement est général et ne se limite pas aux dépôts Git. Les fichiers verrouillés contre la suppression et les chemins refusés par les autorisations du système de fichiers subsistent et figurent dans le rapport d’erreurs final.

Les dépôts de répertoires et `Ctrl+V` affichent un sélecteur d’action. Les conflits de noms existants proposent le remplacement, le saut, le renommage automatique et l’application à tous. Les transferts affichent la progression par élément et peuvent être annulés entre les opérations de premier niveau.

## Panier de cibles

Le panier de cibles en bas conserve des cibles de dépôt réutilisables :

- **+ Nom** ajoute les éléments sélectionnés de la colonne Nom. Les dossiers, archives prises en charge et exécutables ont un comportement propre à leur type de cible.
- **+ Dossier** ajoute les dossiers parents des éléments sélectionnés.
- Pendant un glissement, la barre affiche deux grandes zones de dépôt : **＋ Ajouter comme cible** et **📤 Envoyer à toutes les cibles (n)**. Elles apparaissent dès qu’un glissement commence dans la liste de résultats, ou lorsqu’un glissement externe survole la barre, et disparaissent à la fin.
- **Ajouter comme cible** ajoute exactement les chemins produits par le glissement. Un glissement commencé dans la colonne Dossier ajoute donc les dossiers parents ; commencé dans la colonne Nom, il ajoute les éléments nommés. Déposer dans l’espace vide après les puces de cibles ajoute également des cibles.
- **Envoyer à toutes les cibles** utilise chaque cible disponible après avoir affiché un résumé si nécessaire. Déposer directement sur une puce n’utilise que cette cible.
- Les cibles de type dossier affichent le sélecteur copier, déplacer, lien symbolique et lien physique.
- Les cibles de type archive ajoutent les sources à l’archive. La mise à jour de formats autres que ZIP nécessite un `7z.exe` ou `7zz.exe` installé.
- Les cibles exécutables sont lancées avec les chemins sources comme arguments et demandent une confirmation.
- **Envoyer le presse-papiers à toutes les cibles (n)…** est la même opération avec le presse-papiers comme source ; elle est désactivée tant qu’aucune cible n’est définie.
- Double-cliquez sur une cible pour l’ouvrir. Un clic droit sur une cible permet de filtrer dessus, de l’ouvrir, de la supprimer ou d’effacer toutes les cibles. Un clic droit sur le fond de la barre propose également **Effacer les cibles**.

Le panier est enregistré automatiquement entre les exécutions. Les cibles manquantes restent dans la configuration enregistrée mais sont ignorées jusqu’à ce qu’elles redeviennent disponibles.

Toutes les commandes de cibles et de filtres se trouvent sous `Alt` et se comportent de la même façon partout dans la fenêtre : maintenez `Alt` pour les voir dans le panneau **Raccourcis**, puis appuyez sur `N` (ajouter les noms sélectionnés comme cibles), `F` (ajouter les dossiers parents), `V` (envoyer le presse-papiers à toutes les cibles — continuez à maintenir et ajoutez `L`/`H`/`O` pour lien, lien physique, remplacement), `C` (effacer les cibles), `P` (épingler le filtre), `I`/`E` (importer/exporter les filtres épinglés et les cibles). Les séquences clavier transfèrent directement avec l’action choisie ; seuls le bouton de la barre d’outils et les dépôts à la souris ouvrent la boîte de dialogue de choix d’action.

Tant qu’une séquence clavier est active, **Raccourcis** n’affiche que les touches suivantes valides. `Esc` n’est proposé que si relâcher les touches maintenues exécuterait une action et que `Alt` n’est pas maintenu. Cela vaut aussi dans une séquence `Alt` après avoir relâché `Alt` tout en maintenant une autre touche de séquence. `Backspace` revient toujours d’un pas. Les commandes de flux sont séparées des choix de commandes par une ligne.

Les entrées se terminant par `›` possèdent un niveau supplémentaire de choix ; maintenez une touche de séquence et appuyez sur cette touche pour afficher le sous-menu.

Lorsque la liste de résultats a le focus, les séquences de la touche `T` gèrent aussi les cibles : `T` ajoute les éléments sélectionnés comme cibles, `T` `F` ajoute leurs dossiers parents, `T` `V` envoie le presse-papiers à toutes les cibles et `T` `C` efface les cibles. `T` `V` accepte les mêmes modificateurs que `V` : `L` envoie en liens symboliques, `H` en liens physiques et `O` remplace les fichiers existants.

## Commandes clavier

Donnez le focus à la liste de résultats et appuyez sur une touche affichée dans le panneau **Raccourcis**. Certaines commandes se poursuivent tant que les touches sont maintenues et se terminent lorsque toutes sont relâchées.

Commandes courantes :

- `Enter` : filtrer dans les dossiers sélectionnés.
- `Delete` : déplacer les éléments sélectionnés vers la Corbeille après confirmation.
- `Shift+Delete` : supprimer définitivement les éléments sélectionnés sans confirmation.
- `Ctrl+C` : copie standard compatible avec le shell.
- `Ctrl+X` : couper standard compatible avec le shell.
- `Ctrl+V` : coller après avoir choisi copier, déplacer, lien symbolique ou lien physique.
- `C` : copier les éléments sélectionnés dans le presse-papiers. Ajoutez `V`, `T`, `W` ou `A` pour copier à la place la version du fichier, la date de création, la date de dernière écriture ou la date de dernier accès ; un `+` initial ajoute au presse-papiers au lieu de le remplacer.
- `X` : couper les éléments sélectionnés dans le presse-papiers.
- `D` : comparer dans l’outil de comparaison configuré. Disponible uniquement avec exactement deux éléments sélectionnés.
- `V` : coller les fichiers du presse-papiers dans les dossiers sélectionnés ou parents.
- `O` : ouvrir les éléments sélectionnés dans une autre application.
- `A` : ouvrir les éléments sélectionnés en tant qu’administrateur.
- `F2` : renommer un fichier ou dossier physique directement dans sa cellule Nom. Pour plusieurs éléments, utilisez l’une des commandes de transformation ci-dessous.
- `F3` : afficher les éléments sélectionnés.
- `F4` : modifier les éléments sélectionnés.
- `N` : copier les noms sélectionnés.
- `P` : copier les chemins complets sélectionnés.
- `F` : copier les chemins de dossiers sélectionnés.
- `M` : afficher une barre intégrée pour créer un répertoire dans les dossiers sélectionnés.
- `S` : commandes de sélection.
- `T` : commandes de cibles — ajouter la sélection comme cibles ; puis `F` pour les dossiers parents, `V` pour envoyer le presse-papiers à toutes les cibles (avec `L`/`H`/`O` pour lien, lien physique, remplacement), `C` pour effacer les cibles.
- `U` : extraire les archives sélectionnées. Ajoutez `NumPad7` pour appeler `7z.exe` au lieu du traitement intégré.
- `Z` : compresser les éléments sélectionnés. Ajoutez `NumPad7` pour appeler `7z.exe`.
- `F1` : ouvrir ce fichier d’aide. Fonctionne partout dans la fenêtre.
- `F12` : actualiser depuis NTFS. Fonctionne partout dans la fenêtre, sans sélection ; chaque lecteur s’actualise indépendamment, de sorte qu’un lecteur réseau lent ne retarde jamais les autres. `F1` et `F12` restent visibles dans **Raccourcis** lorsque la grille de résultats a le focus.
- `Right Shift` : ramener le focus dans le champ de filtre.

Commandes sous `Ctrl` :

- `Ctrl+A` : tout sélectionner ou désélectionner.
- `Ctrl+D` : filtrer dans les dossiers parents des éléments sélectionnés.
- `Ctrl+F` : filtrer dans les dossiers sélectionnés.
- `Ctrl+N` : créer de nouveaux dossiers dans les dossiers sélectionnés.
- `Ctrl+J` : aller à l’élément sélectionné suivant ; `Ctrl+Shift+J` au précédent.

Cibles d’ouverture après `O` ou `A`. Chaque entrée n’apparaît que si l’application a été détectée :

- `B` : Explorateur de fichiers.
- `W` : navigateur web par défaut détecté.
- `C` : Chrome.
- `F` : Firefox.
- `E` : Edge.
- `O` : Opera.
- `I` : Internet Explorer.
- `A` : Adobe Reader.
- `T` : visionneuse de texte.
- `D` : Visual Studio Code.
- `V` : Visual Studio.
- `Y` : Antigravity.
- `G` : Ghostscript.
- `P` : GhostPCL.
- `X` : GhostXPS.
- `R` : visionneuse détectée pour le contenu PRN.
- `S`, puis `P` : PowerShell.
- `S` seul : invite de commandes.

`G`, `P`, `X` et `R` demandent une valeur de PPP avant de s’exécuter.

Commandes de sélection après `S` :

- `A` : tout sélectionner ou désélectionner.
- `D` : sélectionner les répertoires.
- `F` : sélectionner les fichiers.
- `I` : inverser la sélection.
- `G` : sélectionner les lignes vertes.
- `R` : sélectionner les lignes rouges.
- `B` : sélectionner les lignes noires.

Commandes de renommage et de modification après `F2` :

- `V` : reprendre le nouveau chemin ou nom depuis le presse-papiers.
- `N` : changer le nom.
- `E` : changer l’extension ; `E` puis `Delete` supprime l’extension.
- `.` : ajouter une extension.
- `Delete` : supprimer du texte dans le nom.
- `F` : ajouter un préfixe.
- `L` : ajouter un suffixe.
- `Insert` : insérer du texte à une position.
- `R` : remplacer du texte.
- `C` : changer la date de création, puis `V` pour la date du presse-papiers ou `C` pour la date actuelle.
- `W` : changer la date de dernière écriture, avec les mêmes choix `V` et `C`.
- Ajoutez `O` en premier pour remplacer les cibles existantes lorsque cela est pris en charge.

## Données et dépannage

L’état utilisateur est stocké sous :

```text
%LOCALAPPDATA%\win-search
```

Si le chargement NTFS est lent, installez le service depuis le programme d’installation — il est sélectionné par défaut — ou acceptez la demande d’élévation. La barre d’état indique si chaque lecteur a utilisé le service, l’accès direct, l’assistant administrateur ou le parcours des dossiers.

Si un lecteur mappé ou externe est indisponible, File Search Manager l’ignore après une brève vérification de disponibilité afin que le démarrage ne se bloque pas sur un stockage inaccessible.
