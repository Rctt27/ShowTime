# Mise à jour depuis l'app

Date : 2026-11-11. Statut : à valider.

## Intention

Aujourd'hui, mettre ShowTime à jour veut dire aller sur GitHub, télécharger le zip, le décompresser et remplacer l'app à la main, sur Windows comme sur Mac. Le but : quand une version plus récente existe, le menu de l'icône (barre des tâches Windows, barre des menus macOS) propose « Installer la version X.Y.Z » ; un clic télécharge la nouvelle version, ferme ShowTime, la remplace, la relance et supprime l'ancienne.

Cela demande le premier appel réseau de ShowTime. La règle « no network calls » de CLAUDE.md venait d'une démonstration à l'employeur de l'auteur : prouver que rien ne pouvait fuir. Elle est abandonnée au profit de **« local first »** : les captures, les vidéos, les sous-titres, les réglages et le journal restent sur la machine, mais l'app s'autorise le réseau quand il rend l'expérience meilleure. Chaque appel réseau est listé dans une section du README, qui dit ce qui part, vers où et quand. L'expérience utilisateur passe avant la sobriété réseau.

## Objectifs

- Voir qu'une nouvelle version existe, depuis le menu de l'icône, sans rien installer à la main.
- Installer en un clic, sur Windows et sur macOS, l'app relancée sur la nouvelle version, l'ancienne supprimée.
- Garder les réglages, le démarrage à l'ouverture de session et, sur Mac, les autorisations (enregistrement d'écran, micro).
- Documenter chaque appel réseau dans le README, et faire tenir cette liste à jour par CLAUDE.md.

## Non-objectifs

- Pas de mise à jour delta : le zip complet est téléchargé (environ 215 Mo sous Windows, 254 Mo sous macOS, dont ffmpeg et le modèle Whisper).
- Pas de canal bêta ni de versions préliminaires : seule la dernière release publiée compte (`/releases/latest` ignore les brouillons et les pre-releases).
- Pas d'installation sans clic, ni de mise à jour forcée ; pas de retour à une version précédente depuis l'app.
- Pas de notarisation Apple ni de signature Authenticode : hors sujet ici.

## Anti-objectifs (ce qui serait un échec même si ça marche)

- Une capture, un nom de fichier, un réglage ou une ligne du journal envoyé où que ce soit. La requête ne porte que ce qu'une requête HTTP porte forcément (adresse IP, `User-Agent` avec la version de ShowTime).
- Une vidéo en cours perdue, ou un éditeur non enregistré fermé sans question.
- Une installation à moitié faite qui laisse ShowTime inutilisable : en cas d'échec, l'ancienne version doit rester et se lancer.
- Sur Mac, les autorisations d'enregistrement d'écran ou de micro à redonner après la mise à jour.
- Le menu bloqué ou ralenti à l'ouverture parce que le réseau est lent ou absent.
- Un appel réseau qui n'est pas dans la liste du README.

## Contraintes

- Toute règle testable va dans le cœur (`ShowTime.Core`, sans I/O), avec 90 % de couverture ; le réseau, l'archive et le remplacement de fichiers derrière des ports (`Ports/`). Un nouveau comportement commence par un test qui échoue.
- L'app macOS est rognée (`TrimMode` partial) : le JSON de GitHub se lit par métadonnées générées à la compilation, comme `SettingsJsonContext`, pas par réflexion.
- Textes d'interface en français, code et commentaires en anglais.
- Une seule instance tourne à la fois (mutex nommé `Local\ShowTime.SingleInstance` sous Windows, identifiant de bundle sous macOS) : la nouvelle version ne doit démarrer qu'une fois l'ancienne sortie, sinon elle se ferme aussitôt.
- Sous Windows, un exe en cours d'exécution et les DLL chargées (les bibliothèques natives de Whisper dans `app\runtimes\win-x64`) ne peuvent pas être supprimés tant que le processus vit.
- Sur Mac, l'app n'est pas notarisée : la nouvelle version ne doit pas recevoir l'attribut de quarantaine, sinon Gatekeeper la bloque au lancement. Un téléchargement par `HttpClient` ne l'ajoute pas ; décompresser avec `ditto` garde la signature et les liens symboliques du bundle.

## Ce que l'auteur a décidé

- Quand une version plus récente existe, le menu de l'icône la propose ; rien ne s'affiche sinon. L'idée de départ était de vérifier à l'ouverture du menu ; voir « Quand vérifier » dans les choix de Claude.
- Un réglage coupe la vérification automatique ; elle est **activée par défaut**. L'auteur privilégie l'expérience utilisateur à l'absence d'appel réseau.
- Le libellé de l'item : « Installer la version X.Y.Z ».
- Au clic : téléchargement de la nouvelle version, redémarrage sur elle, suppression de l'ancienne. Pas de fenêtre de confirmation supplémentaire.
- La source est GitHub, les releases de `Rctt27/ShowTime`.
- CLAUDE.md passe de « no network calls » à « local first » ; le README reçoit une section listant les appels réseau ; CLAUDE.md demande de la tenir à jour.

