# Ligne de commande

`romsieve-cli` exécute la même analyse et le même tri que l'interface, sans fenêtre : sur un NAS, sur un serveur
via SSH, ou dans une tâche planifiée. Il utilise les réglages de l'interface (ou un [profil](settings-and-profiles.md)),
et ne fait qu'une **simulation** tant que vous n'ajoutez pas `--execute`.

## Commandes

| Commande | Rôle |
|---|---|
| `romsieve-cli run` | Simule le tri (par défaut) ou l'exécute (`--execute`) |
| `romsieve-cli undo` | Affiche le dernier tri réel ; l'annule avec `--yes` |
| `romsieve-cli dat` | Écrit le DAT filtré du catalogue (`--output`), sans lire la source |
| `romsieve-cli quarantine` | Affiche la quarantaine des purges de la destination ; la vide avec `--empty --yes` |
| `romsieve-cli profiles` | Liste les profils enregistrés |
| `romsieve-cli patches` | Simule le tri et liste ses [patchs](patches.md) : non appliqués, avec les ROMs proposées, appliqués, avec leur ROM, et écartés ; enregistre vos choix (`--link --rom`, `--ignore`, `--auto`, avec `--yes`) ; avec `--name-from-notes --yes`, écrit le CRC32 dans le nom des IPS appliqués grâce à leur readme |
| `romsieve-cli --help` | Affiche l'aide (aussi `run --help`...) |
| `romsieve-cli --version` | Affiche la version |

## Exemples

```bash
# Simulation avec les réglages de l'interface, rapport et ROMs manquantes exportés
romsieve-cli run --report rapport.json --miss-list manquantes.txt

# Tri réel avec un profil enregistré depuis l'interface (« Enregistrer comme profil… »)
romsieve-cli run --profile "Amiga 1G1R" --execute

# D'autres dossiers, DAT ou mode de transfert que ceux des réglages
romsieve-cli run -s /roms/arrivee -d /roms/triees --dat "Commodore - Amiga.dat" --mode zip --execute

# Appliquer les patchs d'un dossier, et n'écrire que les versions patchées
romsieve-cli run --patches /roms/patchs --patch-only --execute

# Simuler sans appliquer les patchs, en gardant le dossier des patchs des réglages
romsieve-cli run --no-patches

# Un lot de DAT : tout un pack TOSEC, sous-dossiers compris, rangé comme le pack
romsieve-cli run --dat /dats/TOSEC --dat-recursive --dir-dat-mirror --execute
# ... ou deux de ses DAT seulement
romsieve-cli run --dat /dats/No-Intro --dat-only "Nintendo - Game Boy" --dat-only "Nintendo - Game Boy Color"
# ... ou le pack quotidien No-Intro tel qu'il a été téléchargé, sans l'extraire
romsieve-cli run --dat "No-Intro Love Pack (PC XML).zip" --dat-only "Nintendo - Game Boy"

# Compléter une destination non vide, ou la purger d'abord (son contenu est mis de côté dans _purged/)
romsieve-cli run --execute --append
romsieve-cli run --execute --purge --yes

# Les patchs d'une simulation
romsieve-cli patches --patches /roms/patchs

# Appliquer un IPS à une ROM, écarter un patch, puis oublier ce choix
romsieve-cli patches --patches /roms/patchs --link "Jeu (T-Fr).ips" --rom "Jeu (Japan).sfc"   # affiche le choix
romsieve-cli patches --patches /roms/patchs --link "Jeu (T-Fr).ips" --rom "Jeu (Japan).sfc" --yes
romsieve-cli patches --patches /roms/patchs --ignore "Jeu (Hack).bps" --yes
romsieve-cli patches --patches /roms/patchs --auto "Jeu (Hack).bps" --yes

# Le CRC32 écrit dans le nom des IPS appliqués grâce à leur readme, pour d'autres outils
romsieve-cli patches --patches /roms/patchs --name-from-notes          # affiche les nouveaux noms
romsieve-cli patches --patches /roms/patchs --name-from-notes --yes    # renomme

# Annuler le dernier tri réel
romsieve-cli undo           # affiche ce qui serait annulé
romsieve-cli undo --yes     # l'annule

# Quarantaine des purges de la destination (dossier _purged)
romsieve-cli quarantine                         # affiche ce qu'elle contient
romsieve-cli quarantine --empty --yes           # la vide définitivement
romsieve-cli quarantine -d /mnt/roms/sorted     # celle d'une autre destination

# DAT filtré du catalogue : les jeux du DAT que les réglages gardent, sans source ni simulation
romsieve-cli dat --output "Amiga (RomSieve).xml"
romsieve-cli dat --profile "Amiga 1G1R" --dat "Commodore - Amiga.dat" -o amiga-1g1r.dat
romsieve-cli dat --dat /dats/No-Intro -o /dats/No-Intro-1G1R      # un lot : un dossier, un DAT par DAT
```

