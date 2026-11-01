# Exports

Le menu **Exporter**, à droite de la barre d'actions, est disponible après une simulation ou un tri, et dès qu'un
fichier DAT est choisi pour le DAT filtré du catalogue. Ses premières entrées exportent les résultats de la dernière
exécution ; la dernière, sous un trait, n'a besoin que du DAT. Chacune n'est active que si son export est possible, et
le menu lui-même dès que l'une d'elles l'est. Aucun export n'est jamais écrit sur le DAT lu : choisir son fichier
comme cible est refusé, pour que le DAT reste entier.

## Rapport (JSON, CSV)...

Enregistre le résultat de la dernière exécution :

* **JSON** (`.json`) : chaque fichier avec son statut, sa raison, son titre, sa somme de contrôle et son chemin cible,
  plus les statistiques ; adapté aux scripts et à l'archivage.
* **CSV** (`.csv`) : le même tableau, à ouvrir dans un tableur.

## DAT de votre collection...

Génère un fichier DAT des ROMs de votre source que la dernière exécution a **retenues**, pour que d'autres
gestionnaires de ROMs (ClrMamePro, RomVault, Igir...) puissent contrôler votre collection triée :

* **Logiqx XML** (`.xml`), le format le plus largement reconnu ;
* **ClrMamePro** (`.dat`).

Un fichier identifié par le DAT reprend sa taille et ses sommes de contrôle dans le DAT, sans être relu ; celles des
autres fichiers sont calculées pendant l'export. Avec un DAT MAME, chaque set retenu est écrit comme sa machine du DAT
(ROMs, disques, parent et BIOS), sans lire les archives ; un set merged est écrit avec les clones dont son archive
contient les ROMs. Le message final compte les fichiers du DAT comme un gestionnaire de ROMs : une ROM que MAME liste
plusieurs fois sous le même nom dans une machine compte pour une. Pendant l'export, une barre de progression montre les fichiers
déjà traités, et **Arrêter l'export** l'interrompt sans rien écrire. L'entrée est disponible dès qu'au moins un fichier
est retenu.

