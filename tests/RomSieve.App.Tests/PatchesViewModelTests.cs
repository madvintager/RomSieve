using System.IO.Compression;
using RomSieve.App.ViewModels;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using static RomSieve.Core.Tests.Fixtures.PatchTestEncoder;

namespace RomSieve.App.Tests;

/// <summary>
/// The window of the patches (<see cref="PatchesViewModel"/>), on a real simulation: the list grouped by state, the
/// ROMs proposed and the forms of their data, the search among the identified files, what applying would rename, the
/// direct actions (apply, stop applying, undo) and what they say, the actions on a group with their summary checked by
/// nothing beforehand, the footer, and the command of the banner, which runs a new simulation when the window asks for
/// one.
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

    public void Dispose() => _temp.Dispose();

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

    private PatchesViewModel CreateWindow(Func<bool>? isRunning = null) => new(
        _services.I18n,
        _dialogs,
        new PatchNamingService(_services.Patches),
        new PatchLinkService(_temp.GetFilePath(PatchLinkService.FileName), _services.ChecksumService, _services.CrcCache),
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

    // =========================================================================
    // The list
    // =========================================================================

    [Fact]
    public async Task TheList_ShouldGroupThePatchesByState()
    {
        WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game (Japan) (T-Fr).ips", IpsDiff(Japan, Translate(Japan)));
        _temp.WriteBytes("patches/Other.bps", BpsLinear(Data(9, 64), Data(10, 64)));
        _temp.WriteBytes("patches/hacks/Hack.ips", IpsDiff(Usa, Translate(Usa)));
        _temp.WriteText("patches/hacks/Hack.txt", $"ROM CRC32: {Crc(Usa):X8}");
        var named = $"Game (T-En) [{Crc(Usa):x8}].ips";
        _temp.WriteBytes($"patches/{named}", IpsDiff(Usa, Translate(Usa)));

        var window = await LoadAsync();

        ListOf(window).Should().Equal(
            "À associer (1)", "Game (Japan) (T-Fr).ips",
            "Appliqués (2)", "Hack.ips", named,
            "Non réparables (1)", "Other.bps");
        window.SelectedRow.Should().BeSameAs(Row(window, "Game (Japan) (T-Fr).ips"), "the first patch of the list is selected");
        window.SelectedListItem.Should().BeSameAs(window.SelectedRow);

        var ips = Row(window, "Game (Japan) (T-Fr).ips");
        ips.State.Should().Be(PatchRowState.ToMatch);
        ips.StateMark.Should().Be("●");
        ips.StatusText.Should().Be("Ce patch ne s'applique à aucune ROM : ni son nom ni son readme ne disent pour quelle ROM il a été fait.");
        ips.ListSummary.Should().BeEmpty();
        ips.Origins.Select(o => o.Label).Should().Equal("Game (Japan).sfc", "Game (USA).sfc");
        ips.Origins.Should().OnlyContain(o => !o.HasFolder, "a ROM at the root of the source shows no folder");
        ips.HasLocation.Should().BeFalse("it lies at the root of the patch folder");
        (ips.IsToMatch, ips.IsApplied, ips.IsUnfixable).Should().Be((true, false, false));
        ips.AccessibleName.Should().StartWith("Game (Japan) (T-Fr).ips, Ce patch ne s'applique à aucune ROM");
        ips.SelectedOrigin!.Label.Should().Be("Game (Japan).sfc", "the first suggestion is chosen, never applied");
        ips.HasSeveralForms.Should().BeFalse();
        ips.CanApply.Should().BeTrue();
        ips.ApplyLabel.Should().Be("Appliquer à cette ROM");
        ips.PreviewNewName.Should().Be($"Game (Japan) (T-Fr) [{Crc(Japan):x8}].ips");
        ips.CanDetach.Should().BeFalse();
        ips.DetachTooltip.Should().Be("Ce patch n'est appliqué à aucune ROM.");

        var hack = Row(window, "Hack.ips");
        hack.State.Should().Be(PatchRowState.AppliedByNote);
        hack.StateMark.Should().Be("✓");
        hack.ListSummary.Should().Be("→ Game (USA).sfc (readme)");
        hack.StatusText.Should().Be("Appliqué à Game (USA).sfc grâce à Hack.txt");
        hack.Location.Should().Be("hacks");
        hack.HasLocation.Should().BeTrue();
        (hack.IsToMatch, hack.IsApplied, hack.IsUnfixable).Should().Be((false, true, false));
        hack.AccessibleName.Should().Be("Hack.ips → Game (USA).sfc (readme)");

        var byName = Row(window, named);
        byName.ListSummary.Should().Be("→ Game (USA).sfc");
        byName.StatusText.Should().Be("Appliqué à Game (USA).sfc d'après son nom");

        var bps = Row(window, "Other.bps");
        bps.State.Should().Be(PatchRowState.Unfixable);
        bps.StateMark.Should().Be("✗");
        bps.CanChoose.Should().BeFalse();
        bps.CanApply.Should().BeFalse();
        bps.ApplyTooltip.Should().Be("Renommer ce patch ne le réparerait pas : voir sa raison.");
        bps.StatusText.Should().StartWith("Aucune ROM identifiée");

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
    // The actions on a patch
    // =========================================================================

    [Fact]
    public async Task Apply_ShouldRenameThePatchAndMoveItToTheAppliedOnes_AndUndoShouldBringItBack()
    {
        WriteJapanAndUsa();
        var path = _temp.WriteBytes("patches/Game (Japan) (T-Fr).ips", IpsDiff(Japan, Translate(Japan)));
        var window = await LoadAsync();
        var row = window.SelectedRow!;
        window.ApplyCommand.CanExecute(null).Should().BeTrue();

        await window.ApplyCommand.ExecuteAsync(null);

        var renamed = Path.Combine(_patches, $"Game (Japan) (T-Fr) [{Crc(Japan):x8}].ips");
        File.Exists(renamed).Should().BeTrue();
        File.Exists(path).Should().BeFalse();
        row.Name.Should().Be(Path.GetFileName(renamed));
        row.State.Should().Be(PatchRowState.AppliedByName);
        row.StatusText.Should().Be("Son nom désigne maintenant Game (Japan).sfc : la prochaine simulation l'appliquera.");
        row.ListSummary.Should().Be("→ Game (Japan).sfc");
        ListOf(window).Should().Equal("Appliqués (1)", row.Name);
        window.SelectedRow.Should().BeSameAs(row, "the patch stays selected in its new group");
        window.ApplyCommand.CanExecute(null).Should().BeFalse("its name already designates that ROM");
        row.ApplyTooltip.Should().StartWith("Le patch est déjà appliqué à cette ROM");
        row.CanUndo.Should().BeTrue();
        window.ModifiedText.Should().Be("1 patch modifié");
        window.HasModified.Should().BeTrue();

        await window.UndoCommand.ExecuteAsync(null);

        File.Exists(path).Should().BeTrue();
        File.Exists(renamed).Should().BeFalse();
        row.Name.Should().Be("Game (Japan) (T-Fr).ips");
        row.State.Should().Be(PatchRowState.ToMatch);
        row.StatusText.Should().StartWith("Ce patch ne s'applique à aucune ROM : ni son nom");
        row.CanUndo.Should().BeFalse();
        ListOf(window).Should().Equal("À associer (1)", row.Name);
        window.HasModified.Should().BeFalse();
        _log.Select(l => l.Message.Key).Should().Equal("log.patch_renamed", "log.patch_rename_undone");
    }

    [Fact]
    public async Task ARefusal_ShouldBeSaidInTheDetail()
    {
        WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game (T-Fr).ips", IpsDiff(Usa, Translate(Usa)));
        _temp.WriteText($"patches/Game (T-Fr) [{Crc(Usa):x8}].ips", "taken");
        var window = await LoadAsync();
        var row = Row(window, "Game (T-Fr).ips");
        window.SelectedRow = row;
        row.SelectedOrigin!.Label.Should().Be("Game (USA).sfc", "the release the sort writes comes first");

        await window.ApplyCommand.ExecuteAsync(null);

        row.State.Should().Be(PatchRowState.ToMatch);
        row.ResultText.Should().Be($"Un fichier porte déjà le nom Game (T-Fr) [{Crc(Usa):x8}].ips");
        window.HasModified.Should().BeFalse();
    }

    [Fact]
    public async Task TheSearch_ShouldGiveAnotherRom_ToAPatchThatCanTakeOne()
    {
        WriteJapanAndUsa(("Super Mario Bros. (World).sfc", Data(3, 512)));
        _temp.WriteBytes("patches/Super Mario Frères.ips", IpsDiff(Japan, Translate(Japan)));
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
        row.CanApply.Should().BeTrue();

        // Enter in the search takes the first file found, and renames nothing
        window.OriginSearchText = "game";
        window.ChooseFirstSearchResultCommand.Execute(null);
        await row.FormsLoading;
        row.SelectedOrigin!.Label.Should().Be(window.OriginSearchResults[0].Label);
        File.Exists(Path.Combine(_patches, "Super Mario Frères.ips")).Should().BeTrue();

        window.SelectedRow = Row(window, "Other.bps");
        window.OriginSearchText.Should().BeEmpty("the search is that of the selected patch");
        window.CanSearch.Should().BeFalse("no ROM fixes a BPS");
        window.OriginSearchText = "game";
        window.OriginSearchResults.Should().BeEmpty();
    }

    [Fact]
    public async Task ARomWithACopierHeader_ShouldOfferBothFormsOfItsData()
    {
        var data = Data(4, 8192);
        var file = (byte[])[.. Ascii("NES\u001a"), .. new byte[12], .. data];
        WriteJapanAndUsa();
        _temp.WriteBytes("src/Hero (USA).nes", file);
        var datPath = _temp.GetFilePath("dat/snes.dat");
        File.WriteAllText(datPath, File.ReadAllText(datPath).Replace(
            "</datafile>",
            $"""<game name="Hero (USA)"><description>Hero (USA)</description><rom name="Hero (USA).nes" size="{data.Length}" crc="{Crc(data):x8}"/></game></datafile>"""));
        _temp.WriteBytes("patches/Hero (USA) (Hack).ips", IpsDiff(file, Translate(file)));

        var window = await LoadAsync();

        var row = window.Rows.Single();
        row.Forms.Select(f => f.Label).Should().Equal(
            $"Avec l'en-tête copieur (CRC32 {Crc(file):X8})",
            $"Sans l'en-tête copieur (CRC32 {Crc(data):X8})");
        row.HasSeveralForms.Should().BeTrue();
        row.SelectedForm!.Form.Crc32.Should().Be(Crc(file), "the whole file comes first");
        row.SelectedForm = row.Forms[1];
        row.PreviewNewName.Should().Be($"Hero (USA) (Hack) [{Crc(data):x8}].ips");
        row.SelectedForm = null;
        row.SelectedForm.Should().BeSameAs(row.Forms[1], "an empty choice from the view is ignored");
    }

    [Fact]
    public async Task APatchInAnArchive_ShouldShowItsCrcWithoutBeingRenamed()
    {
        WriteJapanAndUsa();
        using (var zip = ZipFile.Open(_temp.GetFilePath("patches/Game (Japan) (T-En).zip"), ZipArchiveMode.Create))
        using (var stream = zip.CreateEntry("patch.ips").Open())
        {
            stream.Write(IpsDiff(Japan, Translate(Japan)));
        }

        var window = await LoadAsync();

        var row = window.Rows.Single();
        row.IsInArchive.Should().BeTrue();
        row.Location.Should().Be("Game (Japan) (T-En).zip");
        row.SelectedOrigin!.Label.Should().Be("Game (Japan).sfc");
        row.CanApply.Should().BeFalse();
        row.HasPreview.Should().BeFalse();
        row.ApplyTooltip.Should().Be("Un patch rangé dans une archive ne se renomme pas : extrayez-le d'abord.");
        row.ArchiveHint.Should().Be($"Dans une archive, qui n'est jamais modifiée : extrayez le patch pour écrire le CRC32 {Crc(Japan):X8} dans son nom");
        window.ListItems.OfType<PatchGroupViewModel>().Single().HasBatch.Should().BeFalse("a patch in an archive is never renamed");
    }

    [Fact]
    public async Task AnIpsAppliedFromItsName_ShouldApplyToAnotherRomOnceRenamed()
    {
        WriteJapanAndUsa();
        var path = _temp.WriteBytes($"patches/Game (T-Fr) [{Crc(Japan):x8}].ips", IpsDiff(Japan, Translate(Japan)));
        var window = await LoadAsync();
        var row = window.SelectedRow!;
        row.State.Should().Be(PatchRowState.AppliedByName);
        row.SelectedForm!.Form.Crc32.Should().Be(Crc(Japan), "its ROM and form stay chosen");
        row.CanApply.Should().BeFalse();
        row.HasPreview.Should().BeFalse();
        window.CanSearch.Should().BeTrue();

        window.OriginSearchText = "usa";
        window.SelectedSearchResult = window.OriginSearchResults.Single();
        await row.FormsLoading;
        row.PreviewNewName.Should().Be($"Game (T-Fr) [{Crc(Usa):x8}].ips", "the CRC32 of the new ROM replaces the old one");
        await window.ApplyCommand.ExecuteAsync(null);

        File.Exists(path).Should().BeFalse();
        File.Exists(Path.Combine(_patches, $"Game (T-Fr) [{Crc(Usa):x8}].ips")).Should().BeTrue();
        row.StatusText.Should().Be("Son nom désigne maintenant Game (USA).sfc : la prochaine simulation l'appliquera.");
        await SimulateAsync();
        _result!.Items.Single(it => it.Patch is not null).Patch!.Origin.DisplayName.Should().Be("Game (USA).sfc");
    }

    [Fact]
    public async Task AnIpsAppliedFromItsName_ShouldStopApplying_AndBeProposedAgain_OrBeUndone()
    {
        WriteJapanAndUsa();
        var path = _temp.WriteBytes($"patches/Game (Japan) (T-Fr) [{Crc(Japan):x8}].ips", IpsDiff(Japan, Translate(Japan)));
        var window = await LoadAsync();
        var row = window.SelectedRow!;
        row.CanDetach.Should().BeTrue();
        row.DetachTooltip.Should().StartWith("Retire du nom du patch ce qui désigne sa ROM");

        await window.DetachCommand.ExecuteAsync(null);

        var detached = Path.Combine(_patches, "Game (Japan) (T-Fr).ips");
        File.ReadAllBytes(detached).Should().Equal(IpsDiff(Japan, Translate(Japan)));
        row.State.Should().Be(PatchRowState.ToMatch);
        row.StatusText.Should().Be("Ce patch ne s'applique plus à aucune ROM : la prochaine simulation vous le proposera de nouveau.");
        ListOf(window).Should().Equal("À associer (1)", "Game (Japan) (T-Fr).ips");
        window.DetachCommand.CanExecute(null).Should().BeFalse();
        row.CanApply.Should().BeTrue("it can be applied again, to its ROM or another one");
        _log.Last().Message.Key.Should().Be("log.patch_detached");

        await window.UndoCommand.ExecuteAsync(null);
        File.Exists(path).Should().BeTrue();
        row.State.Should().Be(PatchRowState.AppliedByName);
        row.StatusText.Should().Be("Appliqué à Game (Japan).sfc d'après son nom");

        await window.DetachCommand.ExecuteAsync(null);
        await SimulateAsync();
        _result!.Items.Should().NotContain(it => it.Patch != null);
        window.Load();
        window.Rows.Single().SelectedOrigin!.Label.Should().Be("Game (Japan).sfc", "its name proposes its ROM again");
    }

    [Fact]
    public async Task AnIpsAppliedThanksToItsReadme_ShouldGetItsRomInItsName_ButNotStopApplying()
    {
        WriteJapanAndUsa();
        _temp.WriteBytes("patches/a/Hack.ips", IpsDiff(Usa, Translate(Usa)));
        _temp.WriteText("patches/a/readme.txt", $"CRC32: {Crc(Usa):X8}");
        var window = await LoadAsync();
        var row = window.SelectedRow!;
        row.State.Should().Be(PatchRowState.AppliedByNote);
        row.ApplyLabel.Should().Be("Écrire la ROM dans le nom");
        row.ApplyTooltip.Should().StartWith("Écrit dans le nom du patch la ROM que donne son readme");
        window.DetachCommand.CanExecute(null).Should().BeFalse("its readme would designate its ROM again");
        row.DetachTooltip.Should().StartWith("Ce patch est appliqué grâce à son readme");

        await window.ApplyCommand.ExecuteAsync(null);

        File.Exists(Path.Combine(_patches, "a", $"Hack [{Crc(Usa):x8}].ips")).Should().BeTrue();
        row.State.Should().Be(PatchRowState.AppliedByName);
    }

    // =========================================================================
    // The actions on a group
    // =========================================================================

    [Fact]
    public async Task AcceptingTheSuggestions_ShouldShowASummaryCheckedByNothing_AndRenameOnlyThePatchesChecked()
    {
        WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game (Japan) (T-Fr).ips", IpsDiff(Japan, Translate(Japan)));
        _temp.WriteBytes("patches/Game (USA) (Hack).ips", IpsDiff(Usa, Translate(Usa)));
        _temp.WriteBytes("patches/Super Mario Frères.ips", IpsDiff(Japan, Translate(Japan)));
        var window = await LoadAsync();
        var group = window.ListItems.OfType<PatchGroupViewModel>().Single();
        group.HasBatch.Should().BeTrue();
        group.BatchLabel.Should().Be("Accepter toutes les suggestions (2)", "a patch without a ROM chosen is left out");

        await group.BatchCommand.ExecuteAsync(PatchGroupKind.ToMatch);

        window.IsBatchMode.Should().BeTrue();
        window.BatchTitle.Should().Be("Patchs à associer : cochez ceux à renommer");
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
        File.Exists(Path.Combine(_patches, $"Game (USA) (Hack) [{Crc(Usa):x8}].ips")).Should().BeTrue();
        File.Exists(Path.Combine(_patches, "Game (Japan) (T-Fr).ips")).Should().BeTrue("only the patches checked are renamed");
        window.ModifiedText.Should().Be("1 patch modifié");
        Row(window, $"Game (USA) (Hack) [{Crc(Usa):x8}].ips").CanUndo.Should().BeTrue();
    }

    [Fact]
    public async Task TheIpsAppliedByAReadme_ShouldGetTheirRomInTheirNameTogether()
    {
        WriteJapanAndUsa();
        _temp.WriteBytes("patches/a/Hack.ips", IpsDiff(Usa, Translate(Usa)));
        _temp.WriteText("patches/a/readme.txt", $"CRC32: {Crc(Usa):X8}");
        _temp.WriteBytes("patches/Game (T-Fr).ips", IpsDiff(Japan, Translate(Japan)));
        var window = await LoadAsync();
        var applied = window.ListItems.OfType<PatchGroupViewModel>().Single(g => g.Kind == PatchGroupKind.Applied);
        applied.BatchLabel.Should().Be("Écrire leur ROM dans leur nom (1)");

        await window.OpenBatchCommand.ExecuteAsync(PatchGroupKind.Applied);
        window.BatchEntries.Should().ContainSingle().Which.Name.Should().Be("Hack.ips");
        window.CheckAllBatchCommand.Execute(null);
        await window.ApplyBatchCommand.ExecuteAsync(null);

        File.Exists(Path.Combine(_patches, "a", $"Hack [{Crc(Usa):x8}].ips")).Should().BeTrue();
        File.Exists(Path.Combine(_patches, "Game (T-Fr).ips")).Should().BeTrue("only the IPS applied by a readme are in that summary");
    }

    [Fact]
    public async Task Back_ShouldLeaveTheSummaryWithoutRenamingAnything()
    {
        WriteJapanAndUsa();
        var path = _temp.WriteBytes("patches/Game (Japan) (T-Fr).ips", IpsDiff(Japan, Translate(Japan)));
        var window = await LoadAsync();
        await window.OpenBatchCommand.ExecuteAsync(PatchGroupKind.ToMatch);
        window.CheckAllBatchCommand.Execute(null);

        window.CloseBatchCommand.Execute(null);

        window.IsBatchMode.Should().BeFalse();
        File.Exists(path).Should().BeTrue();
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

        window.CanOpenFor(bps).Should().BeFalse("a BPS records its ROM, which nothing can change");
        window.CanOpenFor(null).Should().BeFalse();
        window.CanOpenFor(ips).Should().BeTrue();
        _dialogs.OnShowPatches = shown =>
        {
            shown.SelectedRow!.Name.Should().Be($"Game (T-En) [{Crc(Usa):x8}].ips");
            return Task.FromResult(false);
        };
        await window.OpenForAsync(ips);
        _dialogs.ShownPatches.Should().ContainSingle();
    }

    [Fact]
    public async Task TheBanner_ShouldOpenTheWindow_OnThePatchesAppliedFromTheirName()
    {
        WriteJapanAndUsa();
        _temp.WriteBytes($"patches/Game (T-En) [{Crc(Usa):x8}].ips", IpsDiff(Usa, Translate(Usa)));
        await SimulateAsync();
        var window = CreateWindow();

        window.Refresh();

        window.NameMatchedCount.Should().Be(1);
        window.IsNamingLinkVisible.Should().BeTrue();
        window.NamingLinkText.Should().Be("Voir les patchs");
        window.NamingLinkTooltip.Should().StartWith("Ouvre la liste des patchs appliqués d'après leur nom");
        window.OpenCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task TheCommandOfTheBanner_ShouldOpenTheWindow_AndSimulateAgainWhenAsked()
    {
        WriteJapanAndUsa();
        var window = CreateWindow();
        window.OpenCommand.CanExecute(null).Should().BeFalse("before a run, there is nothing to show");

        _temp.WriteBytes("patches/a/Hack.ips", IpsDiff(Usa, Translate(Usa)));
        _temp.WriteText("patches/a/readme.txt", $"CRC32: {Crc(Usa):X8}");
        await SimulateAsync();
        window.Refresh();
        window.NoteMatchedCount.Should().Be(1);
        window.IsNamingLinkVisible.Should().BeTrue("no patch is left out");
        window.NamingLinkText.Should().Be("1 patch à nommer (facultatif)");
        window.OpenCommand.CanExecute(null).Should().BeTrue();

        _dialogs.OnShowPatches = shown =>
        {
            shown.Rows.Should().ContainSingle().Which.Name.Should().Be("Hack.ips");
            return Task.FromResult(false);
        };
        await window.OpenCommand.ExecuteAsync(null);
        _dialogs.ShownPatches.Should().ContainSingle().Which.Should().BeSameAs(window);
        _simulations.Should().Be(1, "closing the window runs nothing");

        _dialogs.OnShowPatches = _ => Task.FromResult(true);
        await window.OpenCommand.ExecuteAsync(null);
        _simulations.Should().Be(2, "« Close and simulate again » runs a new simulation");

        var running = CreateWindow(isRunning: () => true);
        running.Refresh();
        running.OpenCommand.CanExecute(null).Should().BeFalse("nothing opens during a run");
        running.IsNamingLinkVisible.Should().BeFalse();
    }
}
