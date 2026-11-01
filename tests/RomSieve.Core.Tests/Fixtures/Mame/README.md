# DAT MAME de test

Extraits de vrais DAT de MAME 0.289, lus par `MameDatTests` et `MameDescriptionParserTests`. Chaque machine est copiée telle quelle, sans modification ; seuls l'en-tête (« - mini » ajouté au nom) et le choix des machines diffèrent du DAT d'origine. Les DAT complets ne sont pas versionnés.

| Fichier | Origine | Particularités |
|---|---|---|
| `listxml.dat` | `mame -listxml` (MAME 0.289), avec son DTD interne et sa racine `<mame build="...">` | Seul format qui porte `cloneof`, `romof`, `merge`, les disques, les ROM `nodump`, `<driver status>` et `<device_ref>` |
| `sabretools_nonmerged.dat` | « MAME 0.289 ROMs (non-merged) » de Pleasuredome, produit par SabreTools | Clones présents comme machines, mais sans `cloneof` ni `romof` ; chaque jeu contient aussi les ROM de son BIOS |
| `sabretools_merged.dat` | « MAME 0.289 ROMs (merged) » de Pleasuredome, produit par SabreTools | Les clones n'existent pas comme machines : leurs ROM sont rangées dans le parent sous `clone\rom`, sans description |

Machines de `listxml.dat` :

| Cas | Machines |
|---|---|
| Parent et clones | `sf2` et `sf2ua`, `sf2ug`, `sf2uh` (Rev. G et H), `sf2j`, `sf2b` (bootleg, driver `imperfect`), `sf2en` (conversion, une ROM `nodump`) |
| Titre proche, autre jeu | `sf2ce` |
| BIOS | `neogeo` (`isbios`), et `mslug` qui en dépend par `romof` sans être un clone |
| Sets numérotés | `3stooges` (set 1), `3stoogesa` (set 2) |
| Révisions et statut du driver | `aafb` (rev E), `aafbb` (rev B, `preliminary`), `aafbc` (rev C) |
| Disques CHD | `area51` et son clone `area51t`, chacun son disque ; `timecrs4` (World, `romof` vers le BIOS `sys256`, absent du fichier) et son clone `timecrs4j` (Japan), qui partagent le même disque (`merge` dans le clone) |
| Prototype, ROM `nodump` | `airrace` |
| Machine mécanique et device | `3lilpigs` (`ismechanical`, `device_ref` vers `hd44780`), `hd44780` (`isdevice`, `runnable="no"`, une ROM) |

Machines des DAT SabreTools :

* communes aux deux : `sf2`, `neogeo`, `mslug`, `3lilpigs`, `airrace`, et `3do_try` (ROM `baddump`), `barek2ch` (bootleg), `ndxron10` (hack), `ssonicbr` (prototype, hack), `smf` (date dans la description), `3xtwin` (device) ;
* `sabretools_nonmerged.dat` seul : les clones `sf2ua`, `sf2ug`, `sf2uh`, `sf2j`, `sf2b`, `sf2en`, ainsi que `sf2ce`, `3stooges` et `3stoogesa` ;
* `sabretools_merged.dat` : ses 11 machines, dont `sf2` avec les ROM de ses 37 clones.
