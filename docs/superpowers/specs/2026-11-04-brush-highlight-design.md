# Traînée « coup de pinceau » et réglage du trait

## Intention

Le surlignage pendant l'enregistrement (spec du 2026-10-24) dessine une traînée jaune d'épaisseur fixe, faite de
segments droits relevés à 60 Hz. Sur Windows, elle est sans anticrénelage (couleur clé magenta). L'effet est utile,
mais raide. Le but est un trait qui ressemble à un coup de pinceau ou de feutre, comme Markup sur iPhone :

- il **s'affine quand la souris accélère** et ses variations de largeur sont douces ;
- ses **changements de direction sont progressifs**, comme la main qui tient un pinceau. Le geste le plus fréquent est
  d'entourer une zone, en entier ou en partie, et l'arrondi doit y être fluide, sans à-coups ;
- ses bouts s'effilent ;
- il est **assez couvrant pour entourer et assez translucide pour passer sur un texte** : environ 45 % d'opacité au
  lieu de 25 %.

**Chaque utilisateur peut régler ce trait** depuis les réglages, dans une fenêtre où il trace et voit le résultat. Une
valeur par défaut est imposée. La page HTML qui a servi à mettre au point le trait reste dans le dépôt, comme labo
pour de futures options graphiques.

## Objectifs, non-objectifs, anti-objectifs

- **Objectifs** :
  - le trait ci-dessus, identique sur Windows et sur macOS, dans la vidéo comme à l'écran ;
  - un réglage par utilisateur, enregistré dans settings.json ;
  - un aperçu fidèle : on y règle exactement ce que l'enregistrement dessinera.
- **Non-objectifs** :
  - changer la couleur, le déclencheur ou la durée du fondu ;
  - proposer plusieurs styles ou formes de pinceau (le labo sert à les explorer plus tard) ;
  - lire la souris plus souvent qu'à chaque image.
- **Anti-objectifs**, à ne pas atteindre même si le reste fonctionne :
  - un bout de trait en retard sur le curseur ;
  - des bords crénelés ou une frange rose ;
  - des croisements plus foncés ;
  - une saccade visible dans la vidéo ;
  - un aperçu qui diffère du vrai trait ;
  - un ancien settings.json qui ne se charge plus.

## Décisions

**De l'utilisateur**
- La largeur dépend de la vitesse : plus la souris va vite, plus le trait est fin.
- L'opacité est un compromis entre surligneur et feutre.
- La fluidité des courbes est prioritaire, pour les gestes d'entourage.
- Le trait est réglable par chaque utilisateur, dans une **fenêtre native** ouverte depuis les réglages, et non dans la
  page HTML.
- Les valeurs par défaut sont celles du prototype : largeur 18 px, 35 % à grande vitesse, vitesse « rapide »
  2 500 px/s, douceur 60 ms, fluidité 25 ms, effilements 24 et 48 px, pointes à 25 %, opacité 45 %.

**Les miennes** (marquées comme telles, à corriger au besoin)
- La géométrie du trait est entièrement dans le cœur, partagée par les deux apps et testée. Les apps ne font que
  remplir des disques et des quadrilatères.
- C'est un voile uniforme : le trait est peint opaque, puis rendu translucide d'un bloc. C'est l'opacité de la fenêtre,
  comme aujourd'hui.
- L'aperçu fait tourner **le vrai `Highlighter` et le vrai overlay**, placé au-dessus de la zone de tracé : seule
  l'entrée change (le bouton gauche dans la zone au lieu du déclencheur global).
- La zone d'aperçu a une moitié claire et une moitié sombre, avec du texte et un bouton, pour juger les deux fonds
  d'un coup.
- Le bouton d'aperçu est désactivé pendant un enregistrement, pour que le trait d'aperçu ne puisse pas finir dans la
  vidéo.
- Le labo est `tools/highlight-lab.html`. Il n'est pas livré avec l'app, et son algorithme peut prendre de l'avance sur
  celui du cœur, qui fait référence.

## Le trait (cœur, `Recording/BrushStroke.cs`)

