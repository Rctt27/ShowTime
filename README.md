# ShowTime

Outil de capture d'écran et d'enregistrement vidéo pour **Windows** et **macOS**, sans compte ni cloud : les captures restent sur l'ordinateur. ShowTime n'envoie rien nulle part, pas même des statistiques ; son journal est un fichier local.

## Installation

Les versions se téléchargent depuis la page [Releases](https://github.com/Rctt27/ShowTime/releases).

**Windows 10 (2004) ou 11** : `ShowTime-x.y.z-windows-x64.zip`, à extraire dans un dossier, puis lancer `ShowTime.exe`, seul programme à la racine. Le dossier `app` à côté contient ce dont il a besoin (`ffmpeg.exe`, l'encodeur vidéo ; `runtimes`, la bibliothèque de transcription ; `models`, le modèle de transcription ; les licences) : gardez-le à côté de `ShowTime.exe`. Rien d'autre à installer. Il nécessite le [runtime .NET 10](https://dotnet.microsoft.com/download/dotnet/10.0) (« .NET Desktop Runtime »).

**macOS 14 (Sonoma) ou plus récent**, puce Apple ou Intel : `ShowTime-x.y.z-macos.zip`. Décompressez-le, glissez `ShowTime.app` dans Applications, puis ouvrez-le par **clic droit > Ouvrir** : l'app n'est pas signée par un développeur identifié, macOS le demande une fois (sinon : Réglages Système > Confidentialité et sécurité > « Ouvrir quand même »). ffmpeg et la transcription (whisper.cpp et son modèle) sont inclus dans l'app : rien d'autre à installer. L'icône de ShowTime se trouve dans la barre des menus. macOS demande ensuite deux autorisations, à accorder dans Réglages Système > Confidentialité et sécurité :
- **Enregistrement de l'écran**, à la première capture ; ShowTime doit être relancé après l'avoir accordée ;
- **Micro**, à la première vidéo avec le micro.

À partir de la version 0.5.0, les mises à jour gardent ces autorisations : l'app est toujours signée avec le même certificat (`docs/macos-signing.md`).

## Raccourcis par défaut

| Action | Windows | macOS |
| --- | --- | --- |
| Capturer une zone (un simple clic prend la fenêtre sous le curseur) | `Impr. écran` | `Ctrl+Maj+1` |
| Capturer une fenêtre | `Alt+Impr. écran` | `Ctrl+Maj+2` |
| Capturer tout l'écran | `Ctrl+Impr. écran` | `Ctrl+Maj+3` |
| Vidéo d'une zone | `Ctrl+Maj+Impr. écran` | `Ctrl+Maj+4` |
| Démarrer / arrêter l'enregistrement (actifs seulement pendant une vidéo) | `Ctrl+R` / `Ctrl+D` | `Ctrl+Maj+R` / `Ctrl+Maj+D` |
| Surligner avec la souris (maintenir, seulement pendant l'enregistrement) | `Ctrl+Espace` | `Option+Espace` |

Un Mac n'a pas de touche Impr. écran, et `Cmd+Maj+3/4/5` appartiennent à macOS. Les raccourcis se modifient dans les réglages, depuis l'icône ShowTime. On peut aussi y attribuer un raccourci à la capture de la dernière zone et à la vidéo de tout l'écran. Si le système refuse un raccourci, c'est qu'un autre outil l'utilise déjà (un autre logiciel de capture, l'Outil Capture d'écran…).

Tant que le raccourci de surlignage est maintenu, une traînée jaune translucide suit la souris et apparaît dans la vidéo ; elle s'estompe au relâchement. Il peut aussi être une touche seule (Espace, qui n'écrit alors plus rien pendant l'enregistrement) ou, sous Windows, un bouton latéral de la souris : cliquez avec dans le champ des réglages. Sur Mac, il se maintient au clavier (`Option+Espace`, car `Ctrl+Espace` y change la source de saisie). Les boutons supplémentaires des souris gaming s'associent à une touche `F13` à `F24` dans le logiciel du fabricant, puis à ShowTime.

## Éditeur

Outils : flèche, ligne, rectangle, ellipse, crayon, surligneur, texte, numéros, flou, recadrage, sélection pour déplacer ou supprimer. Chaque outil a sa touche (indiquée dans l'infobulle). `Maj` contraint les formes et les angles. Le recadrage reste modifiable : annuler le défait sans perdre les annotations.

| | Windows | macOS |
| --- | --- | --- |
| Enregistrer dans le dossier des captures | `Ctrl+S` | `Cmd+S` |
| Enregistrer sous | `Ctrl+Maj+S` | `Cmd+Maj+S` |
| Copier l'image | `Ctrl+C` | `Cmd+C` |
| Annuler / rétablir | `Ctrl+Z` / `Ctrl+Y` | `Cmd+Z` / `Cmd+Maj+Z` |

## Vidéo

Enregistrement en MP4 d'une zone ou de tout l'écran. Une fois la zone choisie, une barre permet d'activer ou de couper le micro et le son de l'ordinateur, puis un clic sur la gauche de la barre (▶ « Démarrer ») ou le raccourci de démarrage lance un compte à rebours de 3 secondes. Pendant l'enregistrement, la même zone affiche la durée : un clic dessus (■ « Arrêter ») ou le raccourci d'arrêt enregistre la vidéo. Le cadre rouge et la barre restent invisibles dans la vidéo.

La vidéo s'ouvre ensuite dans un aperçu qui la lit aussitôt, avec l'image à chaque instant au survol de la barre de défilement. `←` / `→` reculent ou avancent de 5 secondes, `Début` revient au début, `Espace` lit ou met en pause. On peut l'afficher dans l'Explorateur ou le Finder, l'envoyer à la Corbeille, ou **Refaire** : la vidéo part à la Corbeille et un nouvel enregistrement commence de la même façon.

### Sous-titres

La bascule **Sous-titres** de la barre d'enregistrement fait transcrire le micro dès la fin de l'enregistrement. Sans elle, le bouton **Générer les sous-titres**, en haut de la fenêtre de lecture, le fait après coup : il apparaît tant que la vidéo n'a pas de sous-titres, et transcrit la piste du micro gardée pendant que la fenêtre est ouverte, ou à défaut le son de la vidéo. La transcription se fait sur l'ordinateur, avec le modèle [Whisper](https://github.com/ggerganov/whisper.cpp) livré avec ShowTime (dossier `app\models` sous Windows, dans l'app sur Mac) : rien n'est envoyé. Elle prend environ un tiers de la durée de la vidéo, et attend la fin d'un enregistrement en cours.

Les sous-titres sont découpés comme pour un tutoriel : une ligne de 42 caractères au plus (environ 5 à 7 mots), coupée à une fin de phrase, une virgule ou avant « et », « qui », « sur »…, jamais après un article, chaque bloc durant de 1 à 5 secondes ; un silence d'une demi-seconde termine un bloc. À côté des temps de chaque bloc, sa vitesse de lecture passe en orange au-delà de 15 caractères par seconde et en rouge au-delà de 17, avec l'explication en infobulle.

Le sous-titre en cours s'affiche aussi sur la vidéo, en bas de l'image, sur un bandeau noir translucide qui le garde lisible quelle que soit l'image.

Ils s'affichent à gauche de la vidéo, bloc par bloc, avec les intervalles sans sous-titre entre eux. Le bloc (ou l'intervalle) de l'instant lu est surligné et reste au milieu de la liste. Un clic sur un bloc y amène la vidéo ; modifier un texte ou un temps met la lecture en pause. `Entrée` valide, `Maj+Entrée` passe à la ligne, `Échap` annule, `Ctrl+Z` / `Ctrl+Y` (`Cmd+Z` / `Cmd+Maj+Z` sur Mac) défont et refont. « + Ajouter » crée un sous-titre dans un intervalle.

Les sous-titres sont enregistrés à chaque modification dans un fichier `.srt` du même nom que la vidéo, que les lecteurs (VLC, Lecteur multimédia…) affichent avec elle. La langue (français, anglais ou détection automatique) et un autre modèle Whisper (fichier `ggml-*.bin`) se choisissent dans les réglages.

ffmpeg, qui encode les vidéos, est livré avec ShowTime (version épinglée ; ShowTime ne télécharge rien). Un autre ffmpeg peut être indiqué dans les réglages. Le son de l'ordinateur :
- Windows : capté directement depuis la sortie audio par défaut, sans pilote à installer ;
- macOS : capté directement par ShowTime (ScreenCaptureKit), sans pilote à installer ; il demande l'autorisation Enregistrement de l'écran, déjà nécessaire pour la vidéo.

## Fichiers

| | Windows | macOS |
| --- | --- | --- |
| Captures | `Images\ShowTime`, ou `%USERPROFILE%\Captures ShowTime` si le dossier Images est synchronisé par OneDrive | `~/Pictures/ShowTime` |
| Sous-titres | à côté de chaque vidéo, même nom en `.srt` | à côté de chaque vidéo, même nom en `.srt` |
| Modèle de transcription | `app\models` à côté de `ShowTime.exe`, sinon `%LOCALAPPDATA%\ShowTime\models` | dans l'app (`Contents/Resources/models`), sinon `~/Library/Application Support/ShowTime/models` |
| Piste du micro en attente de transcription | `%LOCALAPPDATA%\ShowTime\work`, effacée une fois transcrite | `~/Library/Caches/ShowTime`, effacée une fois transcrite |
| Réglages | `%APPDATA%\ShowTime\settings.json` | `~/Library/Application Support/ShowTime/settings.json` |
| Journal (7 jours) | `%LOCALAPPDATA%\ShowTime\logs` | `~/Library/Logs/ShowTime` |

ShowTime s'appelait LocalTake jusqu'à la version 0.4 et ne reprend pas ses réglages. Les dossiers `LocalTake` aux mêmes endroits peuvent être supprimés ; décochez « Lancer LocalTake au démarrage » dans ses réglages avant de le désinstaller.

## Un problème ?

Le diagnostic vérifie, sans ouvrir de fenêtre, ce dont ShowTime a besoin : ffmpeg, les périphériques audio, la capture d'écran, la transcription, les raccourcis, et sur Mac les écrans. Quittez ShowTime avant, sinon ses propres raccourcis apparaissent comme déjà pris. Dans un terminal :
- Windows : `ShowTime.exe --diagnostics` ;
- macOS : `open -a ShowTime --args --diagnostics`, puis `open ~/Library/Logs/ShowTime/diagnostics.txt` (lancé directement depuis `Contents/MacOS`, il testerait les autorisations du Terminal plutôt que celles de ShowTime).

Le rapport est aussi enregistré dans `diagnostics.txt`, dans le dossier du journal : joignez-le, avec le journal, à votre signalement.

## Développement

Nécessite le SDK .NET 10 (`global.json`). L'architecture, les commandes et les règles du dépôt sont décrites dans [CLAUDE.md](CLAUDE.md).

```
dotnet build ShowTime.slnx
dotnet test --solution ShowTime.slnx
dotnet run --project src/ShowTime.Windows
```

L'app macOS se construit sur un Mac avec Xcode et le workload `macos` (voir CLAUDE.md) ; `build/package-macos.sh` y ajoute ffmpeg, whisper.cpp et le modèle :

```
dotnet build src/ShowTime.Mac -c Release
bash build/package-macos.sh src/ShowTime.Mac/bin/Release/net10.0-macos/ShowTime.app dist/ShowTime-macos.zip
```

## Licences

ShowTime est livré avec ffmpeg (GPL v3), whisper.cpp et un modèle Whisper (MIT) : voir `THIRD-PARTY-NOTICES.md`, joint à chaque version.
