# Archives de test

Archives réelles utilisées par `ArchiveFormatTests` pour la lecture des 7z et des RAR (SharpCompress).

| Fichier | Origine | Contenu |
|---|---|---|
| `roms_solid.7z` | Créé avec 7-Zip (`7z a -t7z -m0=LZMA2 -mx=9 -ms=on`) | `Alpha (1990)(Ocean).adf`, `Beta (1991)(Ocean).adf`, `Disc (1995)(Sony).cue` et sa piste `Disc (1995)(Sony) (Track 1).bin`, dans un seul bloc solide |
| `roms_nonsolid.7z` | Idem avec `-ms=off` | Les mêmes fichiers, un bloc par fichier |
| `sharpcompress_rar4.rar` | `tests/TestArchives/Archives/Rar.rar` du dépôt SharpCompress (licence MIT) | `тест.txt` (nom cyrillique), `exe/test.exe`, `jpg/test.jpg` (RAR 4) |
| `sharpcompress_rar5.rar` | `tests/TestArchives/Archives/Rar5.rar` du dépôt SharpCompress (licence MIT) | Les mêmes fichiers (RAR 5) |
| `single_game.7z` | Créé avec 7-Zip (`7z a -t7z`) | `Super Game (1995)(Acme).iso` (archive 7z d'un seul jeu) |
| `single_game.rar` | Dépôt `ssokolow/rar-test-files` (licence MIT) | `testfile.txt` (archive RAR d'un seul fichier) |

Le contenu des fichiers des archives 7z est reproduit par `ArchiveFormatTests.Pattern` : un motif ASCII répété (`ALPHA-`, `BETA--`, `TRACK1`) jusqu'à la taille indiquée dans les tests. Aucun outil ne sait créer de RAR hormis WinRAR : les archives RAR sont donc reprises telles quelles.