`quarantine` lit la destination des réglages ou du profil (`--profile`), ou celle de `--destination`.

## Options de `dat`

| Option | Rôle |
|---|---|
| `-o`, `--output <fichier>` | Fichier DAT à écrire (obligatoire) : `.xml` pour Logiqx XML, `.dat` pour ClrMamePro. Avec des [listes de logiciels MAME](naming-conventions-and-dats.md#listes-de-logiciels-mame), le **dossier** à la racine duquel s'écrit un DAT par liste (`megadriv.xml`...), comme des listes de logiciels MAME quand elles ont été lues dans les fichiers `hash` de MAME, sinon en Logiqx XML ; avec un [lot de DAT](naming-conventions-and-dats.md#plusieurs-dat), le **dossier** où s'écrit un DAT par DAT du lot, chacun dans le format du sien. Un fichier `.xml` ou `.dat` est alors refusé (code 2), et les DAT du dossier que l'export n'a pas remplacés sont nommés |
| `-p`, `--profile <nom>` | Utilise un profil enregistré au lieu des réglages de l'interface |
| `--dat <fichier>` | Fichier DAT du catalogue, ou dossier ou archive (ZIP, 7z, RAR) de DAT : un lot No-Intro, Redump ou TOSEC, ou des listes de logiciels MAME ; une archive d'un seul DAT est ce DAT |
| `--dat-recursive` | Lit aussi les DAT des sous-dossiers du dossier de DAT ; une archive est toujours lue entière |
| `--dat-only <nom>` | N'utilise que ce DAT du lot (voir `run`) |
| `--categories <fichier>` | Fichier de catégories des machines MAME |
| `--ratings <fichier>` | Fichier de notes des machines MAME |

`dat` ne lit ni la source ni la destination : voir [Exports](exports.md) pour ce que contient le DAT filtré. Quand les
réglages ne gardent aucun jeu, aucun fichier n'est écrit et le code de sortie est 4.

## Options de `patches`

`patches` prend les options de `run` qui choisissent ce qui est simulé (`--profile`, `--source`, `--destination`,
`--dat`, `--dat-recursive`, `--dat-only`, `--dir-dat-mirror`, `--chd`, `--patches`, `--categories`, `--ratings`,
`--frontend`), sans
rien écrire dans la destination. Il exige un dossier des patchs et un DAT (code 3 sinon), et refuse un tri MAME.
Il applique les patchs même quand **Appliquer les patchs** est décoché dans les réglages.

| Option | Rôle |
|---|---|
| `--link <patch>` | Applique cet IPS à la ROM que donne `--rom`, quoi que disent son nom et son readme (voir [Choisir la ROM du patch](patches.md#choisir-la-rom-du-patch)). Un seul par commande ; refusé pour un UPS ou un BPS, qui désigne lui-même sa ROM (code 2) |
| `--rom <ROM>` | La ROM de `--link` : son nom, ou le CRC32 de la forme pour laquelle le patch a été fait (8 chiffres hexadécimaux). Une ROM à en-tête copieur a deux formes : son nom est refusé (code 2), avec ses deux CRC32 |
| `--ignore <patch>` | Écarte ce patch, quel que soit son format : la prochaine simulation ne l'applique pas (voir [Ne pas appliquer un patch](patches.md#ne-pas-appliquer-un-patch)). Répétable |
| `--auto <patch>` | Oublie votre choix pour ce patch : il s'applique de nouveau d'après ce qu'il enregistre, son nom ou son readme. Répétable ; refusé pour un patch sans choix (code 2) |
| `--name-from-notes` | Montre le nom que prendrait chaque IPS appliqué grâce à son readme, avec le CRC32 de sa ROM (`Hack.ips` devient `Hack [47e70e08].ips`), pour d'autres outils. Un IPS rangé dans une archive n'est pas renommé. Ne se combine pas avec les choix (code 2) |
| `-y`, `--yes` | Enregistre les choix, ou renomme les IPS de `--name-from-notes`. Sans lui, la commande montre ce qu'elle ferait, et le code de sortie est 6 |

La liste donne, pour chaque patch non appliqué, sa raison et les ROMs proposées pour un IPS, chacune avec ce qui la
propose ; pour chaque patch appliqué, sa ROM et ce qui la désigne (le patch lui-même, son nom, son readme ou votre
choix) ; puis les patchs écartés. Une ROM proposée n'est jamais prise d'office : choisissez-la avec `--link --rom`, ou
dans la fenêtre **Patchs** de l'interface. Un patch se désigne par son chemin dans le dossier des patchs ou par son nom,
sans tenir compte des majuscules ; un patch inconnu, un nom que portent plusieurs patchs, un patch illisible ou nommé
deux fois sont refusés (code 2), et rien n'est enregistré. Vos choix vont dans `patch_links.json`, à côté du fichier de
réglages utilisé (`--config`) : aucun fichier de patch n'est modifié. Un choix impossible à enregistrer, ou un renommage
refusé (nom déjà pris, par exemple), donne le code 4.

## Options de `run`

| Option | Rôle |
|---|---|
| `--execute` | Trie réellement. Sans elle, rien n'est écrit. |
| `-p`, `--profile <nom>` | Utilise un profil enregistré au lieu des réglages de l'interface |
| `-s`, `--source <dossier>` | Dossier source |
| `-d`, `--destination <dossier>` | Dossier de destination |
| `--dat <fichier>` | Fichier DAT, ou dossier ou archive (ZIP, 7z, RAR) de DAT : un lot No-Intro, Redump ou TOSEC, ou des listes de logiciels MAME ; une archive d'un seul DAT est ce DAT ([DAT dans une archive](naming-conventions-and-dats.md#dat-dans-une-archive)). Une seule fois. |
| `--dat-recursive` | Lit aussi les DAT des sous-dossiers du dossier de DAT (un lot) ; une archive de DAT est toujours lue entière |
| `--dat-only <nom>` | Ne trie qu'avec ce DAT du lot, désigné par le nom de son en-tête, sans tenir compte des majuscules. Répétable ; remplace les DAT décochés des réglages. Un nom absent du lot est refusé (code 2), avec les noms du lot |
| `--dir-dat-mirror` | Place le dossier de chaque DAT du lot sous les dossiers de son chemin dans le dossier ou l'archive de DAT. Refusé (code 2) quand **Un dossier par DAT** est décoché dans les réglages, ou avec un frontend (`--frontend`, ou celui des réglages) |
| `--chd <dossier>` | Dossier des CHD d'une collection MAME, s'ils ne sont pas dans la source |
| `--patches <dossier>` | Dossier des [patchs](patches.md) (IPS, UPS, BPS) à appliquer aux ROMs identifiées, même quand **Appliquer les patchs** est décoché dans les réglages |
| `--patch-only` | N'écrit que les versions patchées des jeux qui en ont : ni leur ROM d'origine, ni (avec le 1G1R) la ROM retenue pour leur jeu |
| `--no-patches` | N'applique aucun patch, sans changer le dossier des patchs des réglages (refusé avec `--patches`, code 2) |
| `--categories <fichier>` | Fichier de catégories des machines MAME (`catver.ini`, `catlist.ini`) |
| `--ratings <fichier>` | Fichier de notes des machines MAME (`bestgames.ini`) |
| `--mode <mode>` | `copy`, `move`, `hardlink`, `symlink` ou `zip` |
| `--preserve-archives` | Conserve les archives source contenant un seul jeu sans les extraire (modes copy, move, hardlink, symlink) |
| `--frontend <frontend>` | Range chaque DAT dans le dossier que ce frontend donne à sa plateforme ([Pour un frontend](destination-and-transfers.md#pour-un-frontend)) : `none`, `batocera`, `retropie`, `es-de`, `recalbox` ou `mister`. Remplace le frontend des réglages le temps de l'exécution ; une autre valeur est refusée (code 2), avec la liste |
| `--append` | Complète une destination non vide |
| `--purge` | Purge d'abord une destination non vide (exige `--execute` et `--yes`) |
| `-y`, `--yes` | Confirme une purge ou une annulation (et, avec `patches`, un renommage) |
| `--force-foreign-purge` | Autorise la purge d'un dossier sans le marqueur `.romsieve` |
| `--report <fichier>` | Exporte le rapport (`.json` ou `.csv`) |
| `--miss-list <fichier>` | Exporte les ROMs manquantes (`.txt` ou `.csv`, exige un DAT) |
| `--fixdat <fichier>` | Exporte le fixdat des ROMs manquantes (exige un DAT) ; avec des listes de logiciels MAME ou un lot de DAT, le dossier où s'écrit un fixdat par liste ou par DAT (`fixDat_megadriv.xml`...) |

Options communes à toutes les commandes :

| Option | Rôle |
|---|---|
| `--config <fichier>` | Un autre fichier de réglages. Ses profils, ses journaux, ses rapports d'erreur et son cache des sommes de contrôle sont ceux situés à côté de lui. |
| `--lang <fr\|en\|es>` | Langue des messages (par défaut, celle de l'interface, sinon l'anglais) |
| `-q`, `--quiet` | N'affiche que les avertissements, les erreurs et le bilan |

Tous les autres réglages (filtres, 1G1R, modèle, dossier des versions patchées, conflits...) viennent du fichier de
réglages ou du profil : préparez-les dans l'interface, puis enregistrez-les comme profil.

Avec un [lot de DAT](naming-conventions-and-dats.md#plusieurs-dat), le bilan donne, après les totaux, une ligne par DAT
où un jeu a été trouvé (*Nintendo - Game Boy : 12 jeux trouvés, 14/1 600 ROMs (0,9 %)*), puis le nombre des autres
DAT ; avec `--miss-list` ou `--fixdat`, une ligne compte les DAT complets et ceux sans aucun jeu trouvé, qui n'ont rien
à exporter.

Quand les patchs sont appliqués, une ligne du bilan compte les versions patchées et les patchs non appliqués (*Patchs :
2 versions patchées, 1 patch non appliqué.*) ; chaque patch non appliqué a sa ligne dans le journal.

Avec un [frontend](destination-and-transfers.md#pour-un-frontend), le bilan se termine par *Frontend : MiSTer*, suivi
des remarques de l'exécution, même avec `--quiet` : les DAT qui gardent leur nom faute de plateforme, les archives que le
frontend ne lit pas, et les dossiers qui réunissent plusieurs DAT.

## Garde-fous

* Sans `--execute`, rien n'est écrit.
* La source doit exister, le DAT doit être trouvé, et la destination ne doit pas chevaucher la source, ni le dossier
  des patchs. Quand les patchs sont appliqués, un dossier des versions patchées qui sortirait de la destination (chemin
  absolu, `..`, `_purged` comme premier dossier...) est refusé en code 3, pour `run` comme pour `patches`. Un dossier de
  DAT que le tri refuserait (deux DAT du même nom, un DAT de machines MAME, des listes de logiciels mêlées à d'autres
  DAT, tous les DAT décochés) est refusé avant tout, en code 3.
* Un tri réel dans une destination non vide exige `--append`, ou `--purge --yes` ; un dossier sans le marqueur
  `.romsieve` exige en plus `--force-foreign-purge` pour être purgé. **Compléter une destination non vide**, coché
  dans la configuration ou le profil, ne dispense pas de `--append`, comme l'interface demande encore confirmation ;
  et `--purge --yes` purge la destination même quand cette option est cochée.
* `undo` se contente d'afficher le tri à annuler tant que `--yes` n'est pas ajouté. Après une annulation incomplète
  (fichier verrouillé, emplacement d'origine occupé), `undo --yes` reprend les seules opérations restantes.
* `patches` n'enregistre aucun choix et ne renomme rien sans `--yes` ; `--name-from-notes` ne touche qu'au nom des IPS
  du dossier des patchs.
* `quarantine --empty` exige `--yes`, et ne touche qu'à la quarantaine d'une destination marquée par RomSieve : le
  contenu supprimé ne pourra plus être remis en place par l'annulation des tris qui l'ont purgé.
* **Ctrl+C** arrête l'exécution proprement : le fichier en cours d'écriture est supprimé.

## Codes de sortie

| Code | Signification |
|---|---|
| 0 | Succès |
| 1 | Erreur inattendue (un rapport d'erreur est enregistré dans `crashes/` à côté des réglages, et son chemin affiché) |
| 2 | Arguments invalides (option inconnue, valeur invalide, options incompatibles, `--dat` répété, `--dat-only` sans lot ou d'un DAT absent, `--dir-dat-mirror` sans dossier par DAT ou avec un frontend, `--frontend` d'une valeur inconnue, format d'export non pris en charge, `dat --output` sur le DAT lu, `patches --link` sans `--rom`, d'un UPS ou d'un BPS, ou d'une ROM inconnue, ambiguë ou à deux formes, `patches` d'un patch inconnu ou ambigu, `--auto` sans choix enregistré, `--name-from-notes` avec un choix, `--no-patches` avec `--patches`) |
| 3 | Configuration invalide (fichier de réglages ou profil absent ou illisible, source, DAT ou destination introuvable, dossier ou archive de DAT refusé, dossiers qui se chevauchent, modèle qui commence par `_purged` ou `.romsieve`, dossier des versions patchées qui sortirait de la destination) |
| 4 | Terminé, mais des fichiers sont en erreur (dont un tri arrêté par une destination pleine ou un journal impossible à écrire), un export a échoué (ou `dat` n'a gardé aucun jeu), des opérations n'ont pas pu être annulées ou des éléments de la quarantaine n'ont pas pu être supprimés |
| 5 | Interrompu (Ctrl+C) |
| 6 | Confirmation requise (`--yes`, `--append` / `--purge --yes`, `--force-foreign-purge`, `quarantine --empty`, `patches --link`, `--ignore`, `--auto` ou `--name-from-notes` sans `--yes`) |
| 7 | Rien à annuler |

Par exemple, dans un script :

```bash
romsieve-cli run --profile "Quotidien" --execute --append --quiet
case $? in
  0) echo "Trié" ;;
  4) echo "Trié avec des erreurs, voir le journal" ;;
  *) echo "Non trié" ;;
esac
```
