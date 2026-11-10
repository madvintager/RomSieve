using System.IO.Compression;
using RomSieve.App.ViewModels;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using static RomSieve.Core.Tests.Fixtures.PatchTestEncoder;

namespace RomSieve.App.Tests;

/// <summary>
/// The window of the patches (<see cref="PatchesViewModel"/>), on a real simulation: its rows (left out, then applied
/// thanks to a readme), the origins proposed and the forms of their data, the search among the identified files, the
/// boxes greyed for a patch in an archive or one a CRC32 cannot fix, the renaming and what it says, and the command of
/// the banner, which runs a new simulation when the window asks for one.
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
        new PatchNamingService(_services.Patches, _services.ChecksumService, _services.CrcCache),
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

    [Fact]
    public async Task TheRows_ShouldListThePatchesLeftOut_ThenThoseAppliedThanksToTheirReadme()
    {
        WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game (Japan) (T-Fr).ips", IpsDiff(Japan, Translate(Japan)));
        _temp.WriteBytes("patches/Other.bps", BpsLinear(Data(9, 64), Data(10, 64)));
        _temp.WriteBytes("patches/hacks/Hack.ips", IpsDiff(Usa, Translate(Usa)));
        _temp.WriteText("patches/hacks/Hack.txt", $"ROM CRC32: {Crc(Usa):X8}");

        var window = await LoadAsync();

        window.Rows.Select(r => (r.Name, r.Kind)).Should().Equal(
            ("Game (Japan) (T-Fr).ips", PatchRowKind.Unapplied),
            ("Other.bps", PatchRowKind.Unapplied),
            ("Hack.ips", PatchRowKind.AppliedByNote));
        window.SelectedRow.Should().BeSameAs(window.Rows[0]);

        var ips = Row(window, "Game (Japan) (T-Fr).ips");
        ips.CanChoose.Should().BeTrue();
        ips.Origins.Select(o => o.Label).Should().Equal("Game (Japan).sfc", "Game (USA).sfc");
        ips.SelectedOrigin!.Label.Should().Be("Game (Japan).sfc");
        ips.Forms.Should().ContainSingle().Which.Label.Should().Be($"CRC32 {Crc(Japan):X8}");
        ips.HasSeveralForms.Should().BeFalse();
        ips.NewNameText.Should().Be($"Nouveau nom : Game (Japan) (T-Fr) [{Crc(Japan):x8}].ips");
        ips.IsChecked.Should().BeFalse("a suggestion is never taken alone");
        ips.CanCheck.Should().BeTrue();

        var bps = Row(window, "Other.bps");
        bps.CanChoose.Should().BeFalse("a CRC32 in its name would not fix a UPS or a BPS");
        bps.CanCheck.Should().BeFalse();
        bps.CheckTooltip.Should().Be("Un CRC32 dans le nom ne répare pas ce patch : voir sa raison.");
        bps.StatusText.Should().StartWith("Aucune ROM identifiée");

        var hack = Row(window, "Hack.ips");
        hack.StatusText.Should().Be("Appliqué à Game (USA).sfc grâce à Hack.txt");
        hack.Location.Should().Be("hacks");
        hack.CanCheck.Should().BeTrue();
        window.CheckedCount.Should().Be(0);
        window.RenameCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task ARenaming_ShouldWriteTheCrcOfTheOriginChosen_AndTheNextSimulationApplyThePatch()
    {
        WriteJapanAndUsa();
        var path = _temp.WriteBytes("patches/Game (T-Fr).ips", IpsDiff(Usa, Translate(Usa)));
        var window = await LoadAsync();
        var row = window.Rows.Single();
        row.SelectedOrigin = row.Origins.Single(o => o.Label == "Game (USA).sfc");
        await row.FormsLoading;

        row.IsChecked = true;
        window.RenameLabel.Should().Be("Renommer (1)");
        await window.RenameCommand.ExecuteAsync(null);

        var renamed = Path.Combine(_patches, $"Game (T-Fr) [{Crc(Usa):x8}].ips");
        File.Exists(renamed).Should().BeTrue();
        File.Exists(path).Should().BeFalse();
        row.IsRenamed.Should().BeTrue();
        row.IsChecked.Should().BeFalse();
        row.CanCheck.Should().BeFalse("a patch is renamed once");
        row.ResultText.Should().Be($"Renommé en Game (T-Fr) [{Crc(Usa):x8}].ips");
        window.HasRenamed.Should().BeTrue();
        window.CanSearch.Should().BeFalse();
        _log.Should().ContainSingle(l => l.Message.Key == "log.patch_renamed");

        await SimulateAsync();
        _result!.UnappliedPatches.Should().BeEmpty();
        _result.Items.Single(it => it.Patch is not null).Patch!.Origin.DisplayName.Should().Be("Game (USA).sfc");
    }

    [Fact]
    public async Task ARefusal_ShouldBeSaidOnItsRow()
    {
        WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game (T-Fr).ips", IpsDiff(Usa, Translate(Usa)));
        _temp.WriteText($"patches/Game (T-Fr) [{Crc(Usa):x8}].ips", "taken");
        var window = await LoadAsync();
        var row = Row(window, "Game (T-Fr).ips");
        row.SelectedOrigin!.Label.Should().Be("Game (USA).sfc", "the release the sort writes comes first");

        row.IsChecked = true;
        await window.RenameCommand.ExecuteAsync(null);

        row.IsRenamed.Should().BeFalse();
        row.ResultText.Should().Be($"Un fichier porte déjà le nom Game (T-Fr) [{Crc(Usa):x8}].ips");
        window.HasRenamed.Should().BeFalse();
    }

    [Fact]
    public async Task TheSearch_ShouldGiveAnotherOrigin_ToAPatchThatCanTakeOne()
    {
        WriteJapanAndUsa(("Super Mario Bros. (World).sfc", Data(3, 512)));
        _temp.WriteBytes("patches/Super Mario Frères.ips", IpsDiff(Japan, Translate(Japan)));
        _temp.WriteBytes("patches/Other.bps", BpsLinear(Data(9, 64), Data(10, 64)));
        var window = await LoadAsync();
        var row = Row(window, "Super Mario Frères.ips");
        row.Origins.Should().BeEmpty("a translated title proposes nothing");
        row.SelectedForm.Should().BeNull();
        row.CanCheck.Should().BeFalse();
        row.CheckTooltip.Should().Be("Choisissez d'abord la ROM d'origine du patch.");

        window.SelectedRow = row;
        window.OriginSearchText = "mario world";
        window.OriginSearchResults.Select(o => o.Label).Should().Equal("Super Mario Bros. (World).sfc");
        window.ChooseSearchResultCommand.Execute(null);
        await row.FormsLoading;

        row.SelectedOrigin!.Label.Should().Be("Super Mario Bros. (World).sfc");
        row.CanCheck.Should().BeTrue();

        window.SelectedRow = Row(window, "Other.bps");
        window.OriginSearchText.Should().BeEmpty("the search is that of the selected patch");
        window.OriginSearchText = "game";
        window.OriginSearchResults.Should().BeEmpty("a CRC32 cannot fix a BPS");
        window.ChooseSearchResultCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task AnOriginWithACopierHeader_ShouldOfferBothFormsOfItsData()
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
        row.NewNameText.Should().Be($"Nouveau nom : Hero (USA) (Hack) [{Crc(data):x8}].ips");
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
        row.CanCheck.Should().BeFalse();
        row.CheckTooltip.Should().Be("Un patch rangé dans une archive ne se renomme pas : extrayez-le d'abord.");
        row.NewNameText.Should().Be($"Dans une archive, qui n'est jamais modifiée : extrayez le patch pour écrire le CRC32 {Crc(Japan):X8} dans son nom");
    }

    [Fact]
    public async Task TheIpsAppliedByAReadme_ShouldBeCheckedTogether_AndRenamedWithTheirCrc()
    {
        WriteJapanAndUsa();
        _temp.WriteBytes("patches/a/Hack.ips", IpsDiff(Usa, Translate(Usa)));
        _temp.WriteText("patches/a/readme.txt", $"CRC32: {Crc(Usa):X8}");
        _temp.WriteBytes("patches/Game (T-Fr).ips", IpsDiff(Japan, Translate(Japan)));
        var window = await LoadAsync();

        window.CheckNoteMatchedCommand.Execute(null);

        Row(window, "Hack.ips").IsChecked.Should().BeTrue();
        Row(window, "Game (T-Fr).ips").IsChecked.Should().BeFalse("only the IPS applied by a readme are checked together");
        await window.RenameCommand.ExecuteAsync(null);
        File.Exists(Path.Combine(_patches, "a", $"Hack [{Crc(Usa):x8}].ips")).Should().BeTrue();
    }

    [Fact]
    public async Task AnIpsAppliedFromItsName_ShouldBeListed_AndApplyToAnotherRomOnceRenamed()
    {
        WriteJapanAndUsa();
        var path = _temp.WriteBytes($"patches/Game (T-Fr) [{Crc(Japan):x8}].ips", IpsDiff(Japan, Translate(Japan)));
        var window = await LoadAsync();

        var row = window.Rows.Single();
        row.Kind.Should().Be(PatchRowKind.AppliedByName);
        row.StatusText.Should().Be("Appliqué à Game (Japan).sfc d'après son nom");
        row.CanChoose.Should().BeTrue();
        row.SelectedOrigin!.Label.Should().Be("Game (Japan).sfc");
        row.SelectedForm!.Form.Crc32.Should().Be(Crc(Japan));
        row.CanCheck.Should().BeFalse("its name already designates that ROM");
        row.NewNameText.Should().BeEmpty();
        row.CheckTooltip.Should().Be("Le patch est déjà appliqué à cette ROM : choisissez-en une autre, ou cliquez sur « Ne plus appliquer à cette ROM ».");
        window.CanSearch.Should().BeTrue();

        window.OriginSearchText = "usa";
        window.ChooseSearchResultCommand.Execute(null);
        await row.FormsLoading;
        row.SelectedOrigin!.Label.Should().Be("Game (USA).sfc");
        row.CanCheck.Should().BeTrue();
        row.NewNameText.Should().Be($"Nouveau nom : Game (T-Fr) [{Crc(Usa):x8}].ips", "the CRC32 of the new ROM replaces the old one");

        row.IsChecked = true;
        await window.RenameCommand.ExecuteAsync(null);

        File.Exists(path).Should().BeFalse();
        File.Exists(Path.Combine(_patches, $"Game (T-Fr) [{Crc(Usa):x8}].ips")).Should().BeTrue();
        await SimulateAsync();
        _result!.Items.Single(it => it.Patch is not null).Patch!.Origin.DisplayName.Should().Be("Game (USA).sfc");
    }

    [Fact]
    public async Task AnIpsAppliedFromItsName_ShouldStopApplyingToItsRom_AndBeProposedAgain()
    {
        WriteJapanAndUsa();
        var path = _temp.WriteBytes($"patches/Game (Japan) (T-Fr) [{Crc(Japan):x8}].ips", IpsDiff(Japan, Translate(Japan)));
        var window = await LoadAsync();
        var row = window.Rows.Single();
        window.SelectedRow.Should().BeSameAs(row);
        row.CanDetach.Should().BeTrue();
        window.DetachTooltip.Should().StartWith("Retire du nom du patch ce qui désigne sa ROM");
        window.DetachSelectedCommand.CanExecute(null).Should().BeTrue();

        await window.DetachSelectedCommand.ExecuteAsync(null);

        File.Exists(path).Should().BeFalse();
        File.ReadAllBytes(Path.Combine(_patches, "Game (Japan) (T-Fr).ips")).Should().Equal(IpsDiff(Japan, Translate(Japan)));
        row.IsRenamed.Should().BeTrue();
        row.ResultText.Should().Be("Ne s'applique plus à sa ROM : renommé en Game (Japan) (T-Fr).ips");
        row.CanCheck.Should().BeFalse();
        window.HasRenamed.Should().BeTrue("« Simulate again » proposes it again");
        window.CanSearch.Should().BeFalse();
        window.DetachSelectedCommand.CanExecute(null).Should().BeFalse("a patch is renamed once");
        window.DetachTooltip.Should().Be(row.ResultText);
        _log.Should().ContainSingle().Which.Message.Key.Should().Be("log.patch_detached");

        await SimulateAsync();
        _result!.Items.Should().NotContain(it => it.Patch != null);
        window.Load();
        var proposed = window.Rows.Single();
        proposed.Kind.Should().Be(PatchRowKind.Unapplied);
        proposed.SelectedOrigin!.Label.Should().Be("Game (Japan).sfc", "its name proposes its ROM again");
    }

    [Fact]
    public async Task OnlyAnIpsAppliedFromItsNameOnTheDisk_ShouldStopApplying()
    {
        WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game (T-Fr).ips", IpsDiff(Japan, Translate(Japan)));
        _temp.WriteBytes("patches/a/Hack.ips", IpsDiff(Usa, Translate(Usa)));
        _temp.WriteText("patches/a/readme.txt", $"CRC32: {Crc(Usa):X8}");
        using (var zip = ZipFile.Open(_temp.GetFilePath($"patches/Packed.zip"), ZipArchiveMode.Create))
        using (var stream = zip.CreateEntry($"Packed [{Crc(Usa):x8}].ips").Open())
        {
            stream.Write(IpsDiff(Usa, Translate(Usa)));
        }

        var window = await LoadAsync();

        window.Rows.Select(r => (r.Name, r.Kind)).Should().Equal(
            ("Game (T-Fr).ips", PatchRowKind.Unapplied),
            ("Hack.ips", PatchRowKind.AppliedByNote));
        window.SelectedRow = Row(window, "Game (T-Fr).ips");
        window.DetachSelectedCommand.CanExecute(null).Should().BeFalse();
        window.DetachTooltip.Should().Be("Ce patch n'est appliqué à aucune ROM.");

        var hack = Row(window, "Hack.ips");
        window.SelectedRow = hack;
        window.DetachSelectedCommand.CanExecute(null).Should().BeFalse("its readme would give its ROM again");
        window.DetachTooltip.Should().StartWith("Ce patch est appliqué grâce à son readme");
        hack.CanChoose.Should().BeTrue("another ROM, written in its name, wins over its readme");
        window.CanSearch.Should().BeTrue();
    }

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
        window.NamingLinkText.Should().Be("1 patch à nommer");
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
        _simulations.Should().Be(2, "« Simulate again » runs a new simulation");

        var running = CreateWindow(isRunning: () => true);
        running.Refresh();
        running.OpenCommand.CanExecute(null).Should().BeFalse("nothing opens during a run");
        running.IsNamingLinkVisible.Should().BeFalse();
    }
}
