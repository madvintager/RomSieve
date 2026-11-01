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

# Compléter une destination non vide, ou la purger d'abord (son contenu est mis de côté dans _purged/)
romsieve-cli run --execute --append
romsieve-cli run --execute --purge --yes

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
```

`quarantine` lit la destination des réglages ou du profil (`--profile`), ou celle de `--destination`.

## Options de `dat`

| Option | Rôle |
|---|---|
| `-o`, `--output <fichier>` | Fichier DAT à écrire (obligatoire) : `.xml` pour Logiqx XML, `.dat` pour ClrMamePro. Avec des [listes de logiciels MAME](naming-conventions-and-dats.md#listes-de-logiciels-mame), le **dossier** à la racine duquel s'écrit un DAT par liste (`megadriv.xml`...), comme des listes de logiciels MAME quand elles ont été lues dans les fichiers `hash` de MAME, sinon en Logiqx XML ; un fichier `.xml` ou `.dat` est alors refusé (code 2), et les DAT du dossier que l'export n'a pas remplacés sont nommés |
| `-p`, `--profile <nom>` | Utilise un profil enregistré au lieu des réglages de l'interface |
| `--dat <fichier>` | Fichier DAT du catalogue, ou dossier de DAT de listes de logiciels MAME |
| `--categories <fichier>` | Fichier de catégories des machines MAME |
| `--ratings <fichier>` | Fichier de notes des machines MAME |

`dat` ne lit ni la source ni la destination : voir [Exports](exports.md) pour ce que contient le DAT filtré. Quand les
réglages ne gardent aucun jeu, aucun fichier n'est écrit et le code de sortie est 4.

## Options de `run`

| Option | Rôle |
|---|---|
| `--execute` | Trie réellement. Sans elle, rien n'est écrit. |
| `-p`, `--profile <nom>` | Utilise un profil enregistré au lieu des réglages de l'interface |
| `-s`, `--source <dossier>` | Dossier source |
| `-d`, `--destination <dossier>` | Dossier de destination |
| `--dat <fichier>` | Fichier DAT, ou dossier de DAT de listes de logiciels MAME |
| `--chd <dossier>` | Dossier des CHD d'une collection MAME, s'ils ne sont pas dans la source |
| `--categories <fichier>` | Fichier de catégories des machines MAME (`catver.ini`, `catlist.ini`) |
| `--ratings <fichier>` | Fichier de notes des machines MAME (`bestgames.ini`) |
| `--mode <mode>` | `copy`, `move`, `hardlink`, `symlink` ou `zip` |
| `--append` | Complète une destination non vide |
| `--purge` | Purge d'abord une destination non vide (exige `--execute` et `--yes`) |
| `-y`, `--yes` | Confirme une purge ou une annulation |
| `--force-foreign-purge` | Autorise la purge d'un dossier sans le marqueur `.romsieve` |
| `--report <fichier>` | Exporte le rapport (`.json` ou `.csv`) |
| `--miss-list <fichier>` | Exporte les ROMs manquantes (`.txt` ou `.csv`, exige un DAT) |
| `--fixdat <fichier>` | Exporte le fixdat des ROMs manquantes (exige un DAT) ; avec des listes de logiciels MAME, le dossier où s'écrit un fixdat par liste (`fixDat_megadriv.xml`...) |

Options communes à toutes les commandes :

| Option | Rôle |
|---|---|
| `--config <fichier>` | Un autre fichier de réglages. Ses profils, ses journaux, ses rapports d'erreur et son cache des sommes de contrôle sont ceux situés à côté de lui. |
| `--lang <fr\|en\|es>` | Langue des messages (par défaut, celle de l'interface, sinon l'anglais) |
| `-q`, `--quiet` | N'affiche que les avertissements, les erreurs et le bilan |

Tous les autres réglages (filtres, 1G1R, modèle, conflits...) viennent du fichier de réglages ou du profil : préparez-les
dans l'interface, puis enregistrez-les comme profil.

## Garde-fous

* Sans `--execute`, rien n'est écrit.
* La source doit exister, le DAT doit être trouvé, et la destination ne doit pas chevaucher la source.
* Un tri réel dans une destination non vide exige `--append`, ou `--purge --yes` ; un dossier sans le marqueur
  `.romsieve` exige en plus `--force-foreign-purge` pour être purgé. **Compléter une destination non vide**, coché
  dans la configuration ou le profil, ne dispense pas de `--append`, comme l'interface demande encore confirmation ;
  et `--purge --yes` purge la destination même quand cette option est cochée.
* `undo` se contente d'afficher le tri à annuler tant que `--yes` n'est pas ajouté. Après une annulation incomplète
  (fichier verrouillé, emplacement d'origine occupé), `undo --yes` reprend les seules opérations restantes.
* `quarantine --empty` exige `--yes`, et ne touche qu'à la quarantaine d'une destination marquée par RomSieve : le
  contenu supprimé ne pourra plus être remis en place par l'annulation des tris qui l'ont purgé.
* **Ctrl+C** arrête l'exécution proprement : le fichier en cours d'écriture est supprimé.

## Codes de sortie

| Code | Signification |
|---|---|
| 0 | Succès |
| 1 | Erreur inattendue (un rapport d'erreur est enregistré dans `crashes/` à côté des réglages, et son chemin affiché) |
| 2 | Arguments invalides (option inconnue, valeur invalide, options incompatibles, format d'export non pris en charge, `dat --output` sur le DAT lu) |
| 3 | Configuration invalide (fichier de réglages ou profil absent ou illisible, source, DAT ou destination introuvable, dossiers qui se chevauchent) |
| 4 | Terminé, mais des fichiers sont en erreur (dont un tri arrêté par une destination pleine ou un journal impossible à écrire), un export a échoué (ou `dat` n'a gardé aucun jeu), des opérations n'ont pas pu être annulées ou des éléments de la quarantaine n'ont pas pu être supprimés |
| 5 | Interrompu (Ctrl+C) |
| 6 | Confirmation requise (`--yes`, `--append` / `--purge --yes`, `--force-foreign-purge`, ou `quarantine --empty` sans `--yes`) |
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
