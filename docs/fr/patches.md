# Patchs

Un patch est un petit fichier qui ne contient que les **différences** entre une ROM et une version modifiée de cette
ROM : une traduction, une correction de bogues, un hack. On le diffuse à la place de la ROM modifiée, puisque diffuser
la ROM reviendrait à diffuser le jeu ; c'est à chacun de l'appliquer à sa propre copie.

RomSieve applique les patchs d'un dossier aux ROMs de votre collection : chaque patch dont la ROM d'origine est
identifiée dans la source donne une **version patchée**, écrite dans la destination à côté de la version d'origine. La
ROM d'origine n'est **jamais modifiée**.

Un patch ne s'applique qu'à la ROM exacte pour laquelle il a été fait : même région, même révision, même dump. RomSieve
la trouve pour vous par sa somme de contrôle (CRC32), ce qui demande un [fichier DAT](naming-conventions-and-dats.md#fichiers-dat).

## Formats pris en charge

| Format | Extension | ROM d'origine trouvée par | Contrôles |
|---|---|---|---|
| **IPS** | `.ips` | le CRC32 écrit dans le nom du patch, ou dans son readme (voir [Rapprochement](#rapprochement)) | aucun : le format n'enregistre aucune somme de contrôle |
| **IPS32** | `.ips` ou `.ips32` | le CRC32 écrit dans le nom du patch, ou dans son readme | aucun |
| **UPS** | `.ups` | le CRC32 et la taille qu'il enregistre | CRC32 du patch, de la ROM d'origine et du résultat |
| **BPS** | `.bps` | le CRC32 et la taille qu'il enregistre | CRC32 du patch, de la ROM d'origine et du résultat |

Le format est lu dans le contenu du fichier, pas seulement dans son extension. UPS et BPS sont les plus sûrs : ils ne
s'appliquent qu'à la bonne ROM et vérifient leur résultat. Un patch abîmé ou tronqué est refusé avant d'être appliqué.

Les autres formats (xdelta, PPF, APS, BSDiff…) ne sont pas pris en charge : leurs fichiers sont ignorés. Les sets
d'arcade MAME et les logiciels des listes MAME ne sont jamais patchés.

## Dossier des patchs

Dans l'étape **Collection**, le cadre **Patchs** a le champ **Dossier des patchs**, avec son bouton **Parcourir...**. Il
est toujours affiché, quelle que soit la convention.

* Le dossier est lu avec ses **sous-dossiers**. Seuls les fichiers `.ips`, `.ips32`, `.ups` et `.bps` sont lus, et les
  textes `.txt` et `.nfo` qui peuvent donner la ROM d'origine d'un IPS (voir [Rapprochement](#rapprochement)).
* Les patchs rangés dans des **archives** ZIP, 7z ou RAR du dossier sont lus aussi.
* Le dossier peut se trouver dans la source : ses fichiers ne sont pas triés comme des fichiers de la collection. S'il
  est la source elle-même, seuls les patchs sont mis de côté, et vos ROMs sont triées comme d'habitude.
* La destination ne doit pas chevaucher le dossier des patchs, ni dans un sens ni dans l'autre : sous l'étape **Sortie**,
  une ligne le signale (*Chevauche les patchs*), et la simulation comme le tri sont refusés. Une purge ne touche jamais
  au dossier des patchs.

Le journal de la simulation dit combien de patchs ont été trouvés et combien ont donné une version patchée.

## Rapprochement

Seule une ROM **identifiée dans le DAT** peut recevoir un patch, quel que soit son statut : une version écartée par le
1G1R reçoit son patch comme une autre. C'est souvent le cas d'une traduction, faite pour la version japonaise d'un jeu
que le 1G1R écarte au profit de la version américaine.

* Un **UPS** ou un **BPS** désigne sa ROM d'origine par le CRC32 et la taille qu'il enregistre.
* Un **IPS** n'enregistre rien : son nom doit donner le CRC32 de la ROM d'origine, en 8 chiffres hexadécimaux, entre
  crochets, entre parenthèses ou séparé du reste du nom (`Jeu (Japan) (T-En) [47E70E08].ips`, `(0x47e70e08)`). C'est la
  règle d'igir.
* Un IPS dont le nom ne donne aucun CRC32 le prend dans son **readme**, s'il en a un. La plupart des patchs le donnent,
  sous la forme qu'emploie romhacking.net (`File/ROM CRC32: 47E70E08`) ou une autre (`CRC32 : 0x47e70e08`). Voir
  [Readme des IPS](#readme-des-ips).
* Un IPS sans CRC32, ni dans son nom ni dans son readme, n'est pas appliqué.
* Une ROM avec un **en-tête copieur** (iNES pour la NES, en-tête de 512 octets pour la Super Nintendo, en-tête Lynx)
  est cherchée sous ses deux formes, avec et sans en-tête : un patch fait pour l'une ou pour l'autre trouve sa ROM. Le
  patch s'applique à la forme qu'il attend, et le résultat est écrit tel que le patch le produit.
* Si plusieurs fichiers de la source ont le même CRC32, le patch s'applique à celui que le tri écrit, sinon au premier
  dans l'ordre des chemins.
* Plusieurs patchs pour une même ROM donnent chacun leur version patchée.

### Readme des IPS

Un readme est un fichier texte `.txt` ou `.nfo` d'au plus 1 Mo, rangé à côté du patch : dans le même dossier du dossier
des patchs, ou dans la même archive, au même endroit. Un readme sur le disque n'accompagne jamais un patch rangé dans
une archive, ni l'inverse.

* Un readme qui porte le nom du patch (`Jeu (T-Fr).txt` pour `Jeu (T-Fr).ips`, sans tenir compte des majuscules) est
  le sien.
* Sinon, les readme d'un dossier ou d'une archive qui ne contient **qu'un seul patch** sont les siens.
* Dans un dossier de plusieurs patchs, un readme qui ne porte le nom d'aucun n'en accompagne aucun : il ne dit pas de
  quel patch il parle.

RomSieve ne lit que les lignes où figure le mot **CRC**, et sur elles les CRC32 qui le suivent. Un readme donne souvent
aussi le CRC32 du résultat patché : il ne désigne aucune ROM de la source, et il est ignoré. Le nom du patch l'emporte
toujours sur son readme, et un UPS ou un BPS, qui enregistre lui-même sa ROM d'origine, ne lit jamais le sien.

Un readme donne parfois le CRC32 d'une ROM **avec et sans son en-tête copieur**, sur deux lignes (par exemple
`File CRC32` et `ROM CRC32`). Un IPS ne dit pas laquelle des deux formes il attend : RomSieve ne l'applique pas. Le
readme dit en général pour quelle forme le patch a été fait. Écrivez alors le CRC32 de cette forme dans le nom du patch.

Le panneau de détail d'une version patchée nomme le readme qui a donné le CRC32.

## La version patchée

* **Nom** : celui de la ROM d'origine, suivi d'un libellé du patch entre crochets, avec l'extension de la ROM
  d'origine (voir [Choix du nom](#choix-du-nom)).
* **Dossier** : celui que la ROM d'origine aurait dans la destination, même quand le 1G1R l'écarte.
* **Jeu** : la version patchée appartient au jeu de sa ROM d'origine. Elle reste en dehors du 1G1R, des multi-disques
  et des filtres : elle n'est jamais écartée à la place de son origine, ni ne l'écarte.
* **Conflits** : une collision avec un fichier déjà présent dans la destination suit le mode de conflit choisi (voir
  [Conflits](destination-and-transfers.md#conflits)). Si deux patchs donnent le même nom (le même patch en IPS et en
  BPS, par exemple), la seconde version patchée prend le suffixe `_01` quel que soit le mode : les deux sont écrites.

### Choix du nom

Le nom part toujours de celui de la ROM d'origine, tel que le DAT le donne : la région et la révision viennent du DAT,
jamais de l'auteur du patch, et chaque version patchée se range à côté de sa ROM d'origine. Le libellé entre crochets
est tiré du nom du patch :

| Patch | ROM d'origine | Version patchée |
|---|---|---|
| `Jeu (Japan) (T-En).bps` | `Jeu (Japan).sfc` | `Jeu (Japan) [T-En].sfc` |
| `Jeu (T-En v1.2).bps` | `Jeu (Japan).sfc` | `Jeu (Japan) [T-En v1.2].sfc` |
| `Jeu (T-En) [47E70E08].ips` | `Jeu (Japan).sfc` | `Jeu (Japan) [T-En].sfc` |
| `SMB3_eng_v1.2.ups` | `Super Mario Bros. 3 (Japan).nes` | `Super Mario Bros. 3 (Japan) [SMB3_eng_v1.2].nes` |
| `Alpha (1990)(Ocean)[h Hack].bps` | `Alpha (1990)(Ocean).adf` | `Alpha (1990)(Ocean)[h Hack].adf` |

* Ce qui répète la ROM d'origine est retiré : son nom entier ou son titre, quand le nom du patch commence par eux, et
  son CRC32, avec ses crochets ou ses parenthèses. Les parenthèses et crochets qui restent disparaissent.
* Les crochets marquent un dump modifié, comme chez TOSEC (`[h]`, `[tr]`) : les parenthèses décrivent, chez No-Intro et
  Redump, une version officielle. Pour une ROM TOSEC, le libellé suit le nom sans espace, comme un drapeau TOSEC.
* Un nom de patch **générique** (`patch`, `patched`, `hack`, `fix`, `translation`, `rom`, `game`, `english`, `eng`,
  `update`, ou une version seule comme `v1.1`) cède la place au nom de l'archive qui contient le patch, ou de son dossier
  dans cette archive, puis au nom de son dossier dans le dossier des patchs, si ce dossier ne contient pas d'autre patch
  (un dossier de plusieurs patchs les range, par système ou par genre, et ne dit rien de chacun). `Traduction CT/patch.ips`
  donne `Chrono Trigger (Japan) [Traduction CT].sfc`. Sans meilleur libellé, le nom générique est gardé.
* Un patch qui ne donne aucun libellé (nommé comme sa ROM d'origine, comme le demandent les émulateurs qui appliquent
  un patch au lancement) prend le libellé `patched` : `Jeu (Japan) [patched].sfc`. La version patchée ne porte jamais le
  nom de sa ROM d'origine.

### N'écrire que les versions patchées

Cochez **N'écrire que les versions patchées**, à côté du champ, pour qu'une ROM d'origine remplacée par au moins une
version patchée ne soit pas écrite. Le tableau la montre **Exclu**, avec la raison *Remplacée par sa version patchée*.
Une ROM que le 1G1R écartait déjà garde sa raison. L'option est grisée sans dossier des patchs.

## Modes de transfert

| Mode | Version patchée |
|---|---|
| **Copier** | Un fichier, écrit à côté de la ROM d'origine |
| **Déplacer** | Un fichier, écrit **avant** que la ROM d'origine ne quitte la source. Avec *N'écrire que les versions patchées*, la ROM d'origine remplacée reste dans la source. |
| **Hard link**, **Symlink** | Une **copie** : un lien ne peut pas porter un contenu modifié. Le journal le dit. |
| **ZIP** | Une archive à elle, au format TorrentZip, comme celle de tout autre jeu |

Une ROM d'origine rangée dans une archive est extraite à côté de la cible le temps d'appliquer le patch, puis supprimée.
L'archive n'est jamais modifiée.

L'écriture suit les mêmes règles de sûreté que toute autre (voir [Sécurité, purge et annulation](safety-and-undo.md)) :
le fichier n'apparaît sous son nom qu'une fois complet, et un tri arrêté ou en échec ne laisse aucun fichier tronqué. Le
CRC32 de la ROM d'origine est vérifié pendant l'application : une ROM modifiée depuis son identification est refusée,
même pour un IPS. Pour un UPS ou un BPS, le CRC32 du résultat l'est aussi : un résultat faux passe la version patchée
en **Erreur**, sans rien laisser dans la destination. Avec **Vérifier les fichiers copiés**, la version patchée est
relue après son écriture.

## Patchs non appliqués

Un patch qui ne donne aucune version patchée a sa ligne dans le journal, avec sa raison :

| Raison | Ce qu'il faut faire |
|---|---|
| Aucune ROM identifiée de ce CRC32 dans la source | La ROM d'origine manque, ou elle n'est pas identifiée par le DAT |
| Ni le nom du patch IPS ni un texte qui l'accompagne ne donnent le CRC32 de la ROM d'origine | Choisissez sa ROM dans la fenêtre **Patchs** (voir [Nommer un IPS](#nommer-un-ips)), ou ajoutez le CRC32 au nom du patch, par exemple `[1a2b3c4d]` |
| Aucune ROM identifiée dans la source pour les CRC32 que donne un readme | La ROM d'origine manque, ou elle n'est pas identifiée par le DAT |
| Patch illisible | Le fichier est tronqué, abîmé, ou d'un autre format |
| Le nom du patch donne les CRC32 de plusieurs ROMs de la source | Ne gardez dans le nom que le CRC32 de la bonne ROM |
| Un readme donne les CRC32 de plusieurs ROMs de la source | Écrivez dans le nom du patch le CRC32 de la bonne ROM |
| Un readme donne le CRC32 d'une ROM avec et sans son en-tête copieur | Écrivez dans le nom du patch le CRC32 de la forme pour laquelle il a été fait |
| La ROM d'origine fait partie d'un jeu en plusieurs fichiers | Un `.cue` et ses pistes, ou un jeu en plusieurs disques : ces jeux ne sont pas patchés |
| La ROM n'a pas la taille que le patch attend | Ce n'est pas la ROM pour laquelle le patch a été fait |

Sans DAT, les patchs ne sont pas lus : le journal le dit.

## Nommer un IPS

La plupart des IPS téléchargés n'ont de CRC32 ni dans leur nom ni dans leur readme. Plutôt que de le chercher et de
renommer le fichier à la main, ouvrez la fenêtre **Patchs** : après une simulation, cliquez sur « *n* patchs non
appliqués » dans le bandeau des résultats (ou sur « *n* patchs à nommer », quand aucun patch n'est resté non appliqué
mais que des IPS l'ont été grâce à leur readme).

![La fenêtre Patchs](../images/fr/patches_window.png)

La fenêtre liste les patchs non appliqués, puis les IPS appliqués grâce à leur readme. Pour un patch sélectionné :

* **ROM d'origine** propose les ROMs identifiées dont le nom ou le titre commence le nom du patch, ou celui de son
  archive, ou celui de son dossier quand ce dossier ne contient que lui (`Traduction CT/patch.ips`). Les noms génériques
  (`patch`, `fix`, `v1.1`…) ne comptent pas. La ROM entière passe avant le titre seul, puis la ROM que le tri écrit. Pour
  un patch dont le nom ou le readme désigne plusieurs ROMs, ce sont ces ROMs.
* **Autre ROM** cherche parmi toutes les ROMs identifiées par la simulation, quand aucune ROM proposée n'est la bonne : un
  titre traduit (`Super Mario Frères.ips` pour `Super Mario Bros.`) ou abrégé n'en propose aucune. **Prendre cette ROM**
  (ou un double clic) la choisit.
* **Forme** choisit, pour une ROM à en-tête copieur, le CRC32 avec ou sans l'en-tête. Un IPS ne dit pas pour laquelle il
  a été fait : son readme le dit en général. La liste est grisée quand la ROM n'a qu'une forme.

Une ROM proposée n'est jamais prise d'office : cochez chaque patch à renommer, vérifiez le **Nouveau nom** affiché, puis
cliquez sur **Renommer**. Le CRC32 s'ajoute avant l'extension (`Jeu (T-Fr).ips` devient `Jeu (T-Fr) [47e70e08].ips`),
la ligne dit ce qu'il en est, et le journal le note. **Relancer la simulation** ferme la fenêtre et relance la simulation,
qui applique les patchs renommés. Le CRC32 ajouté n'apparaît pas dans le nom de la version patchée (voir
[Choix du nom](#choix-du-nom)).

* **Cocher les IPS appliqués par un readme** coche les IPS que la simulation a déjà appliqués grâce à leur readme :
  écrire leur CRC32 dans leur nom les fait aussi appliquer par d'autres outils, comme igir.
* Un patch rangé dans une **archive** n'est jamais renommé, puisqu'une archive n'est jamais modifiée : la fenêtre donne le
  CRC32 de la ROM choisie, et il faut extraire le patch pour écrire ce CRC32 dans son nom.
* Un patch dont le nom donne déjà un CRC32, un UPS ou un BPS ne se choisit pas : un CRC32 dans son nom ne le réparerait
  pas.
* Rien n'est écrasé : un nom déjà pris laisse le patch tel quel, et sa ligne le dit. Le renommage n'est pas inscrit au
  journal des tris, et **Annuler le dernier tri** ne le défait pas : il se défait à la main.

## Dans les résultats

* Le tableau marque chaque version patchée d'une pastille **Patchée**. Dans la vue groupée, elle suit la version retenue
  de son jeu. Le panneau de détail nomme le patch, son format et la ROM d'origine ; celui d'une ROM d'origine nomme ses
  versions patchées. Une version patchée ne peut pas être choisie comme gagnant 1G1R.
* Le bandeau compte les versions patchées dans la pastille **Patchées**, qui n'affiche qu'elles, et les patchs non
  appliqués dans « *n* patchs non appliqués », dont l'infobulle donne la liste avec leurs raisons et qu'un clic ouvre
  dans la fenêtre **Patchs** (voir [Nommer un IPS](#nommer-un-ips)).
* Dans un [tri partiel](simulation-table.md#trier-une-partie-de-la-simulation), une version patchée suit la case de son
  jeu.
* Les [exports](exports.md) la décrivent : le rapport nomme son patch, et le DAT de votre collection l'écrit avec ses
  propres sommes de contrôle.

## Annulation

Chaque version patchée est inscrite au journal du tri : **Annuler le dernier tri** la supprime, sauf si elle a été
modifiée depuis le tri.

## En ligne de commande

`--patches <dossier>` donne le dossier des patchs, et `--patch-only` n'écrit que les versions patchées (voir
[Ligne de commande](command-line.md)). Le bilan compte les versions patchées et les patchs non appliqués.

`romsieve-cli patches` simule le tri et liste ses patchs non appliqués, avec les ROMs proposées pour un IPS sans CRC32,
puis les IPS appliqués grâce à leur readme ; `--name-from-notes --yes` écrit le CRC32 dans le nom de ces derniers.
