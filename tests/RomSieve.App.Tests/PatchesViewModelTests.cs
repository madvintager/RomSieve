using System.IO.Compression;
using RomSieve.App.ViewModels;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using static RomSieve.Core.Tests.Fixtures.PatchTestEncoder;

namespace RomSieve.App.Tests;

/// <summary>
/// The window of the patches (<see cref="PatchesViewModel"/>), on a real simulation: the list of every patch grouped by
/// state, the ROMs proposed and the forms of their data, the search among the identified files, the choices recorded
/// without changing a file (apply to a ROM, set aside, back to the automatic choice) and followed by the next simulation,
/// the ROM written in the name of an IPS for other tools, undo, « Accept all the suggestions » with its summary checked by
/// nothing beforehand, a choice that cannot be saved, the footer, and the command of the banner, which runs a new
/// simulation when the window asks for one.
/// </summary>
public sealed class PatchesViewModelTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("fr");
    private readonly HeadlessDialogService _dialogs = new();
    private readonly List<(LogLevel Level, LocalizedMessage Message)> _log = [];
    private readonly string _patches;
    private SortConfig _config = null!;
    private SortExecutionResult? _result;
    private int _simulations;

    private static readonly byte[] Japan = Data(1, 4096);
    private static readonly byte[] Usa = Data(2, 4096);

    public PatchesViewModelTests()
    {
        _patches = _temp.CreateSubdirectory("patches");
    }

    public void Dispose()
    {
        _temp.Dispose();
        File.Delete(_services.PatchLinks.FilePath);
    }

    private static byte[] Data(int seed, int size)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static byte[] Translate(byte[] data) => [.. data, .. Ascii("English")];

    /// <summary>The Japanese and American releases of a game (the American one kept by the 1G1R), and a DAT of both.</summary>
    private void WriteJapanAndUsa(params (string Name, byte[] Data)[] others)
    {
        _temp.WriteBytes("src/Game (Japan).sfc", Japan);
        _temp.WriteBytes("src/Game (USA).sfc", Usa);
        var roms = new[] { ("Game (Japan)", "Game (Japan).sfc", Japan), ("Game (USA)", "Game (USA).sfc", Usa) }
            .Concat(others.Select(o =>
            {
                _temp.WriteBytes($"src/{o.Name}", o.Data);
                return (Path.GetFileNameWithoutExtension(o.Name), o.Name, o.Data);
            }));
        var games = string.Concat(roms.Select(r =>
            $"""<game name="{r.Item1}"><description>{r.Item1}</description><rom name="{r.Item2}" size="{r.Item3.Length}" crc="{Crc(r.Item3):x8}"/></game>"""));
        var dat = _temp.WriteText("dat/snes.dat", $"""<?xml version="1.0"?><datafile><header><name>Nintendo - SNES</name><homepage>No-Intro</homepage></header>{games}</datafile>""");
        _config = new SortConfig
        {
            SourceDir = _temp.GetFilePath("src"),
            DestinationDir = _temp.CreateSubdirectory("dst"),
            DatFile = dat,
            NamingConvention = NamingConvention.NoIntro,
            PatchDir = _patches,
            Filters = new FilterConfig { OneGameOneRom = true, PriorityLanguages = ["en", "ja"] }
        };
    }

    private async Task SimulateAsync()
    {
        _simulations++;
        _result = await _services.Processor.ExecuteAsync(_config, isDryRun: true);
    }

    /// <summary>The window, on the same choices as the simulations.</summary>
    private PatchesViewModel CreateWindow(Func<bool>? isRunning = null) => new(
        _services.I18n,
        _dialogs,
        new PatchNamingService(_services.Patches),
        _services.PatchLinks,
        () => _result?.Items ?? [],
        () => _result?.Stats,
        () => _config,
        isRunning ?? (() => false),
        SimulateAsync,
        (level, message) => _log.Add((level, message)));

    private static PatchRowViewModel Row(PatchesViewModel window, string name) => window.Rows.Single(r => r.Name == name);

    private async Task<PatchesViewModel> LoadAsync()
    {
        await SimulateAsync();
        var window = CreateWindow();
        window.Load();
        await Task.WhenAll(window.Rows.Select(r => r.FormsLoading));
        return window;
    }

    /// <summary>The list as titles of groups and names of patches, in its order.</summary>
    private static IEnumerable<string> ListOf(PatchesViewModel window) =>
        window.ListItems.Select(i => i is PatchGroupViewModel g ? g.Title : ((PatchRowViewModel)i).Name);

    private PatchLink? LinkOf(byte[] patch) => _services.PatchLinks.Load().Links.GetValueOrDefault(PatchKey.Of(patch));

    private async Task ChooseAsync(byte[] patch, PatchLink link) =>
        (await _services.PatchLinks.UpdateAsync([new(PatchKey.Of(patch), link)], (_, _) => { })).Should().BeTrue();

    private PatchedVersion PatchedVersion() => _result!.Items.Single(it => it.Patch is not null).Patch!;

    // =========================================================================
    // The list
    // =========================================================================

    [Fact]
    public async Task TheList_ShouldGroupEveryPatchByState()
    {
        WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game (Japan) (T-Fr).ips", IpsDiff(Japan, Translate(Japan)));
        _temp.WriteBytes("patches/Other.bps", BpsLinear(Data(9, 64), Data(10, 64)));
        _temp.WriteBytes("patches/Game (Japan) (T-En).bps", BpsLinear(Japan, Translate(Japan)));
        _temp.WriteBytes("patches/hacks/Hack.ips", IpsDiff(Usa, Translate(Usa)));
        _temp.WriteText("patches/hacks/Hack.txt", $"ROM CRC32: {Crc(Usa):X8}");
        var named = $"Game (T-En) [{Crc(Usa):x8}].ips";
        _temp.WriteBytes($"patches/{named}", IpsDiff(Usa, Translate(Usa)));
        var aside = BpsLinear(Usa, [.. Usa, .. Ascii("Aside")]);
        _temp.WriteBytes("patches/Aside.bps", aside);
        await ChooseAsync(aside, PatchLink.Ignore("Aside.bps"));

        var window = await LoadAsync();

        ListOf(window).Should().Equal(
            "À associer (1)", "Game (Japan) (T-Fr).ips",
            "Appliqués (3)", "Game (Japan) (T-En).bps", named, "Hack.ips",
            "Écartés (1)", "Aside.bps",
            "Non réparables (1)", "Other.bps");
        window.SelectedRow.Should().BeSameAs(Row(window, "Game (Japan) (T-Fr).ips"), "the first patch of the list is selected");
        window.SelectedListItem.Should().BeSameAs(window.SelectedRow);

        var ips = Row(window, "Game (Japan) (T-Fr).ips");
        ips.State.Should().Be(PatchRowState.ToMatch);
        ips.StateMark.Should().Be("●");
        ips.StatusText.Should().Be("Ce patch ne s'applique à aucune ROM : ni son nom ni son readme ne disent pour quelle ROM il a été fait.");
        ips.ListSummary.Should().BeEmpty();
        ips.Origins.Select(o => o.Label).Should().Equal("Game (Japan).sfc", "Game (USA).sfc");
        ips.Origins.Select(o => o.Reason).Should().Equal(
            ["Le nom du patch commence par le nom de cette ROM", "Le nom du patch commence par le titre de cette ROM"],
            "each ROM proposed says why");
        ips.Origins.Should().OnlyContain(o => o.HasReason);
        ips.Origins.Select(o => o.StatusLabel).Should().Equal(
            ["Exclu", "Retenu"],
            "a ROM the 1G1R leaves out is proposed too, and says so: a translation is often made for it");
        ips.Origins[0].StatusTip.Should().Contain("Game (USA).sfc", "the tooltip says why the simulation left it out");
        ips.Origins.Should().OnlyContain(o => !o.HasFolder, "a ROM at the root of the source shows no folder");
        ips.HasLocation.Should().BeFalse("it lies at the root of the patch folder");
        (ips.IsToMatch, ips.IsApplied, ips.IsUnfixable, ips.IsNeutral).Should().Be((true, false, false, false));
        ips.AccessibleName.Should().StartWith("Game (Japan) (T-Fr).ips, Ce patch ne s'applique à aucune ROM");
        ips.SelectedOrigin!.Label.Should().Be("Game (Japan).sfc", "the first suggestion is chosen, never applied");
        ips.HasSeveralForms.Should().BeFalse();
        ips.CanApply.Should().BeTrue();
        ips.ApplyTooltip.Should().StartWith("Enregistre votre choix");
        ips.CanIgnore.Should().BeTrue();
        ips.CanRestoreAutomatic.Should().BeFalse("no choice is recorded for it");
        ips.CanWriteName.Should().BeFalse("it is applied to no ROM");
        ips.HasPatchedName.Should().BeFalse();

        var bps = Row(window, "Game (Japan) (T-En).bps");
        bps.State.Should().Be(PatchRowState.Applied);
        bps.Source.Should().Be(PatchCrcSource.Recorded);
        bps.ListSummary.Should().Be("→ Game (Japan).sfc");
        bps.StatusText.Should().Be("Appliqué à Game (Japan).sfc : le patch désigne lui-même sa ROM et la vérifie");
        bps.PatchedNameText.Should().Be("Version patchée : Game (Japan) [T-En].sfc");
        bps.CanChoose.Should().BeFalse("a BPS records its ROM");
        bps.CanIgnore.Should().BeTrue("any patch can be set aside");

        var hack = Row(window, "Hack.ips");
        hack.StateMark.Should().Be("✓");
        hack.ListSummary.Should().Be("→ Game (USA).sfc (readme)");
        hack.StatusText.Should().Be("Appliqué à Game (USA).sfc grâce à Hack.txt");
        hack.Location.Should().Be("hacks");
        (hack.IsToMatch, hack.IsApplied, hack.IsUnfixable).Should().Be((false, true, false));
        hack.AccessibleName.Should().Be("Hack.ips → Game (USA).sfc (readme)");

        var byName = Row(window, named);
        byName.StatusText.Should().Be("Appliqué à Game (USA).sfc d'après son nom");
        byName.CanApply.Should().BeFalse("its ROM is chosen");
        byName.ApplyTooltip.Should().StartWith("Le patch est déjà appliqué à cette ROM");
        byName.CanWriteName.Should().BeFalse("its name already gives its ROM");

        var setAside = Row(window, "Aside.bps");
        setAside.StateMark.Should().Be("–");
        setAside.IsNeutral.Should().BeTrue();
        setAside.StatusText.Should().Be("Ce patch n'est pas appliqué : vous l'avez écarté.");
        setAside.CanIgnore.Should().BeFalse();
        setAside.IgnoreTooltip.Should().Be("Ce patch est déjà écarté.");
        setAside.CanRestoreAutomatic.Should().BeTrue();

        var unfixable = Row(window, "Other.bps");
        unfixable.State.Should().Be(PatchRowState.Unfixable);
        unfixable.StateMark.Should().Be("✗");
        unfixable.CanChoose.Should().BeFalse();
        unfixable.ApplyTooltip.Should().StartWith("Un UPS ou un BPS désigne lui-même sa ROM");
        unfixable.StatusText.Should().StartWith("Aucune ROM identifiée");
        unfixable.CanIgnore.Should().BeTrue("setting it aside silences it");

        window.SelectedListItem = window.ListItems[2];
        window.SelectedRow.Should().BeSameAs(ips, "a title of group is never selected");
        window.HasModified.Should().BeFalse();
        window.ModifiedText.Should().BeEmpty();
    }

    [Fact]
    public async Task AProposedRom_ShouldShowItsFolderInTheSource_WhereverThePatchedVersionsGo()
    {
        WriteJapanAndUsa();
        Directory.CreateDirectory(_temp.GetFilePath("src/jp"));
        File.Move(_temp.GetFilePath("src/Game (Japan).sfc"), _temp.GetFilePath("src/jp/Game (Japan).sfc"));
        _temp.WriteBytes("patches/Game (Japan) (T-Fr).ips", IpsDiff(Japan, Translate(Japan)));
        _config.PatchedPattern = "Patchs/{title}";

        var window = await LoadAsync();

        // The folder that tells two files of the same name apart is that of the source, never of the destination
        var japan = Row(window, "Game (Japan) (T-Fr).ips").Origins.Single(o => o.Label == "Game (Japan).sfc");
        japan.Folder.Should().Be("jp");
        Row(window, "Game (Japan) (T-Fr).ips").Origins.Single(o => o.Label == "Game (USA).sfc").HasFolder.Should().BeFalse();
    }

    // =========================================================================
    // Applying to a ROM
    // =========================================================================

    [Fact]
    public async Task Apply_ShouldRecordTheChoiceWithoutRenaming_TheNextSimulationFollowsIt_AndUndoForgetsIt()
    {
        WriteJapanAndUsa();
        var patch = IpsDiff(Japan, Translate(Japan));
        var path = _temp.WriteBytes("patches/Game (Japan) (T-Fr).ips", patch);
        var window = await LoadAsync();
        var row = window.SelectedRow!;
        window.ApplyCommand.CanExecute(null).Should().BeTrue();

        await window.ApplyCommand.ExecuteAsync(null);

        File.Exists(path).Should().BeTrue("the patch is not renamed");
        Directory.GetFiles(_patches).Should().ContainSingle();
        LinkOf(patch).Should().Be(PatchLink.ToOrigin("Game (Japan) (T-Fr).ips", Crc(Japan), "Game (Japan).sfc"));
        row.State.Should().Be(PatchRowState.Applied);
        row.Source.Should().Be(PatchCrcSource.Link);
        row.StatusText.Should().Be("Appliqué à Game (Japan).sfc : votre choix");
        row.ListSummary.Should().Be("→ Game (Japan).sfc (votre choix)");
        ListOf(window).Should().Equal("Appliqués (1)", row.Name);
        window.SelectedRow.Should().BeSameAs(row, "the patch stays selected in its new group");
        window.ApplyCommand.CanExecute(null).Should().BeFalse("it is applied to that ROM");
        row.CanRestoreAutomatic.Should().BeTrue();
        row.CanUndo.Should().BeTrue();
        row.CanWriteName.Should().BeTrue("its name does not give its ROM, which other tools need");
        window.ModifiedText.Should().Be("1 patch modifié");
        _log.Should().ContainSingle(l => l.Message.Key == "log.patch_choice");

        await SimulateAsync();
        PatchedVersion().Origin.DisplayName.Should().Be("Game (Japan).sfc");
        PatchedVersion().CrcSource.Should().Be(PatchCrcSource.Link);

        await window.UndoCommand.ExecuteAsync(null);

        LinkOf(patch).Should().BeNull("undoing the choice forgets it");
        row.State.Should().Be(PatchRowState.ToMatch);
        row.StatusText.Should().StartWith("Ce patch ne s'applique à aucune ROM : ni son nom");
        row.CanUndo.Should().BeFalse();
        ListOf(window).Should().Equal("À associer (1)", row.Name);
        window.HasModified.Should().BeFalse();
    }

    [Fact]
    public async Task AChoiceThatCannotBeSaved_ShouldBeSaidInTheDetail_AndChangeNothing()
    {
        WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game (T-Fr).ips", IpsDiff(Usa, Translate(Usa)));
        var window = await LoadAsync();
        var row = Row(window, "Game (T-Fr).ips");
        window.SelectedRow = row;
        row.SelectedOrigin!.Label.Should().Be("Game (USA).sfc", "the release the sort writes comes first");

        // A file of choices written by a newer version is never written
        File.WriteAllText(_services.PatchLinks.FilePath, """{ "version": 99 }""");
        await window.ApplyCommand.ExecuteAsync(null);

        row.State.Should().Be(PatchRowState.ToMatch);
        row.ResultText.Should().StartWith("Le fichier de vos choix pour les patchs").And.Contain("version plus récente");
        row.CanUndo.Should().BeFalse();
        window.HasModified.Should().BeFalse();
        _log.Should().Contain(l => l.Level == LogLevel.Error && l.Message.Key == "log.patch_links_newer");
        File.ReadAllText(_services.PatchLinks.FilePath).Should().Be("""{ "version": 99 }""");
    }

    [Fact]
    public async Task TheSearch_ShouldGiveAnotherRom_ToAPatchThatCanTakeOne()
    {
        WriteJapanAndUsa(("Super Mario Bros. (World).sfc", Data(3, 512)));
        var patch = IpsDiff(Japan, Translate(Japan));
        _temp.WriteBytes("patches/Super Mario Frères.ips", patch);
        _temp.WriteBytes("patches/Other.bps", BpsLinear(Data(9, 64), Data(10, 64)));
        var window = await LoadAsync();
        var row = Row(window, "Super Mario Frères.ips");
        row.HasOrigins.Should().BeFalse("a translated title proposes nothing");
        row.CanApply.Should().BeFalse();
        row.ApplyTooltip.Should().Be("Choisissez d'abord la ROM du patch.");

        window.SelectedRow = row;
        window.OriginSearchText = "mario world";
        window.HasSearchResults.Should().BeTrue();
        window.OriginSearchResults.Select(o => o.Label).Should().Equal("Super Mario Bros. (World).sfc");
        window.SelectedSearchResult = window.OriginSearchResults[0];
        await row.FormsLoading;

        row.SelectedOrigin!.Label.Should().Be("Super Mario Bros. (World).sfc");
        row.Origins.Should().ContainSingle("the ROM found joins those of the patch");
        row.Origins[0].HasReason.Should().BeFalse("a ROM found by the search is not proposed by the simulation");
        row.CanApply.Should().BeTrue();

        // Enter in the search takes the first file found, and records nothing
        window.OriginSearchText = "game";
        window.ChooseFirstSearchResultCommand.Execute(null);
        await row.FormsLoading;
        row.SelectedOrigin!.Label.Should().Be(window.OriginSearchResults[0].Label);
        LinkOf(patch).Should().BeNull();

        window.SelectedRow = Row(window, "Other.bps");
        window.OriginSearchText.Should().BeEmpty("the search is that of the selected patch");
        window.CanSearch.Should().BeFalse("no ROM can be chosen for a BPS");
        window.OriginSearchText = "game";
        window.OriginSearchResults.Should().BeEmpty();
    }

    [Fact]
    public async Task ARomWithACopierHeader_ShouldOfferBothFormsOfItsData_AndRecordTheOneChosen()
    {
        var data = Data(4, 8192);
        var file = (byte[])[.. Ascii("NES\u001a"), .. new byte[12], .. data];
        WriteJapanAndUsa();
        _temp.WriteBytes("src/Hero (USA).nes", file);
        var datPath = _temp.GetFilePath("dat/snes.dat");
        File.WriteAllText(datPath, File.ReadAllText(datPath).Replace(
            "</datafile>",
            $"""<game name="Hero (USA)"><description>Hero (USA)</description><rom name="Hero (USA).nes" size="{data.Length}" crc="{Crc(data):x8}"/></game></datafile>"""));
        var patch = IpsDiff(data, Translate(data));
        _temp.WriteBytes("patches/Hero (USA) (Hack).ips", patch);

        var window = await LoadAsync();

        var row = window.Rows.Single();
        row.Forms.Select(f => f.Label).Should().Equal(
            $"Avec l'en-tête copieur (CRC32 {Crc(file):X8})",
            $"Sans l'en-tête copieur (CRC32 {Crc(data):X8})");
        row.HasSeveralForms.Should().BeTrue();
        row.SelectedForm!.Form.Crc32.Should().Be(Crc(file), "the whole file comes first");
        row.SelectedForm = row.Forms[1];
        row.SelectedForm = null;
        row.SelectedForm.Should().BeSameAs(row.Forms[1], "an empty choice from the view is ignored");

        await window.ApplyCommand.ExecuteAsync(null);

        LinkOf(patch)!.OriginCrc32.Should().Be(Crc(data));
        await SimulateAsync();
        PatchedVersion().SourceOffset.Should().Be(16, "the patch applies to the data after the header");
    }

    [Fact]
    public async Task APatchInAnArchive_ShouldTakeARom_LikeAnyOther()
    {
        WriteJapanAndUsa();
        var patch = IpsDiff(Japan, Translate(Japan));
        var zip = _temp.GetFilePath("patches/Game (Japan) (T-En).zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var stream = archive.CreateEntry("patch.ips").Open())
        {
            stream.Write(patch);
        }

        var before = File.ReadAllBytes(zip);
        var window = await LoadAsync();

        var row = window.Rows.Single();
        row.IsInArchive.Should().BeTrue();
        row.Location.Should().Be("Game (Japan) (T-En).zip");
        row.SelectedOrigin!.Label.Should().Be("Game (Japan).sfc");
        row.CanApply.Should().BeTrue();
        window.ListItems.OfType<PatchGroupViewModel>().Single().HasBatch.Should().BeTrue();

        await window.ApplyCommand.ExecuteAsync(null);

        row.CanWriteName.Should().BeFalse("an archive is never modified");
        File.ReadAllBytes(zip).Should().Equal(before);
        await SimulateAsync();
        PatchedVersion().File.ArchiveMember.Should().Be("patch.ips");
    }

    [Fact]
    public async Task AnIpsAppliedFromItsName_ShouldApplyToAnotherRom_WithoutBeingRenamed()
    {
        WriteJapanAndUsa();
        var path = _temp.WriteBytes($"patches/Game (T-Fr) [{Crc(Japan):x8}].ips", IpsDiff(Japan, Translate(Japan)));
        var window = await LoadAsync();
        var row = window.SelectedRow!;
        row.Source.Should().Be(PatchCrcSource.FileName);
        row.SelectedForm!.Form.Crc32.Should().Be(Crc(Japan), "its ROM and form stay chosen");
        row.CanApply.Should().BeFalse();
        window.CanSearch.Should().BeTrue();

        window.OriginSearchText = "usa";
        window.SelectedSearchResult = window.OriginSearchResults.Single();
        await row.FormsLoading;
        await window.ApplyCommand.ExecuteAsync(null);

        File.Exists(path).Should().BeTrue();
        row.StatusText.Should().Be("Appliqué à Game (USA).sfc : votre choix");
        row.CanWriteName.Should().BeTrue("its name gives another ROM");
        row.WriteNameTooltip.Should().Contain($"Game (T-Fr) [{Crc(Usa):x8}].ips");
        await SimulateAsync();
        PatchedVersion().Origin.DisplayName.Should().Be("Game (USA).sfc", "the choice comes before the name");
    }

    // =========================================================================
    // Setting aside, and back to the automatic choice
    // =========================================================================

    [Fact]
    public async Task AnyPatch_ShouldBeSetAside_AndBackToTheAutomaticChoice_ShouldGiveBackWhereItStood()
    {
        WriteJapanAndUsa();
        var patch = BpsLinear(Japan, Translate(Japan));
        _temp.WriteBytes("patches/Game (T-En).bps", patch);
        var window = await LoadAsync();
        var row = window.SelectedRow!;
        row.CanRestoreAutomatic.Should().BeFalse();

        await window.IgnoreCommand.ExecuteAsync(null);

        LinkOf(patch)!.Kind.Should().Be(PatchLinkKind.Ignored);
        row.State.Should().Be(PatchRowState.Ignored);
        row.HasPatchedName.Should().BeFalse("the patch no longer gives a patched version");
        ListOf(window).Should().Equal("Écartés (1)", "Game (T-En).bps");
        window.IgnoreCommand.CanExecute(null).Should().BeFalse();
        window.RestoreAutomaticCommand.CanExecute(null).Should().BeTrue();

        // Without a choice when the window opened, the window knows where the automatic choice puts the patch
        await window.RestoreAutomaticCommand.ExecuteAsync(null);

        LinkOf(patch).Should().BeNull();
        row.State.Should().Be(PatchRowState.Applied);
        row.StatusText.Should().Be("Appliqué à Game (Japan).sfc : le patch désigne lui-même sa ROM et la vérifie");
        row.PatchedNameText.Should().Be("Version patchée : Game (Japan) [T-En].sfc");
        window.HasModified.Should().BeFalse();
        row.CanUndo.Should().BeTrue("each change is undone in turn");
    }

    [Fact]
    public async Task APatchSetAside_ShouldStayOutOfTheNextSimulation_AndComeBackWhenItsChoiceIsForgotten()
    {
        WriteJapanAndUsa();
        var patch = IpsDiff(Japan, Translate(Japan));
        _temp.WriteBytes($"patches/Game (T-Fr) [{Crc(Japan):x8}].ips", patch);
        var window = await LoadAsync();
        await window.IgnoreCommand.ExecuteAsync(null);

        await SimulateAsync();
        _result!.Items.Should().NotContain(it => it.Patch != null);
        _result.IgnoredPatches.Should().ContainSingle();
        window.Load();
        var row = window.Rows.Single();
        row.State.Should().Be(PatchRowState.Ignored);

        // A choice recorded when the window opened: the window cannot tell where the automatic choice puts the patch
        await window.RestoreAutomaticCommand.ExecuteAsync(null);

        row.State.Should().Be(PatchRowState.Automatic);
        row.StateMark.Should().Be("↺");
        row.StatusText.Should().Be("Revenu au choix automatique : la prochaine simulation dira à quelle ROM il s'applique.");
        ListOf(window).Should().Equal("Choix automatique (1)", row.Name);
        window.ModifiedText.Should().Be("1 patch modifié");
        await SimulateAsync();
        PatchedVersion().CrcSource.Should().Be(PatchCrcSource.FileName, "its name designates its ROM again");
    }

    [Fact]
    public async Task TheRomWrittenInTheName_ShouldBeRemoved_AfterANewSimulation_WhenUndoNoLongerCan()
    {
        WriteJapanAndUsa();
        var patch = IpsDiff(Japan, Translate(Japan));
        var original = _temp.WriteBytes("patches/Game (Japan) (T-Fr).ips", patch);
        var window = await LoadAsync();
        await window.ApplyCommand.ExecuteAsync(null);
        await window.WriteNameCommand.ExecuteAsync(null);
        var named = Path.Combine(_patches, $"Game (Japan) (T-Fr) [{Crc(Japan):x8}].ips");
        File.Exists(named).Should().BeTrue();

        // A new simulation: the window shows its results, and its history of changes is gone
        await window.SimulateAgainCommand.ExecuteAsync(null);
        var row = window.Rows.Single();
        row.CanUndo.Should().BeFalse();
        row.Source.Should().Be(PatchCrcSource.Link, "the choice comes before the name");
        row.CanWriteName.Should().BeFalse("its name already gives its ROM");
        row.CanClearName.Should().BeTrue();
        row.ClearNameTooltip.Should().StartWith("Renomme le patch en Game (Japan) (T-Fr).ips");

        await window.ClearNameCommand.ExecuteAsync(null);

        File.Exists(original).Should().BeTrue("the name it had before is back");
        File.Exists(named).Should().BeFalse();
        row.CanClearName.Should().BeFalse();
        LinkOf(patch)!.OriginCrc32.Should().Be(Crc(Japan), "the choice does not change");
        _log.Should().Contain(l => l.Message.Key == "log.patch_name_cleared");
        await SimulateAsync();
        PatchedVersion().Origin.DisplayName.Should().Be("Game (Japan).sfc");
    }

    [Fact]
    public async Task RemovingTheRomFromTheName_OfAPatchOnlyItsNameApplied_ShouldKeepItsRomAsAChoice()
    {
        WriteJapanAndUsa();
        var patch = IpsDiff(Japan, Translate(Japan));
        var named = _temp.WriteBytes($"patches/Game (T-Fr) [{Crc(Japan):x8}].ips", patch);
        var window = await LoadAsync();
        var row = window.SelectedRow!;
        row.Source.Should().Be(PatchCrcSource.FileName);
        row.ClearNameTooltip.Should().Contain("garde la même ROM");

        await window.ClearNameCommand.ExecuteAsync(null);

        File.Exists(Path.Combine(_patches, "Game (T-Fr).ips")).Should().BeTrue();
        File.Exists(named).Should().BeFalse();
        LinkOf(patch)!.OriginCrc32.Should().Be(Crc(Japan), "the ROM its name gave is recorded first");
        row.Source.Should().Be(PatchCrcSource.Link);
        await SimulateAsync();
        PatchedVersion().CrcSource.Should().Be(PatchCrcSource.Link);
        PatchedVersion().Origin.DisplayName.Should().Be("Game (Japan).sfc", "RomSieve applies it as before");

        // Each change undone in turn: the name, then the choice
        await window.UndoCommand.ExecuteAsync(null);
        File.Exists(named).Should().BeTrue();
        await window.UndoCommand.ExecuteAsync(null);
        LinkOf(patch).Should().BeNull();
        row.Source.Should().Be(PatchCrcSource.FileName);
        window.HasModified.Should().BeFalse();
    }

    [Fact]
    public async Task AnIpsAppliedThanksToItsReadme_ShouldBeSetAside_OrGetItsRomInItsName()
    {
        WriteJapanAndUsa();
        _temp.WriteBytes("patches/a/Hack.ips", IpsDiff(Usa, Translate(Usa)));
        _temp.WriteText("patches/a/readme.txt", $"CRC32: {Crc(Usa):X8}");
        var window = await LoadAsync();
        var row = window.SelectedRow!;
        row.Source.Should().Be(PatchCrcSource.Notes);
        row.CanWriteName.Should().BeTrue();
        row.WriteNameTooltip.Should().StartWith($"Renomme le patch en Hack [{Crc(Usa):x8}].ips");

        await window.WriteNameCommand.ExecuteAsync(null);

        var renamed = Path.Combine(_patches, "a", $"Hack [{Crc(Usa):x8}].ips");
        File.Exists(renamed).Should().BeTrue();
        row.Name.Should().Be(Path.GetFileName(renamed));
        row.State.Should().Be(PatchRowState.Applied, "renaming changes nothing else");
        row.CanWriteName.Should().BeFalse();
        _log.Should().Contain(l => l.Message.Key == "log.patch_renamed");

        await window.UndoCommand.ExecuteAsync(null);
        File.Exists(Path.Combine(_patches, "a", "Hack.ips")).Should().BeTrue();
        row.Name.Should().Be("Hack.ips");

        await window.IgnoreCommand.ExecuteAsync(null);
        await SimulateAsync();
        _result!.Items.Should().NotContain(it => it.Patch != null, "its readme no longer applies it");
    }

    // =========================================================================
    // « Accept all the suggestions »
    // =========================================================================

    [Fact]
    public async Task AcceptingTheSuggestions_ShouldShowASummaryCheckedByNothing_AndRecordOnlyThePatchesChecked()
    {
        WriteJapanAndUsa();
        var french = IpsDiff(Japan, Translate(Japan));
        var hack = IpsDiff(Usa, [.. Usa, .. Ascii("Hack")]);
        _temp.WriteBytes("patches/Game (Japan) (T-Fr).ips", french);
        _temp.WriteBytes("patches/Game (USA) (Hack).ips", hack);
        _temp.WriteBytes("patches/Super Mario Frères.ips", IpsDiff(Japan, [.. Japan, .. Ascii("Mario")]));
        var window = await LoadAsync();
        var group = window.ListItems.OfType<PatchGroupViewModel>().Single();
        group.HasBatch.Should().BeTrue();
        group.BatchLabel.Should().Be("Accepter toutes les suggestions (2)", "a patch without a ROM chosen is left out");

        await group.BatchCommand.ExecuteAsync(PatchGroupKind.ToMatch);

        window.IsBatchMode.Should().BeTrue();
        window.BatchEntries.Select(e => (e.Name, e.OriginLabel)).Should().BeEquivalentTo(new[]
        {
            ("Game (Japan) (T-Fr).ips", "Game (Japan).sfc"),
            ("Game (USA) (Hack).ips", "Game (USA).sfc")
        });
        window.BatchEntries.Should().OnlyContain(e => !e.IsChecked, "a suggestion is never taken alone");
        window.ApplyBatchCommand.CanExecute(null).Should().BeFalse();

        window.BatchEntries.Single(e => e.Name == "Game (USA) (Hack).ips").IsChecked = true;
        window.ApplyBatchLabel.Should().Be("Appliquer (1)");
        await window.ApplyBatchCommand.ExecuteAsync(null);

        window.IsBatchMode.Should().BeFalse();
        LinkOf(hack)!.OriginCrc32.Should().Be(Crc(Usa));
        LinkOf(french).Should().BeNull("only the patches checked are applied");
        Directory.GetFiles(_patches).Should().HaveCount(3, "nothing is renamed");
        window.ModifiedText.Should().Be("1 patch modifié");
        Row(window, "Game (USA) (Hack).ips").CanUndo.Should().BeTrue();
    }

    [Fact]
    public async Task Back_ShouldLeaveTheSummaryWithoutRecordingAnything()
    {
        WriteJapanAndUsa();
        var patch = IpsDiff(Japan, Translate(Japan));
        _temp.WriteBytes("patches/Game (Japan) (T-Fr).ips", patch);
        var window = await LoadAsync();
        await window.OpenBatchCommand.ExecuteAsync(PatchGroupKind.ToMatch);
        window.CheckAllBatchCommand.Execute(null);

        window.CloseBatchCommand.Execute(null);

        window.IsBatchMode.Should().BeFalse();
        LinkOf(patch).Should().BeNull();
        window.HasModified.Should().BeFalse();
    }

    // =========================================================================
    // Opening the window
    // =========================================================================

    [Fact]
    public async Task ThePatchedVersionOfTheTable_ShouldOpenTheWindowOnItsPatch()
    {
        WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game (T-Fr).ips", IpsDiff(Japan, Translate(Japan)));
        _temp.WriteBytes($"patches/Game (T-En) [{Crc(Usa):x8}].ips", IpsDiff(Usa, Translate(Usa)));
        _temp.WriteBytes("patches/Other.bps", BpsLinear(Usa, Translate(Usa)));
        await SimulateAsync();
        var window = CreateWindow();
        var versions = _result!.Items.Select(it => it.Patch).OfType<PatchedVersion>().ToList();
        var ips = versions.Single(p => p.Format == PatchFormat.Ips);
        var bps = versions.Single(p => p.Format == PatchFormat.Bps);

        window.CanOpenFor(null).Should().BeFalse();
        window.CanOpenFor(ips).Should().BeTrue();
        window.OpenForLabel(ips).Should().Be("Changer la ROM de ce patch…");
        window.CanOpenFor(bps).Should().BeTrue("the window shows every patch");
        window.OpenForLabel(bps).Should().Be("Voir ce patch…", "a BPS records its ROM, which nothing can change");
        _dialogs.OnShowPatches = shown =>
        {
            shown.SelectedRow!.Name.Should().Be($"Game (T-En) [{Crc(Usa):x8}].ips");
            return Task.CompletedTask;
        };
        await window.OpenForAsync(ips);
        _dialogs.ShownPatches.Should().ContainSingle();
        _simulations.Should().Be(1, "nothing changed in the window");
    }

    [Fact]
    public async Task APatchSetAsideFromTheTable_ShouldLeaveTheNextSimulation()
    {
        WriteJapanAndUsa();
        var bps = BpsLinear(Usa, Translate(Usa));
        _temp.WriteBytes("patches/Other.bps", bps);
        await SimulateAsync();
        var window = CreateWindow();
        var version = PatchedVersion();
        window.CanIgnoreFor(version).Should().BeTrue();
        window.CanIgnoreFor(null).Should().BeFalse();

        await window.IgnoreForAsync(version);

        LinkOf(bps)!.Kind.Should().Be(PatchLinkKind.Ignored);
        _simulations.Should().Be(2, "the table shows the patch set aside at once");
        _result!.Items.Should().NotContain(it => it.Patch != null);
        _result.IgnoredPatches.Should().ContainSingle();
        _log.Should().Contain(l => l.Message.Key == "log.patch_choice");
        CreateWindow(isRunning: () => true).CanIgnoreFor(version).Should().BeFalse("nothing changes during a run");
    }

    [Fact]
    public async Task TheCommandOfTheBanner_ShouldOpenTheWindow_AndSimulateAgainWhenAsked()
    {
        WriteJapanAndUsa();
        var window = CreateWindow();
        window.OpenCommand.CanExecute(null).Should().BeFalse("before a run, there is nothing to show");

        _temp.WriteBytes("patches/Game (T-En).bps", BpsLinear(Usa, Translate(Usa)));
        await SimulateAsync();
        window.Refresh();
        window.PatchCount.Should().Be(1);
        window.IsOpenLinkVisible.Should().BeTrue("no patch is left out: « See the patches » opens the window");
        window.OpenCommand.CanExecute(null).Should().BeTrue();

        _dialogs.OnShowPatches = shown =>
        {
            shown.Rows.Should().ContainSingle().Which.Name.Should().Be("Game (T-En).bps");
            return Task.CompletedTask;
        };
        await window.OpenCommand.ExecuteAsync(null);
        _dialogs.ShownPatches.Should().ContainSingle().Which.Should().BeSameAs(window);
        _simulations.Should().Be(1, "closing the window without a choice runs nothing");

        // A choice made, the window closed without simulating: the simulation runs again
        _dialogs.OnShowPatches = shown => shown.IgnoreCommand.ExecuteAsync(null);
        await window.OpenCommand.ExecuteAsync(null);
        _simulations.Should().Be(2, "the table always shows what the choices give");
        _result!.IgnoredPatches.Should().ContainSingle();

        // The choice taken back, simulated in the window: closing it runs nothing more
        _dialogs.OnShowPatches = async shown =>
        {
            await shown.RestoreAutomaticCommand.ExecuteAsync(null);
            shown.SimulateAgainCommand.CanExecute(null).Should().BeTrue();
            await shown.SimulateAgainCommand.ExecuteAsync(null);
            shown.HasModified.Should().BeFalse();
            shown.Rows.Single().State.Should().Be(PatchRowState.Applied, "the window shows the new simulation");
        };
        await window.OpenCommand.ExecuteAsync(null);
        _simulations.Should().Be(3, "one simulation, in the window");

        // A patch left out: the text of the patches left out opens the window, the link hides
        _temp.WriteBytes("patches/Hack.ips", IpsDiff(Japan, [.. Japan, .. Ascii("Hack")]));
        await SimulateAsync();
        window.Refresh();
        window.PatchCount.Should().Be(2);
        window.IsOpenLinkVisible.Should().BeFalse();

        var running = CreateWindow(isRunning: () => true);
        running.Refresh();
        running.OpenCommand.CanExecute(null).Should().BeFalse("nothing opens during a run");
        running.IsOpenLinkVisible.Should().BeFalse();
    }
}
