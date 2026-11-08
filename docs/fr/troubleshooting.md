# Dépannage

### Beaucoup de fichiers sont « Non conformes »

* **Sans DAT**, RomSieve se fie aux noms : un fichier TOSEC doit au moins ressembler à `Titre (date)(éditeur).ext`.
  Vérifiez que la **Convention** correspond à vos fichiers (le badge affiche la convention effective), ou
  imposez-la.
* **Avec un DAT**, un fichier non conforme est absent du DAT : mauvais DAT (autre système, autre version), fichier
  modifié, ou fichier dans une archive dont le contenu n'a pas de CRC32 enregistré (certaines archives 7z et RAR).
* Les fichiers non conformes ne sont pas perdus : ils sont transférés dans `_unmatched/` dans la destination, ou
  laissés dans la source quand **Isoler les fichiers non conformes dans _unmatched** (étape *Sortie*) est décochée.

### Un filtre semble sans effet

Les réglages TOSEC de la page **Filtres** ne s'appliquent qu'aux noms TOSEC, et les réglages *No-Intro / Redump* qu'aux
noms No-Intro et Redump. Vérifiez **Réglages pour :** et le badge de convention. En 1G1R, certaines options (média préféré, versions modifiées) ne servent qu'à
départager les versions d'un même jeu.

