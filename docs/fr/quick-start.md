# Démarrage rapide

Cette page vous accompagne pour un premier tri, d'un dossier en vrac à une arborescence propre. Comptez quelques
minutes ; rien n'est écrit sur le disque avant la dernière étape.

La colonne de gauche de la fenêtre suit les étapes de cette page : **Collection**, **Sélection** (**Filtres**, puis
**Langues et 1G1R**), **Sortie**, puis **Résultats**. Les boutons du bas (**Simuler (Dry-Run)**, **Démarrer le tri**...)
restent accessibles depuis chacune.

## 1. Choisir les dossiers

* Dans l'étape **Collection**, cadre **Source** : le dossier qui contient vos fichiers non triés (ROMs, images disques,
  archives ZIP / 7z / RAR...). Laissez *Scanner récursivement les sous-dossiers* coché pour inclure ses sous-dossiers.
  Les dossiers et fichiers du système (corbeilles, `System Volume Information`, `Thumbs.db`...) sont ignorés, comme les
  dossiers illisibles et les liens vers un dossier, que le journal signale.
* Dans l'étape **Sortie**, cadre **Destination** : le dossier où l'arborescence triée sera construite. Préférez un dossier
  **vide** pour un premier essai.

> La destination ne peut être ni la source elle-même, ni un dossier parent de la source, ni (avec un scan récursif) un
> dossier situé dans la source. RomSieve refuse ces combinaisons pour protéger vos fichiers.

## 2. Choisir éventuellement un fichier DAT

Un fichier DAT est le catalogue officiel d'une collection (TOSEC, No-Intro ou Redump), avec le nom et les sommes de
contrôle de chaque ROM. Avec un DAT, RomSieve identifie vos fichiers par leur **somme de contrôle**, quel que soit
leur nom, et peut vous dire quelles ROMs **manquent**. Sans DAT, il se fie aux seuls **noms de fichiers**.

Dans l'étape **Collection**, cadre **Catalogue DAT**, cliquez sur **Parcourir...** à côté de *Fichier*. Un résumé
apparaît sous le champ (nom, version, nombre de ROMs), et le badge à côté de **Convention**, dans le même cadre,
affiche la convention détectée.

Voir [Conventions de nommage et fichiers DAT](naming-conventions-and-dats.md) pour le détail.

## 3. Choisir comment transférer les fichiers

Dans l'étape **Sortie** :

* **Mode** : **Copier** (recommandé pour un premier essai : la source n'est pas touchée), **Déplacer**, **Hard link**,
  **Symlink** ou **ZIP** (une archive par jeu).
* **Conflits** : que faire si un fichier cible existe déjà : renommer avec un suffixe `_01`, ignorer, ou écraser.
* **Modèle** : l'arborescence à construire, par exemple `{publisher}/{year}/{title}/` (défaut TOSEC) ou
  `{country}/{title}/` (menu *Préréglages*, *Pays / titre (No-Intro, Redump)*). L'aperçu, en bas du cadre
  *Arborescence*, montre où iraient quelques fichiers de la source.

Voir [Destination et transferts](destination-and-transfers.md).

## 4. Ajuster les filtres (facultatif)

* La page **Filtres** de l'étape *Sélection* exclut les mauvais dumps, les hacks, les démos, les prototypes..., avec des réglages pour TOSEC
  et pour No-Intro / Redump. Les réglages par défaut sont raisonnables : les mauvais dumps, les over/underdumps, les
  images infectées et les versions modifiées sont exclus.
* La page **Langues et 1G1R** ne retient que vos langues ou vos pays, et avec le **1G1R** une seule version par
  jeu. Les profils rapides (France, Europe, USA, Japon) sont un bon point de départ.

Voir [Filtres](filters.md) et [Langues, régions et 1G1R](languages-regions-1g1r.md).

## 5. Simuler

Cliquez sur **Simuler (Dry-Run)**. La fenêtre passe à l'étape **Résultats** : RomSieve analyse la source et remplit le
tableau **Aperçu de la simulation**, une ligne par fichier : son titre, son statut et le chemin où il **serait** écrit. Rien n'est écrit sur le disque.

![Bandeau des statistiques](../images/fr/stats_banner.png)

Le bandeau au-dessus du tableau compte les fichiers par statut ; cliquez sur une pastille pour n'afficher que ces
lignes :

| Statut | Signification |
|---|---|
| **Retenus** | Seront écrits dans la destination. |
| **Exclus** | Écartés par un filtre ou par le 1G1R ; la raison est indiquée. Laissés tels quels dans la source. |
| **Non conformes** | Nom non compris, ou fichier absent du DAT. Iront dans `_unmatched/` dans la destination. |
| **Doublons** | Même fichier trouvé deux fois. Laissés tels quels dans la source. |

Vérifiez quelques lignes, revenez aux réglages (**Modifier les réglages**, ou une étape de la colonne de gauche) pour
modifier un réglage si besoin, et simulez à nouveau : une deuxième simulation de la même
source met en évidence ce qui a changé. Voir [Le tableau de simulation](simulation-table.md).

Une fois la simulation terminée, **Démarrer le tri** passe au premier plan et une phrase résume ce que fera le tri, par
exemple « Copier 11 fichiers + 1 non conforme (14.61 Mo) vers D:\Retro\Amiga\Sorted ». Modifier un réglage masque ce résumé jusqu'à la
prochaine simulation.

## 6. Trier

Cliquez sur **Démarrer le tri**. RomSieve demande une confirmation qui récapitule la source, la destination et le
mode. Si la destination n'est pas vide, RomSieve y ajoute les fichiers lorsque l'option *Compléter une destination non
vide* est cochée ; sinon, il propose de la purger d'abord. Le contenu purgé est mis de côté, pas supprimé : voir [Sécurité, purge et annulation](safety-and-undo.md).
Pour ne trier que quelques jeux, cochez-les ou filtrez le tableau et utilisez **Trier les jeux cochés affichés**, dans le menu de la
flèche à côté de **Démarrer le tri** : voir [Trier une partie de la simulation](simulation-table.md#trier-une-partie-de-la-simulation).

Pendant le tri, une barre de progression, le nombre de fichiers traités et le temps restant s'affichent au-dessus des
boutons, et chaque opération est inscrite dans l'onglet **Journal des opérations** de l'étape **Résultats**, qui
s'affiche au lancement du tri. Le bouton **Simuler (Dry-Run)**
devient **Arrêter** : il arrête le tri proprement, le fichier en cours d'écriture est supprimé et la source n'est jamais
touchée.

## 7. Annuler si besoin

**Annuler le dernier tri** défait le dernier tri réel : les fichiers déplacés retrouvent leur place, les copies, liens
et playlists sont supprimés, et le contenu purgé est remis en place. Un fichier que vous avez modifié depuis le tri
n'est jamais supprimé.

## Et ensuite ?

* [Exportez](exports.md) un rapport, la liste des ROMs manquantes ou un DAT de votre collection.
* Enregistrez vos réglages dans un [profil](settings-and-profiles.md) pour les réutiliser, y compris depuis la
  [ligne de commande](command-line.md).
