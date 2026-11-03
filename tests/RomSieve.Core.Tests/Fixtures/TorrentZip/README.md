# Archives TorrentZip de référence

Fichiers d'entrée et archives TorrentZip de référence lus par `TorrentZipWriterTests` et `ZipOutputTests`. L'écrivain de
RomSieve (`TorrentZipWriter`) doit reproduire chaque archive **à l'octet près**.

| Jeu (dossier de `input/`, archive de `reference/`) | Ce qu'il vérifie |
|---|---|
| `Compressible (1990)(Test)` | Texte compressible de 48 Kio, sur plusieurs blocs Deflate |
| `Incompressible (1990)(Test)` | Données pseudo-aléatoires de 16 Kio (blocs stockés dans le flux Deflate) |
| `Literals And Copies (1990)(Test)` | Littéraux mêlés de copies : seul cas où le zlib de SharpCompress, qui garde l'heuristique `TRUNCATE_BLOCK` de DotNetZip, ne donne pas la sortie de zlib |
| `Empty (1990)(Test)` | Fichier vide (`03 00` dans l'archive) |
| `Mixed Case (1990)(Test)` | Six entrées de casses et de signes mêlés (`_`, `[`) : l'ordre de TorrentZip |
| `Non ASCII (1990)(Test)` | Un nom codable en CP437 (`Café Crème (Démo).bin`, sans drapeau UTF-8) et un nom qui ne l'est pas (`ゲーム (Japan).bin`, en UTF-8) |
| `Disc (1995)(Sony)` | Un `.cue` (fins de ligne CRLF) et ses deux pistes, dans une seule archive |

Les fichiers de `input/` et de `reference/` sont marqués `binary` dans `.gitattributes` : une conversion des fins de
ligne changerait leur contenu, et les archives ne correspondraient plus.

## Régénération

Les archives ont été produites par **igir 5.8.0** (`--zip-format torrentzip`, compression par zlib 1.1.3), puis
recoupées avec **TrrntZipCMD** de RomVault (dépôt `RomVault/RVWorld`, `SAM/TrrntZipCMD`) : les deux outils donnent les
mêmes octets.

1. Produire les entrées et leur DAT (Node.js ; le générateur est déterministe, il redonne les mêmes fichiers) :
   `node generate.mjs <dossier>` écrit `<dossier>/input/` et `<dossier>/torrentzip.dat`.
2. Écrire les archives avec igir (Node.js 22.22 ou plus, 24.15 ou plus) :
   `npx igir@5.8.0 copy zip --dat <dossier>/torrentzip.dat --input <dossier>/input --output <dossier>/reference --zip-format torrentzip`
3. Recouper avec RomVault : compresser chaque dossier de `input/` en un ZIP ordinaire du même nom dans un autre
   dossier, lancer `dotnet TrrntZipCMD.dll <cet autre dossier>` (sans option : il réécrit chaque archive en TorrentZip ;
   `-f` est le mode réparation, qui ne réécrit pas une archive sans structure), puis comparer les empreintes des
   archives à celles de `reference/`.
4. Remplacer `input/` et `reference/` par les nouveaux dossiers.