Le DAT suit les cases de la table : tant que des jeux sont décochés
([Trier une partie de la simulation](simulation-table.md#trier-une-partie-de-la-simulation)), il ne décrit que les jeux
cochés et les sets MAME dont ils ont besoin, les mêmes fichiers que ceux qu'écrirait **Trier la sélection**. Avec tous
les jeux cochés, il décrit toute la simulation.

## Miss List (TXT, CSV, fixdat)...

Liste les ROMs du DAT qui **manquent** à votre collection. Il faut un DAT et une exécution terminée. Trois formats sont
proposés :

* **Texte** (`.txt`) : un court en-tête (nom et version du DAT, totaux) puis un nom de ROM manquante par ligne ;
* **CSV** (`.csv`) : à ouvrir dans un tableur ;
* **Fixdat Logiqx XML** (`.xml`) : un DAT standard qui ne contient que les ROMs manquantes, regroupées par jeu avec leurs
  relations parent / clone. Chargez-le dans un autre gestionnaire de ROMs pour chercher exactement ce qui vous manque.

Les ROMs des jeux reconnus seulement par le nom d'un CHD (voir
[Conventions de nommage et fichiers DAT](naming-conventions-and-dats.md#fichiers-chd)) ne sont ni trouvées ni
manquantes : elles ne sont pas listées.

## DAT filtré du catalogue (sans source)...

Génère le DAT des jeux du **catalogue** (le fichier DAT choisi à l'étape Collection) que vos réglages gardent, **sans
simulation et sans lire la source** : les filtres, les langues et les pays, le 1G1R et, pour MAME, les filtres des
machines, les catégories, les notes et le volume s'appliquent aux jeux du DAT comme à une collection complète nommée
d'après lui. Chargez le résultat dans un gestionnaire de ROMs (ClrMamePro, RomVault, Igir...) pour constituer ou
contrôler une collection des seuls jeux que vous voulez : RomSieve lui-même ne reconstruit jamais un set.

Les deux exports DAT décrivent des choses différentes :

| | DAT de votre collection | DAT filtré du catalogue |
|---|---|---|
| Il faut | Une exécution terminée | Un fichier DAT |
| Il décrit | Les fichiers de votre collection que l'exécution a retenus (les seuls jeux cochés, si certains sont décochés) | Les jeux du DAT que vos réglages garderaient |
| Vainqueur 1G1R | La meilleure version parmi celles que vous avez | La meilleure version du DAT |

Avec les mêmes réglages et une collection complète, les deux gardent les mêmes jeux. Sinon, ils diffèrent : les mêmes
réglages ne choisissent pas parmi les mêmes versions. Par exemple, avec les priorités FR, puis EU, puis US, et une
source qui n'a que *Zelda (USA)*, le DAT de votre collection nomme *Zelda (USA)* et le DAT filtré du catalogue *Zelda
(France)*, que vous n'avez pas ; un jeu absent de votre source ne figure que dans le second. Le DAT filtré du catalogue
décrit donc la collection à atteindre, à charger dans un gestionnaire de ROMs, et le DAT de votre collection ce que vous
avez. Quelques réglages sont sans effet sur le DAT filtré du catalogue :

* **Sets complets uniquement**, l'emprunt de disques propres et le critère « set multi-disques complet » : tout jeu du
  catalogue est complet.
* Les vérifications faites sur les fichiers (SHA-1, en-têtes copieurs, CHD lus) et les choix 1G1R manuels de la table
  des résultats.
* **Volume** (MAME) : la taille d'un set est celle de ses ROMs telle que le DAT la donne, avant compression, et ses CHD
  ne comptent pas, le DAT ne donnant pas leur taille.

Chaque jeu est écrit en entier, avec toutes ses ROMs (et les pistes d'un disque). Un set MAME est écrit comme sa machine
du DAT, à la manière d'un set non-merged : les sets dont il a besoin (devices dont il ne liste pas les ROMs) sont gardés
avec lui. Une relation vers un jeu écarté (le parent d'un clone gardé, le BIOS d'un jeu gardé) est retirée, le jeu se
suffisant alors à lui-même. L'en-tête du DAT garde son nom, suivi de « (RomSieve) », et sa version. Le format suit l'extension choisie : `.xml`
pour Logiqx XML, `.dat` pour ClrMamePro (les DAT des listes de logiciels MAME s'écrivent dans un dossier : voir
plus bas). Le pied de fenêtre montre l'étape en cours, et
**Arrêter l'export** l'interrompt sans rien écrire ; quand les réglages ne gardent aucun jeu, aucun fichier n'est écrit.

## Listes de logiciels MAME

Avec des [listes de logiciels MAME](naming-conventions-and-dats.md#listes-de-logiciels-mame), une liste ou un dossier
de listes, le DAT de votre collection et le DAT filtré du catalogue demandent un **dossier**, pas un fichier : un DAT
par liste s'écrit à sa racine, au nom de la liste (`megadriv.xml`, `gamegear.xml`...), comme un gestionnaire de ROMs
les range dans son `DatRoot` et comme MAME les cherche. L'en-tête de chaque DAT ne porte que le nom de la liste :
RomVault nomme le dossier d'un DAT d'après son en-tête, et MAME ne trouve les logiciels d'une liste que dans le dossier
de ce nom.

* Quand les listes viennent des fichiers de MAME lui-même (son dossier `hash`, ou la sortie de `mame -getsoftlist`),
  les DAT s'écrivent comme des **listes de logiciels MAME**, le format que MAME lit dans son dossier `hash` : chaque
  logiciel gardé est recopié tel quel du fichier de MAME, avec ses parties, ses zones de données et ses
  caractéristiques, et un clone dont le parent est écarté perd son `cloneof`. Le dossier peut être donné à MAME comme
  `hashpath`. Sinon (DAT SabreTools), ils s'écrivent en Logiqx XML. Le format ClrMamePro n'est pas proposé pour les
  listes : il perdrait ce dont MAME a besoin.
* Quand le dossier contient déjà des DAT, RomSieve les nomme et demande avant d'écrire : le DAT de chaque liste
  exportée remplace celui du même nom, et les autres restent. Après l'export, le journal nomme les DAT du dossier que
  l'export n'a pas remplacés : un gestionnaire de ROMs qui lit le dossier les lit aussi, retirez donc ceux des listes
  que vous ne voulez plus.
* RomSieve n'écrit jamais sur les fichiers d'où les listes ont été lues : choisissez un autre dossier que le dossier
  `hash` de MAME.
* Le fixdat (**Miss List (TXT, CSV, fixdat)...**, `.xml`) s'écrit en un fixdat par liste à laquelle il manque quelque chose,
  nommé `fixDat_<liste>.xml`, dans le dossier du fichier choisi. La Miss List nomme chaque logiciel avec sa liste
  (`gamegear/shinobi`).

## Depuis la ligne de commande

`romsieve-cli run` produit les mêmes exports avec `--report`, `--miss-list` et `--fixdat`, et
`romsieve-cli dat --output <fichier>` le DAT filtré du catalogue : voir [Ligne de commande](command-line.md).
