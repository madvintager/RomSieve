# Sécurité, purge et annulation

RomSieve manipule des collections de plusieurs téraoctets. Il est conçu pour qu'un mauvais réglage puisse toujours
être rattrapé.

## Ce que RomSieve ne fait jamais

* **Une simulation n'écrit rien**, nulle part.
* **La source n'est jamais modifiée** en modes Copier, Hard link, Symlink et ZIP. En mode Déplacer, seuls les fichiers
  retenus et non conformes la quittent ; les fichiers exclus et les doublons restent, et les archives inspectées ne sont
  jamais déplacées.
* **La destination ne peut être ni la source, ni la contenir**, ni (avec un scan récursif) se trouver à l'intérieur :
  les fichiers triés seraient réanalysés, et une purge viderait la source. RomSieve refuse ces dossiers, y compris
  quand un lien symbolique ou une jonction mène de l'un à l'autre : les chemins sont comparés tels qu'écrits, puis avec
  leurs liens résolus. Les lecteurs réseau mappés et les montages liés (*bind mounts*) ne sont pas résolus.
* **Rien n'est écrit hors de la destination.** Un nom de fichier lu dans une archive ou un DAT qui est un chemin absolu
  (`C:/…`, `/home/…`), contient un segment `..` ou un caractère nul pourrait écrire n'importe où sur le disque, ce que
  font exprès certaines archives trouvées sur Internet. Un tel fichier est en erreur dès la simulation, avec la raison
  « Non écrit : le nom … est dangereux ou invalide », et rien n'est écrit pour lui ni pour les autres fichiers de son
  jeu ; les autres fichiers sont triés comme d'habitude. Chaque cible est encore vérifiée juste avant d'être écrite.
* **Une purge ne supprime rien** (voir ci-dessous).
* **Un tri interrompu ne laisse aucun fichier partiel** : **Arrêter** (le bouton **Simuler (Dry-Run)** pendant une
  exécution) supprime le fichier en cours d'écriture, même au milieu d'une grosse copie. Un fichier s'écrit sous un nom
  temporaire (`<nom>.<id>.romsieve-tmp`) et ne prend son nom qu'une fois complet : un plantage ou une coupure de courant
  ne laisse au plus que ce fichier temporaire, que l'analyse ignore et qu'un tri suivant supprime en écrivant le même
  fichier (une heure au moins après). Fermer RomSieve pendant un tri demande une confirmation, arrête le tri et ne
  ferme qu'une fois le tri arrêté ; pendant une annulation, RomSieve se ferme à sa fin.
* **Un transfert en échec ne laisse pas non plus de fichier partiel** : quand un partage réseau disparaît ou qu'un
  fichier est verrouillé, le fichier en cours d'écriture est supprimé, le fichier passe en erreur et le journal donne le
  message, puis le tri continue avec les suivants. Un jeu dont tous les fichiers n'ont pas pu être écrits passe en
  erreur tout entier. En mode Déplacer, un fichier reste dans la source tant que sa copie n'est pas complète et écrite
  sur le disque lui-même, au-delà des caches du système (une coupure de courant ou un disque débranché juste après le
  tri ne perd rien) ; l'annulation remet un fichier déplacé en place de la même façon. La copie n'est jamais supprimée
  une fois la source partie. **Annuler le dernier tri** retire ce que le tri a écrit.
* **Un tri s'arrête quand continuer ferait du tort**, et dit pourquoi dans un message : quand la destination est pleine
  (tous les fichiers suivants échoueraient aussi), et quand le journal des opérations ne peut pas être écrit (la suite
  ne pourrait pas être annulée ; un tri ne commence jamais sans son journal). Les fichiers non transférés restent dans
  la source, en erreur avec la raison « Non trié : le tri s'est arrêté avant ce fichier ».
* **L'annulation n'écrase jamais** un fichier à son emplacement d'origine, et **ne supprime jamais un fichier modifié**
  depuis le tri.

## Purger une destination non vide

Si la destination contient déjà des fichiers et que *Compléter une destination non vide* n'est pas coché, RomSieve
propose de la **purger** avant le tri :

* le contenu précédent est **déplacé** dans `_purged/<date>/` à l'intérieur de la destination. Sur un même disque,
  c'est instantané et ne prend aucune place supplémentaire ;
* rien n'est supprimé : **Annuler le dernier tri** remet ce contenu en place ;
* pour libérer l'espace disque, videz la quarantaine une fois le résultat vérifié (voir ci-dessous).