Avec un DAT, les filtres ne voient que les fichiers que le DAT identifie : un fichier absent du DAT reste non conforme,
même avec un tag que vous excluez, quand **Filtrer aussi les fichiers absents du DAT** est décochée (voir
[Fichiers absents du DAT](filters.md#fichiers-absents-du-dat)). C'est le cas d'une configuration enregistrée par une
version précédente de RomSieve.

### Le 1G1R n'a pas gardé la version que je voulais

L'info-bulle de la version exclue, ou le panneau **Détail de la ligne sélectionnée**, indique le critère qui a décidé
(par exemple *langue FR préférée à EN*).
Changez l'ordre de vos langues ou de vos pays, ou la [cascade des critères](languages-regions-1g1r.md#modifier-la-cascade),
ou faites un clic droit sur la version et choisissez **Retenir cette version comme gagnante 1G1R**.

### Deux jeux différents ont été regroupés, ou un jeu a été séparé

Le 1G1R regroupe les versions par titre et par système (et par parent / clone avec un DAT). Utilisez **Isoler toutes
les versions de « … »** dans le menu contextuel pour voir le groupe, et un DAT si possible : c'est lui qui donne le
regroupement le plus fiable.

### « La destination est située à l'intérieur du répertoire source »

La destination ne peut être ni la source, ni un dossier parent de celle-ci, ni (avec un scan récursif) un dossier situé
à l'intérieur. Choisissez une destination hors de la source, ou décochez *Scanner récursivement les sous-dossiers*.

### La première analyse est lente

La première analyse lit chaque fichier pour calculer sa somme de contrôle ; sur un disque dur mécanique, c'est à peu
près la vitesse d'une copie. Les sommes de contrôle sont ensuite mises en cache, et les analyses suivantes de fichiers
inchangés sont bien plus rapides. Sur un SSD ou un NAS, réglez **Calcul des CRC** (fenêtre des
**Préférences…**) sur plusieurs fichiers à la fois.

### Les hard links échouent

Les liens physiques ne fonctionnent que sur un même disque (partition), et pas en FAT32 / exFAT. Choisissez **Copier**
ou **Symlink**.

### Les liens symboliques échouent sous Windows

Créer des liens symboliques peut exiger le mode développeur (*Paramètres > Système > Espace développeurs*) ou les droits
d'administrateur.

### Windows affiche « Windows a protégé votre ordinateur »

Les exécutables ne sont pas signés. Cliquez sur **Informations complémentaires**, puis sur **Exécuter quand même**.

### Où sont passés mes fichiers après une purge ?

Dans `_purged/<date>/` à l'intérieur de la destination. **Annuler le dernier tri** les remet en place ; sinon
déplacez-les vous-même, ou supprimez le dossier pour libérer l'espace. Voir
[Sécurité, purge et annulation](safety-and-undo.md).

### L'annulation signale des opérations « non annulées »

Un fichier modifié depuis le tri est conservé, un emplacement d'origine déjà occupé n'est jamais écrasé, et un fichier
écrasé pendant le tri ne peut pas être restauré. Le journal des opérations liste chaque cas avec son chemin. Quand le
bilan dit que des opérations peuvent être reprises (un fichier ouvert par un autre programme, un emplacement d'origine
occupé), fermez le fichier ou libérez l'emplacement, puis cliquez de nouveau sur **Annuler le dernier tri** : seules ces
opérations sont rejouées (voir [Sécurité, purge et annulation](safety-and-undo.md)).

### « Tri arrêté »

Le tri s'est arrêté avant la fin, et le message dit pourquoi. **La destination est pleine** (la simulation et la
confirmation du tri le signalent à l'avance : « Place insuffisante ») : libérez de la place, puis relancez le tri avec
*Compléter une destination non vide* coché et *Conflits* sur **Ignorer** : les fichiers déjà écrits restent tels quels.
**Le journal des opérations ne peut pas être écrit** : vérifiez le dossier des réglages (voir
[Installation](installation.md)), sa place libre et ses droits d'écriture. Quand le journal a échoué en cours de tri, le
message nomme le dernier fichier écrit, que le journal ne contient pas : **Annuler le dernier tri** ne le retirera pas.

Un jeu en erreur avec la raison « Jeu incomplet dans la destination » n'a pas l'un de ses fichiers (une piste d'un
`.cue`, par exemple) : ses autres fichiers ont été écrits. **Annuler le dernier tri** les retire, ou relancez le tri une
fois la cause corrigée.

### « Dossiers illisibles ignorés » ou « Liens vers un dossier non suivis »

RomSieve n'a pas pu lire certains dossiers de la source (droits d'un autre utilisateur, dossier du système), ou y a
trouvé des liens vers des dossiers (liens symboliques, jonctions) : il a continué avec le reste, et les fichiers de ces
dossiers ne sont pas triés. Donnez-vous le droit de lire le dossier, ou choisissez comme source le dossier vers lequel
mène le lien. Les dossiers et fichiers du système (corbeilles, `System Volume Information`, `lost+found`, `Thumbs.db`,
`.DS_Store`...) sont ignorés sans un mot : ils n'appartiennent jamais à une collection.

### « Erreur inattendue »

RomSieve a rencontré une erreur qu'il ne prévoyait pas. Il enregistre un rapport d'erreur et vous dit où ; la
fenêtre reste ouverte, mais il est préférable de redémarrer RomSieve avant un tri réel. La ligne de commande affiche
le même chemin et se termine avec le code 1. Les rapports sont dans le dossier `crashes` situé à côté des réglages
(voir [Installation](installation.md)) ; joignez le plus récent à votre signalement. Si RomSieve se ferme tout seul,
un rapport est aussi enregistré.

### Signaler un problème

Signalez-le sur la [page des tickets](https://github.com/madvintager/RomSieve/issues/new/choose) de RomSieve (en
anglais de préférence) : un formulaire demande ce qu'il faut. Notez la version (barre de titre, ou
`romsieve-cli --version`) et les étapes qui mènent au problème, et joignez le rapport exporté après une simulation (**Exporter** > **Rapport (JSON, CSV)...**), ou le journal complet de l'exécution
concernée : son chemin est donné à la fin du journal (dossier `logs`, voir
[Installation](installation.md#emplacement-des-fichiers-de-romsieve)). Après une erreur inattendue ou une fermeture brutale, joignez aussi le dernier rapport d'erreur du
dossier `crashes` : il donne la version, le système et le détail technique de l'erreur, ainsi que les chemins des
fichiers concernés, le cas échéant.
