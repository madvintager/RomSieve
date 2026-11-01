# Installation

## Téléchargement

Chaque version propose une archive par plateforme :

| Plateforme | Archive |
|---|---|
| Windows 10/11 (x64) | `RomSieve-<version>-win-x64.zip` |
| Linux (x64) | `RomSieve-<version>-linux-x64.tar.gz` |

Chaque archive contient :

* `RomSieve` (`RomSieve.exe` sous Windows) : l'interface graphique ;
* `romsieve-cli` (`romsieve-cli.exe`) : la [ligne de commande](command-line.md) ;
* `README.md` et `LICENSE` (GPLv3) ;
* `THIRD-PARTY-NOTICES.txt` et le dossier `licenses` : les mentions de droits d'auteur et les licences des composants
  que les deux programmes embarquent (.NET, Avalonia, SkiaSharp, SharpCompress...). **Licences des composants tiers**,
  dans la [fenêtre des préférences](settings-and-profiles.md), ouvre ce fichier ;
* `docs/` : ce guide utilisateur, en anglais et en français, à lire hors ligne dans un navigateur (ouvrir
  `docs/index.html`).

Les deux programmes sont **autonomes** : aucune installation de .NET n'est nécessaire. Chacun tient en un seul fichier
de 75 à 100 Mo environ.

### Vérifier un téléchargement

Chaque archive est accompagnée d'un fichier `.sha256`. Placez-les dans le même dossier, puis :

```bash
sha256sum -c RomSieve-1.2.0-linux-x64.tar.gz.sha256
```

Sous Windows, dans PowerShell, comparez l'empreinte affichée par `Get-FileHash RomSieve-1.2.0-win-x64.zip` avec le
contenu du fichier `.sha256`.

## Premier lancement

### Windows

Décompressez l'archive où vous voulez (par exemple `C:\Programmes\RomSieve`) et lancez `RomSieve.exe`.

Les exécutables ne sont pas signés : au premier lancement, Windows SmartScreen peut afficher *« Windows a protégé votre
ordinateur »*. Cliquez sur **Informations complémentaires**, puis sur **Exécuter quand même**.

### Linux

```bash
tar -xzf RomSieve-1.2.0-linux-x64.tar.gz
cd RomSieve-1.2.0-linux-x64
./RomSieve
```

L'archive conserve le droit d'exécution. S'il a été perdu (copie par un partage Windows, par exemple), rétablissez-le
avec `chmod +x RomSieve romsieve-cli`.

Les deux programmes utilisent quelques bibliothèques du système, présentes sur la plupart des bureaux Linux :

* **ICU** (`libicu`) et **OpenSSL** (`libssl`), pour les deux programmes ;
* pour l'interface seulement, **X11** (`libX11`, `libICE`, `libSM`) et **Fontconfig**. Sous Wayland, l'interface
  passe par XWayland, que GNOME et KDE fournissent d'office.

Si l'une manque, RomSieve ne démarre pas et la nomme dans le terminal (*« Couldn't find a valid ICU package »*, ou
*« Unable to load shared library 'libSM.so.6' »*, par exemple). Pour les installer :

```bash
# Debian, Ubuntu (ICU et OpenSSL y sont toujours installés)
sudo apt install libx11-6 libice6 libsm6 libfontconfig1
# Fedora
sudo dnf install libX11 libICE libSM fontconfig libicu openssl-libs
# Arch
sudo pacman -S libx11 libice libsm fontconfig icu openssl
```

## Emplacement des fichiers de RomSieve

| Fichier | Windows | Linux |
|---|---|---|
| Réglages (`config.json`) | `%APPDATA%\RomSieve\` | `~/.config/RomSieve/` (ou `$XDG_CONFIG_HOME/RomSieve/`) |
| Profils nommés (`profiles/*.json`) | `%APPDATA%\RomSieve\profiles\` | `~/.config/RomSieve/profiles/` |
| Journaux des tris (`journals/`) | `%APPDATA%\RomSieve\journals\` | `~/.config/RomSieve/journals/` |
| Journaux des exécutions (`logs/`) | `%APPDATA%\RomSieve\logs\` | `~/.config/RomSieve/logs/` |
| Rapports d'erreur (`crashes/`) | `%APPDATA%\RomSieve\crashes\` | `~/.config/RomSieve/crashes/` |
| Cache des sommes de contrôle (`crc_cache.json`) | `%LOCALAPPDATA%\RomSieve\` | `~/.cache/RomSieve/` (ou `$XDG_CACHE_HOME/RomSieve/`) |
| Cache des en-têtes (`header_cache.json`) | `%LOCALAPPDATA%\RomSieve\` | `~/.cache/RomSieve/` (ou `$XDG_CACHE_HOME/RomSieve/`) |

* Les **réglages** sont enregistrés à la fermeture de RomSieve et avant chaque tri réel : le lancement suivant
  retrouve vos dossiers, vos filtres et la disposition de la fenêtre.
* Les **journaux des tris** consignent chaque fichier écrit par un tri réel : ce sont eux qui permettent
  l'[annulation](safety-and-undo.md). RomSieve garde ceux des 50 derniers tris réels ; les plus anciens sont supprimés
  au début d'un tri, et leurs tris ne peuvent plus être annulés.
* Les **journaux des exécutions** gardent toutes les lignes du journal de chaque simulation, tri ou annulation lancé
  depuis l'interface, dont le panneau ne montre que les dernières ; la fin de l'exécution donne le chemin de son
  fichier. RomSieve garde les 20 derniers.
* Les **rapports d'erreur** ne sont écrits qu'après une erreur inattendue (voir [Dépannage](troubleshooting.md)).
  Vous pouvez les supprimer à tout moment.
* Le **cache des sommes de contrôle** mémorise le CRC32 de chaque fichier (invalidé si le fichier change) : une
  deuxième analyse de la même collection est bien plus rapide.
* Le **cache des en-têtes** mémorise ce que disent les en-têtes des archives (leurs fichiers, avec leur taille et leur
  CRC32) et des CHD, lui aussi invalidé si le fichier change. Une archive inchangée n'est plus ouverte, ce qui compte
  surtout sur un partage réseau : la lecture des en-têtes d'une collection MAME complète y prend des minutes, et une
  deuxième analyse est plusieurs fois plus rapide.

Pour repartir des réglages par défaut, fermez RomSieve et supprimez `config.json`.

## Mise à jour

Remplacez les exécutables par ceux de la nouvelle archive. Vos réglages, profils, journaux et cache sont conservés :
ils sont rangés hors du dossier du programme.

## Compiler depuis les sources

Voir la section *Building from Source* du [README](../../README.md) (SDK .NET 10 requis).