Entrée : les échantillons `(PixelPoint, TimeSpan)` relevés depuis l'appui, le style, l'échelle de l'écran (DPI) et
`lifted` (relâché). Sortie : des disques `StrokeDisc(X, Y, Radius)` en pixels physiques. Le trait est l'union des
disques et du quadrilatère tangent (l'enveloppe) entre chaque paire de disques consécutifs.

1. **Fluidité** : chaque échantillon devient la moyenne de ses voisins, pondérée par une gaussienne en temps
   (σ = `Fluidity`). La fenêtre est symétrique, de ±3σ au plus, et se resserre près des deux bouts. Ainsi le trait
   commence au point d'appui et finit sous la souris, sans retard, et la partie la plus récente se lisse à mesure que
   les relevés suivants arrivent. Une pause, où rien n'est relevé, ne mélange rien : s'arrêter puis repartir garde un
   angle net. Sur un cercle tracé en 0,6 s avec un léger tremblement, 25 ms rend les virages environ trois fois plus
   réguliers et resserre le cercle de 3 % (7 % à 40 ms).
2. **Vitesse** : distance sur durée entre deux échantillons lissés, divisée par l'échelle. On travaille donc en pixels
   « à 100 % » par seconde, et le même geste donne le même trait à 100 % et à 200 %. Les durées sont les vrais
   horodatages : un minuteur en retard ne fausse pas la vitesse.
3. **Largeur visée** : `Thickness × (1 − (1 − MinRatio) × easeOut(min(v / FastSpeed, 1)))`, avec
   `easeOut(x) = x(2 − x)`.
4. **Douceur** : `w += (visée − w) × (1 − exp(−dt / WidthSmoothing))`.
5. **Courbe** : une Catmull-Rom centripète passe par les points lissés, avec un pas d'environ 3 px × échelle. Les
   largeurs sont interpolées entre les points.
6. **Effilements**, selon la longueur parcourue `s` sur la longueur totale `L` :
   - au début, toujours : `taper(s / StartTaper)` ;
   - à la fin, seulement si `lifted` : `taper((L − s) / min(EndTaper, L / 2))` ;
   - avec `taper(x) = TaperFloor + (1 − TaperFloor) × easeOut(min(x, 1))`.

   Un appui sans bouger donne un point de `TaperFloor` × la largeur.

À côté :
- `Hull(a, b)` donne les quatre points tangents, ou null quand un disque contient l'autre.
- `Changed(previous, current)` donne le rectangle en pixels à repeindre, depuis le dernier disque commun et élargi de
  la marge d'anticrénelage, ou null si rien n'a changé. Grâce à lui, chaque image ne repeint que le bout qui bouge,
  sur les deux plateformes. Seul ce qui dépend des derniers échantillons change : la fenêtre de fluidité et la
  dernière portion de courbe.

## Le style (`Settings/HighlightStyle.cs`)

- C'est un record immuable, dont chaque propriété porte sa valeur par défaut : `Thickness`, `MinRatio`, `FastSpeed`,
  `WidthSmoothing` (ms), `Fluidity` (ms), `StartTaper`, `EndTaper`, `TaperFloor` et `Opacity`. Il est stocké dans
  `AppSettings.HighlightStyle`, et donc dans settings.json sous la forme d'un objet `"HighlightStyle": { … }`.
  - Clé absente → le style par défaut. Champ absent → sa valeur par défaut. Un ancien fichier se charge donc.
  - Les métadonnées sont générées par `SettingsJsonContext`, sans réflexion, à cause du découpage (trimming) de l'app
    macOS.
- La table `HighlightStyle.Fields` décrit chaque réglage : libellé et aide en français, minimum, maximum, pas, unité,
  lecture et écriture (`with`). Les deux fenêtres construisent leurs curseurs à partir de cette table. `Clamped()`
  ramène chaque valeur dans sa plage ; le `Highlighter` s'en sert, ce qui protège d'un fichier modifié à la main.
- `HighlightTrail.FullOpacity` devient `style.Opacity`, et `FadeOpacity` prend l'opacité de départ.

## Windows : overlay à transparence par pixel (`Recording/HighlightOverlay.cs`)

- L'overlay quitte la couleur clé, `Opacity` et `OnPaint`, qui passent par `SetLayeredWindowAttributes`, pour
  `UpdateLayeredWindow`. Il garde les styles étendus et ignore toujours `WM_DPICHANGED`.
- Sa surface est une DIB section 32 bits prémultipliée, de la taille de la zone, enveloppée dans un `Bitmap`
  `Format32bppPArgb`. GDI+ y peint en `AntiAlias` des disques et des enveloppes jaunes opaques. Chaque image efface
  puis repeint `changed` seulement, et le pousse par `UpdateLayeredWindowIndirect` (`prcDirty`).
- Le fondu passe par `SourceConstantAlpha`, sans repeindre ni recopier : `UpdateLayeredWindow` avec `hdcSrc` nul. À
  vérifier en premier ; à défaut, repousser la surface entière à chaque image du fondu.
- gdigrab (`CAPTUREBLT`) copie aussi les fenêtres à alpha par pixel : le test vidéo existant le garde.

## macOS (`Recording/MacHighlightOverlay.cs`)

- `TrailView` reçoit les disques. Il invalide `changed`, converti en points, puis, dans `DrawRect`, efface le rectangle
  sale et remplit les morceaux qui le touchent. Il y a un remplissage par morceau, pas un seul chemin : les
  orientations s'y annuleraient.
- `AlphaValue` reste l'opacité, et l'échelle est celle de l'écran de la zone.

## La fenêtre « Trait du surlignage »

- Elle s'ouvre par un bouton « Personnaliser le trait… », sous le champ « Surligner (maintenir) » des réglages, sur
  Windows (WPF) comme sur macOS (AppKit). Elle est modale aux réglages et désactivée pendant un enregistrement.
- Elle contient :
  - une zone d'aperçu (moitié claire, moitié sombre, texte et bouton) ;
  - les curseurs tirés de `HighlightStyle.Fields`, chacun avec sa valeur et son aide ;
  - les boutons « Valeurs par défaut », « Annuler » et « OK ».
- Maintenir le bouton gauche dans la zone trace exactement comme pendant un enregistrement :
  - le vrai `Highlighter`, avec le même relevé à 16 ms, le même trait et le même fondu ;
  - le vrai overlay, placé sur la zone à chaque appui ;
  - une entrée d'aperçu : appui dans la zone, bouton tenu, position du curseur de la plateforme.

  Un curseur déplacé modifie aussi le trait en cours.
- Pour cela, le `Highlighter` lit son style à la demande, à chaque image : celui des réglages pendant un enregistrement,
  le brouillon dans l'aperçu. Il peut aussi être activé sans le déclencheur configuré, puisque l'aperçu ne l'utilise
  pas et doit marcher même si le déclencheur est vide.
- « OK » remet le style dans le formulaire des réglages (`SettingsForm`), que leur « OK » enregistre par
  `SettingsEditor.TryApply`. « Annuler » ne change rien.

## Le labo (`tools/highlight-lab.html`)

- C'est le prototype, versionné dans le dépôt : une page autonome, sans dépendance. Elle offre le même relevé que l'app
  (`pointerrawupdate`, minuteur de 16 ms), l'algorithme ci-dessus, une comparaison avec la traînée d'avant, les
  échantillons bruts et lissés, et « Copier les valeurs ».
- Un commentaire d'en-tête dit que le cœur fait référence et que la page sert à essayer de nouvelles options
  graphiques avant de les porter.

## Tests

- **Cœur** (TDD, couverture ≥ 90 %) :
  - `BrushStroke` :
    - lent → pleine largeur, rapide → plus fin, jamais sous `MinRatio` ;
    - une accélération brusque → une largeur qui varie progressivement ;
    - échelle 2 → des rayons doublés pour le même geste ;
    - la fluidité rend un cercle bruité plus régulier, garde le bout sous la souris et ne mélange rien à travers une
      pause ;
    - aucun trou dans un virage serré ;
    - un échantillon seul → un point ;
    - l'effilement de fin seulement si `lifted` ;
    - `Hull` ;
    - `Changed` : identique → null, ajout → la queue seule, tête retirée → tout.
  - `HighlightStyle` : valeurs par défaut dans leurs plages, `Clamped`, `Fields` cohérente.
  - `Highlighter` : style lu à la demande, redessin au relâchement, activation sans déclencheur.
  - `SettingsEditor` : le style est appliqué.
- **Infrastructure** : settings.json garde le style à l'aller-retour, un fichier sans la clé ou avec un objet partiel
  se charge, et les fichiers `tests/fixtures/legacy` se chargent toujours.
- **Windows.Tests** :
  - l'overlay reste traversant, jamais activé, et non exclu de la capture ;
  - un trait en diagonale laisse des pixels d'alpha strictement entre 0 et 255, et tous jaunes ;
  - la traînée est dans la vidéo ;
  - la fenêtre d'aperçu construit un curseur par champ et « Valeurs par défaut » remet le style par défaut.
- **À la main** (`docs/test-checklist.md`, Windows et macOS) :
  - lent large, rapide fin ;
  - un cercle fluide ;
  - bords lisses, croisements pas plus foncés, texte lisible ;
  - 150 % et deux écrans ;
  - l'aperçu, puis un enregistrement, avec le style choisi ;
  - le MP4 image par image.

## Docs et version

- CLAUDE.md, puce « Highlight » : `BrushStroke`, `HighlightStyle`, l'overlay Windows à alpha par pixel, la fenêtre
  d'aperçu et le labo. README : une ligne sur « Personnaliser le trait… ».
- Nouvelle fonctionnalité et nouvelle clé de réglage → version MINOR (0.6.0), au moment de la release décidée par
  l'utilisateur.

## Laissé au constructeur

- Les signatures exactes (`Highlighter`, port `IHighlightOverlay` avec `Scale` et `Draw(discs, changed)`, entrée
  d'aperçu).
- La mise en page des deux fenêtres.
- La conversion de la zone d'aperçu en pixels physiques.
- Le pas exact de subdivision.

## Risques

- **`UpdateLayeredWindow` à `hdcSrc` nul** pour le fondu : repli décrit plus haut.
- **Coût d'une repeinte complète** (nouveau trait, quart retiré, curseur déplacé) : quelques dizaines de ms en 4K, une
  fois. À mesurer pendant une capture.
- **Overlay d'aperçu** : il est créé à chaque appui et caché à la fin du fondu. Si la fenêtre bouge pendant un tracé, il
  reste en place jusqu'au tracé suivant.
