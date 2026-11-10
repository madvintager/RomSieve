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
| **IPS** | `.ips` | le CRC32 écrit dans le nom du patch (voir [Rapprochement](#rapprochement)) | aucun : le format n'enregistre aucune somme de contrôle |
| **IPS32** | `.ips` ou `.ips32` | le CRC32 écrit dans le nom du patch | aucun |
| **UPS** | `.ups` | le CRC32 et la taille qu'il enregistre | CRC32 du patch, de la ROM d'origine et du résultat |
| **BPS** | `.bps` | le CRC32 et la taille qu'il enregistre | CRC32 du patch, de la ROM d'origine et du résultat |

Le format est lu dans le contenu du fichier, pas seulement dans son extension. UPS et BPS sont les plus sûrs : ils ne
s'appliquent qu'à la bonne ROM et vérifient leur résultat. Un patch abîmé ou tronqué est refusé avant d'être appliqué.

Les autres formats (xdelta, PPF, APS, BSDiff…) ne sont pas pris en charge : leurs fichiers sont ignorés. Les sets
d'arcade MAME et les logiciels des listes MAME ne sont jamais patchés.

## Dossier des patchs

Dans l'étape **Collection**, le cadre **Patchs** a le champ **Dossier des patchs**, avec son bouton **Parcourir...**. Il
est toujours affiché, quelle que soit la convention.

* Le dossier est lu avec ses **sous-dossiers**. Seuls les fichiers `.ips`, `.ips32`, `.ups` et `.bps` sont lus.
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
  règle d'igir. Un IPS sans CRC32 dans son nom n'est pas appliqué.
* Une ROM avec un **en-tête copieur** (iNES pour la NES, en-tête de 512 octets pour la Super Nintendo, en-tête Lynx)
  est cherchée sous ses deux formes, avec et sans en-tête : un patch fait pour l'une ou pour l'autre trouve sa ROM. Le
  patch s'applique à la forme qu'il attend, et le résultat est écrit tel que le patch le produit.
* Si plusieurs fichiers de la source ont le même CRC32, le patch s'applique à celui que le tri écrit, sinon au premier
  dans l'ordre des chemins.
* Plusieurs patchs pour une même ROM donnent chacun leur version patchée.

## La version patchée

* **Nom** : celui du patch, avec l'extension de la ROM d'origine. `Jeu (Japan) (T-En).bps` appliqué à
  `Jeu (Japan).sfc` donne `Jeu (Japan) (T-En).sfc`. Pour un IPS, le CRC32 est retiré du nom, avec ses crochets ou ses
  parenthèses.
* **Dossier** : celui que la ROM d'origine aurait dans la destination, même quand le 1G1R l'écarte.
* **Jeu** : la version patchée appartient au jeu de sa ROM d'origine. Elle reste en dehors du 1G1R, des multi-disques
  et des filtres : elle n'est jamais écartée à la place de son origine, ni ne l'écarte.
* **Conflits** : une collision avec un fichier déjà présent dans la destination suit le mode de conflit choisi (voir
  [Conflits](destination-and-transfers.md#conflits)). Si la version patchée porte le nom de sa ROM d'origine (un patch
  nommé comme sa ROM, comme le demandent les émulateurs qui appliquent un patch au lancement), la ROM d'origine garde
  son nom, et la version patchée prend le suffixe `_01` quel que soit le mode : les deux sont écrites.

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
| Le nom d'un patch IPS doit donner le CRC32 de la ROM d'origine | Ajoutez le CRC32 au nom du patch, par exemple `[1a2b3c4d]` |
| Patch illisible | Le fichier est tronqué, abîmé, ou d'un autre format |
| Le nom du patch donne les CRC32 de plusieurs ROMs de la source | Ne gardez dans le nom que le CRC32 de la bonne ROM |
| La ROM d'origine fait partie d'un jeu en plusieurs fichiers | Un `.cue` et ses pistes, ou un jeu en plusieurs disques : ces jeux ne sont pas patchés |
| La ROM n'a pas la taille que le patch attend | Ce n'est pas la ROM pour laquelle le patch a été fait |

Sans DAT, les patchs ne sont pas lus : le journal le dit.

## Dans les résultats

* Le tableau marque chaque version patchée d'une pastille **Patchée**. Dans la vue groupée, elle suit la version retenue
  de son jeu. Le panneau de détail nomme le patch, son format et la ROM d'origine ; celui d'une ROM d'origine nomme ses
  versions patchées. Une version patchée ne peut pas être choisie comme gagnant 1G1R.
* Le bandeau compte les versions patchées dans la pastille **Patchées**, qui n'affiche qu'elles, et les patchs non
  appliqués dans « *n* patchs non appliqués », dont l'infobulle donne la liste avec leurs raisons.
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