## Choix de Claude (à vérifier en lisant)

- **Source** : `GET https://api.github.com/repos/Rctt27/ShowTime/releases/latest`, sans authentification (60 requêtes par heure et par IP, largement assez). Version tirée de `tag_name` (`vX.Y.Z`), comparée à la version de l'assembly (`<Version>` de `Directory.Build.props`) selon SemVer, sans suffixe. Fichier choisi par nom : `ShowTime-X.Y.Z-windows-x64.zip` ou `ShowTime-X.Y.Z-macos.zip` ; une release sans le fichier de la plateforme n'est pas proposée.
- **Quand vérifier** : au démarrage (quelques secondes après, pour ne pas ralentir le lancement), puis toutes les 6 heures tant que ShowTime tourne, et à l'ouverture du menu si la dernière réponse date de plus de 15 minutes. Ainsi l'item est en général déjà là quand on ouvre le menu, au lieu d'apparaître une seconde plus tard. Une vérification déjà en cours n'est pas relancée. Délai réseau : 10 s.
- **Signal discret** : quand une version est disponible, l'info-bulle de l'icône le dit (« ShowTime — version X.Y.Z disponible ») ; pas de notification système, pas de pastille.
- **Réglage** : clé `CheckForUpdates` dans `settings.json`, `true` par défaut (`SettingsDefaults`, les deux plateformes) ; un ancien fichier sans la clé la prend à `true`. Dans les deux fenêtres de réglages, une case cochée par défaut : « Rechercher automatiquement les mises à jour ». Décochée, ShowTime ne vérifie plus rien de lui-même ; le menu garde alors un item « Rechercher une mise à jour » qui vérifie à la demande et affiche « ShowTime est à jour » ou propose « Installer la version X.Y.Z ».
- **Échec de vérification** (hors ligne, limite de GitHub, réponse illisible) : rien dans le menu, une ligne dans le journal, nouvel essai à la prochaine ouverture.
- **Placement** : l'item se met en tête du menu, suivi d'un séparateur ; sur Mac, juste sous la ligne « ShowTime X.Y.Z » déjà présente. Windows n'a pas de ligne de version : on n'en ajoute pas.
- **Pendant l'installation** : l'item devient « Téléchargement de la version X.Y.Z… NN % », grisé ; un deuxième clic n'a pas d'effet. Le menu se ferme normalement ; la progression se lit en le rouvrant. À la fin du téléchargement et de la vérification, l'app se ferme et l'installation continue sans fenêtre.
- **Pendant le téléchargement** ShowTime reste entièrement utilisable : captures, éditeurs, vidéos, transcriptions. Rien n'est fermé au clic.
- **Avant de fermer**, une fois le téléchargement vérifié : ShowTime passe par le même chemin que « Quitter » (`TrayController.Quit`, `StatusMenu.ShouldTerminate` / `PrepareToQuitAsync`), à ce moment-là et pas au clic, pour que les questions portent sur ce qui est ouvert à cet instant. S'il n'y a rien à garder (pas d'éditeur non enregistré, pas de session vidéo, pas de transcription en cours), l'installation se fait sans question. Sinon :
  - une session vidéo qui n'est pas au repos (sélection, prête, compte à rebours, enregistrement, finalisation) : l'installation attend qu'elle se termine, statut « Installation après l'enregistrement… », sans question ;
  - un éditeur non enregistré : sa question habituelle ; Annuler suspend l'installation, et l'item devient « Installer la version X.Y.Z (téléchargée) » : un nouveau clic reprend sans retélécharger ;
  - une transcription en cours : comme « Quitter » le fait aujourd'hui (elle reprendra au prochain lancement, ou la piste sera supprimée, selon ce que fait déjà `Transcriptions` au démarrage). Le builder vérifie ce comportement et l'écrit dans le README.
  Le téléchargement vérifié reste dans `.update-X.Y.Z` jusqu'à l'installation ou à la fermeture de ShowTime.
- **Vérification du fichier** : HTTPS, puis la taille annoncée par GitHub (`size` de l'asset) et son empreinte SHA-256 (`digest` de l'asset, que l'API de GitHub fournit pour les fichiers de release ; à confirmer). Sur Mac en plus : `codesign --verify --deep --strict` sur le bundle décompressé, et un certificat feuille identique à celui de l'app en cours (même `LocalTake`). Une version signée ad hoc (sans les secrets) n'est installée que si l'app en cours l'est aussi.
- **Remplacement sous Windows** : le zip est décompressé dans un dossier temporaire sur le même volume que l'installation (à côté : `<dossier de ShowTime>\.update-X.Y.Z`), pour que les déplacements soient des renommages. Un petit script (PowerShell, lancé caché, détaché du processus) attend la fin de l'ancien processus par son PID, déplace l'ancien `ShowTime.exe`, `LISEZ-MOI.txt` et `app\` dans `.old`, met les nouveaux à leur place, relance `ShowTime.exe`, attend que la nouvelle version confirme son démarrage, puis supprime `.old` et le dossier temporaire. Si une étape échoue, il remet `.old` en place et relance l'ancienne version. Les fichiers que l'utilisateur aurait ajoutés dans le dossier ne sont pas touchés. Le zip contient un dossier `ShowTime\` à sa racine (`Compress-Archive -Path build/ShowTime` dans `release.yml`) : c'est son contenu qui est installé.
- **Démarrage confirmé** : la nouvelle version, une fois son icône affichée et ses services construits, écrit un fichier de confirmation à un chemin que le script lui passe en argument. Sans confirmation au bout de 60 s, ou si elle se ferme avant, le script arrête la nouvelle version, remet l'ancienne, la relance, et l'ancienne affiche « La version X.Y.Z n'a pas pu démarrer ; ShowTime est resté en version A.B.C. » avec le journal du script. Cas visés : un runtime .NET Desktop trop ancien sous Windows (publication `--self-contained false`), une version minimale de macOS relevée.
- **Remplacement sur Mac** : décompression avec `ditto -x -k` dans un dossier temporaire du même volume que le bundle, puis un script `/bin/sh` détaché attend la fin du PID, renomme l'ancien bundle, met le nouveau au même chemin, relance avec `open`, attend la confirmation de démarrage, puis supprime l'ancien. Même retour arrière en cas d'échec ou sans confirmation. Le même chemin et le même certificat gardent l'élément d'ouverture de session (`SMAppService`) et les autorisations.
- **Dossier non modifiable** (Program Files, `/Applications` appartenant à root, volume en lecture seule, app lancée depuis l'image disque) : vérifié avant le téléchargement ; l'item ouvre alors la page de la release dans le navigateur, avec un message qui dit pourquoi.
- **Échec du téléchargement ou de la vérification** : message en français, fichiers temporaires supprimés, ShowTime continue sur l'ancienne version, l'item redevient cliquable.
- **Restes** : au démarrage, ShowTime supprime un `.update-*` ou un `.old` laissé par une installation interrompue, sauf pendant qu'un script de mise à jour tourne (c'est lui qui le lance, avec l'argument de confirmation) : un `.old` n'est un reste que hors installation.
- **Vérification de l'empreinte** : l'API renvoie bien `digest: "sha256:…"` pour les deux zips de v0.6.3 ; le dépôt est public, l'appel sans authentification fonctionne.
- **Journal** : chaque étape (vérification, téléchargement, vérification du fichier, remplacement) est notée dans le journal local ; le script écrit le sien à côté.
- **README** : une section « Réseau » dit ce qui part (une requête vers `api.github.com` au démarrage, toutes les 6 heures et à l'ouverture du menu au plus toutes les 15 minutes ; le téléchargement depuis `github.com` / `objects.githubusercontent.com` au clic), ce qui ne part jamais (captures, vidéos, sous-titres, réglages, journal), et comment couper la vérification.
- **`--diagnostics`** : n'appelle pas le réseau.

## Ce qui va dans le cœur (testé sans I/O)

- La comparaison des versions et la lecture du tag.
- La lecture de la réponse de GitHub (métadonnées générées) et le choix du fichier selon la plateforme.
- Le calendrier des vérifications (démarrage, 6 heures, ouverture du menu après 15 minutes) sur `TimeProvider`, le réglage qui le coupe, une seule vérification à la fois, la décision d'afficher l'item.
- L'état de la mise à jour (rien, disponible, téléchargement NN %, en attente de la fin de l'enregistrement, échec), qui donne le libellé et l'état de l'item pour les deux apps.
- La vérification de taille et d'empreinte.
- Le texte du script de remplacement, généré à partir des chemins et du PID, pour pouvoir le tester comme une chaîne.

Ports envisagés : un client des releases (HTTP), un téléchargeur avec progression, un installeur par plateforme (décompresser, vérifier la signature sur Mac, lancer le script et quitter).

## Laissé au builder

- Les noms exacts des classes, des ports et des fichiers.
- Le choix entre PowerShell et un script `cmd` sous Windows, tant que rien ne s'affiche et que le retour arrière marche.
- La façon de rafraîchir un `ContextMenuStrip` ou un `NSMenu` ouvert.
- Les tests d'intégration du téléchargement et du remplacement (serveur HTTP local, dossiers temporaires) et les lignes à ajouter à `docs/test-checklist.md` pour la vérification à la main sur les deux plateformes.
- La mise à jour de CLAUDE.md (paragraphe « Project », architecture) pour décrire le nouveau module.