Le dossier `_purged` d'une purge précédente n'est jamais touché par la suivante : chaque purge a son propre dossier daté.

Les dossiers et fichiers du système (`System Volume Information`, `$RECYCLE.BIN`, `lost+found`, `.Trash-1000`,
`.DS_Store`...) ne comptent pas : la racine d'une clé USB ou d'une carte mémoire qui ne contient qu'eux est une
destination vide, et une purge ne les déplace jamais.

### Vider la quarantaine

La quarantaine grossit à chaque purge. Dans l'étape *Sortie*, le cadre *Destination* indique ce qu'elle contient
(*Quarantaine des purges (_purged) : 3 purge(s), 1 245 fichier(s), 12,40 Go.*) et propose **Vider la quarantaine…**.
La ligne est là dès qu'une destination est choisie : tant que rien n'a été mis de côté, elle indique *Quarantaine des
purges (_purged) : vide.* et son bouton reste grisé. Après confirmation, son contenu est **supprimé définitivement** : c'est la seule suppression de RomSieve.

* L'annulation des tris qui ont purgé ce contenu ne peut plus le remettre en place : elle défait le reste et signale les
  éléments disparus.
* Les fichiers triés ne sont jamais touchés, et un lien rangé dans la quarantaine est supprimé sans toucher à sa cible.
* Seule la quarantaine d'une destination marquée par RomSieve est affichée et vidée : le dossier `_purged` d'un
  dossier sans marqueur est un dossier ordinaire.

En [ligne de commande](command-line.md) : `romsieve-cli quarantine` l'affiche, `romsieve-cli quarantine --empty --yes`
la vide.

### Le marqueur `.romsieve`

Chaque tri réel dépose un fichier caché nommé `.romsieve` à la racine de sa destination. Si vous demandez à purger un
dossier **sans** ce marqueur (par exemple un dossier choisi par erreur, ou un dossier trié par un autre outil), une seconde confirmation, *Dossier non créé par RomSieve*, vérifie que vous avez choisi le
bon dossier. En [ligne de commande](command-line.md), une telle purge exige aussi `--force-foreign-purge`.

Une destination qui ne contient que le marqueur et `_purged` est considérée comme vide. Quand une ancienne destination
sert ensuite de source, son dossier `_purged` est ignoré.

## Annuler un tri

Chaque tri réel écrit un **journal** des opérations effectuées (voir
[l'emplacement des fichiers de RomSieve](installation.md#emplacement-des-fichiers-de-romsieve)). **Annuler le
dernier tri** lit le journal du dernier tri pas encore annulé, affiche ce qui va être défait (date, nombre d'opérations,
mode, dossiers) et, après votre confirmation, le défait :

| Opération du tri | Annulation |
|---|---|
| Déplacement | Le fichier retrouve son emplacement d'origine |
| Copie, extraction, archive ZIP, playlist, marqueur `.romsieve` | Le fichier est supprimé, s'il n'a pas changé |
| Hard link, lien symbolique | Le lien est supprimé (la source garde les données) |
| Descripteur réécrit (`.cue`...) | La copie réécrite est supprimée ; en mode Déplacer, le descripteur d'origine est restauré |
| Purge | Le contenu mis de côté dans `_purged` retrouve sa place |

Les dossiers laissés vides sont supprimés. Cliquer à nouveau sur **Annuler le dernier tri** défait le tri précédent, et
ainsi de suite.

Certaines opérations ne peuvent pas être défaites ; elles sont listées dans le journal des opérations et dans le
bilan final :

* la cible a été **modifiée** depuis le tri : elle est conservée ;
* un fichier **occupe déjà** l'emplacement d'origine : rien n'est écrasé ;
* un fichier **écrasé** pendant le tri (conflits en *Écraser*) ne peut pas être restauré ;
* la cible n'existe plus.

Une opération empêchée par un emplacement d'origine occupé ou par une erreur de lecture ou d'écriture (un fichier
ouvert par un autre programme) **peut être reprise** : le bilan le dit, et le tri reste proposé. Fermez le fichier ou
libérez son emplacement, puis cliquez de nouveau sur **Annuler le dernier tri** : seules les opérations restantes sont
rejouées. Les autres cas sont définitifs.

> L'annulation s'appuie sur les journaux des tris. Ne supprimez pas le dossier `journals` si vous pourriez vouloir
> annuler un tri. RomSieve garde les journaux des 50 derniers tris réels : un tri plus ancien ne peut plus être annulé.
