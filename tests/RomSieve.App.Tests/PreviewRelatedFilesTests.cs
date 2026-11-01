using RomSieve.App.ViewModels;
using RomSieve.Core.Models;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// Files transferred with a row of the preview table (tracks of a descriptor, CHDs of a MAME set, other files of a game):
/// badge next to the source name, list of the detail panel with their targets, column of their number and its sort.
/// </summary>
public sealed class PreviewRelatedFilesTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("fr");

    public void Dispose() => _temp.Dispose();

    private PreviewTableViewModel CreateViewModel() => new(
        _services.I18n,
        _services.FilterService,
        _services.Processor,
        () => false,
        () => null,
        () => null,
        () => null,
        () => new HashSet<string>(),
        (_, _) => { },
        (_, _) => { },
        () => { },
        () => { },
        () => { });

    private static ProcessItem CreateItem(string sourcePath, long size = 10) => new()
    {
        SourcePath = sourcePath,
        FileSize = size,
        Parsed = new ParsedRomName { RawFilename = Path.GetFileName(sourcePath), Title = Path.GetFileNameWithoutExtension(sourcePath) },
        Status = FileStatus.Valid
    };

    private static ChdInfo CreateChd() =>
        new(5, 1000, new string('a', 40), new string('b', 40), null, ChdMediaKind.Cd,
            [new ChdTrack(1, "MODE1_RAW", 100), new ChdTrack(2, "AUDIO", 100)]);

    [Fact]
    public async Task DescriptorOfASimulation_ShouldShowItsTracksWithTheirTargets()
    {
        var src = _temp.CreateSubdirectory("src");
        var dst = Path.Combine(_temp.Path, "dst");
        _temp.WriteText(Path.Combine("src", "Disc (1995)(Sony) (Track 1).bin"), "TRACK-1");
        _temp.WriteText(Path.Combine("src", "Disc (1995)(Sony) (Track 2).bin"), "TRACK-22");
        _temp.WriteText(Path.Combine("src", "Disc (1995)(Sony).cue"), """
            FILE "Disc (1995)(Sony) (Track 1).bin" BINARY
              TRACK 01 MODE1/2352
                INDEX 01 00:00:00
            FILE "Disc (1995)(Sony) (Track 2).bin" BINARY
              TRACK 02 AUDIO
                INDEX 01 00:00:00
            """);
        var result = await _services.Processor.ExecuteAsync(
            new SortConfig { SourceDir = src, DestinationDir = dst, Pattern = "{title}/" }, isDryRun: true);
        var vm = CreateViewModel();
        foreach (var item in result.Items)
        {
            vm.AddPreviewItem(item, dst);
        }

        var row = vm.AllPreviewItems.Should().ContainSingle("the tracks go with their descriptor").Subject;
        row.SourceName.Should().Be("Disc (1995)(Sony).cue");
        row.HasRelatedFiles.Should().BeTrue();
        row.RelatedFilesBadge.Should().Be("+ 2 pistes");
        row.RelatedCountText.Should().Be("2");
        row[PreviewTableViewModel.RelatedColumnIndex].Should().Be("2");

        row.RelatedFiles.Select(f => f.Name).Should().Equal("Disc (1995)(Sony) (Track 1).bin", "Disc (1995)(Sony) (Track 2).bin");
        row.RelatedFiles[0].SizeText.Should().Be("7 o");
        row.RelatedFiles[0].SizeSuffix.Should().Be(" (7 o)");
        new PreviewRelatedFile("Track.bin", string.Empty, string.Empty, string.Empty).SizeSuffix.Should().BeEmpty("no empty brackets");
        var trackTarget = result.Items.Single().RelatedItems[1].FinalTargetPath!;
        row.RelatedFiles[1].TargetText.Should().Be(Path.GetRelativePath(dst, trackTarget),
            "the target is relative to the destination, as in the table")
            .And.EndWith("Disc (1995)(Sony) (Track 2).bin");
        row.RelatedFiles.Should().OnlyContain(f => f.HasTarget && !f.HasChdText);
    }

    [Fact]
    public void Badge_ShouldCountTheFilesByKind()
    {
        var vm = CreateViewModel();
        var plain = CreateItem(Path.Combine(_temp.Path, "Game.zip"));
        var set = CreateItem(Path.Combine(_temp.Path, "kinst.zip"));
        set.RelatedItems.Add(CreateItem(Path.Combine(_temp.Path, "kinst", "kinst.chd")));
        var mixed = CreateItem(Path.Combine(_temp.Path, "Game (Track 1).bin"));
        mixed.RelatedItems.Add(CreateItem(Path.Combine(_temp.Path, "Game (Track 2).bin")));
        mixed.RelatedItems.Add(CreateItem(Path.Combine(_temp.Path, "Game (Track 3).bin")));
        mixed.RelatedItems.Add(CreateItem(Path.Combine(_temp.Path, "Game.chd")));
        var single = CreateItem(Path.Combine(_temp.Path, "Game.gdi"));
        single.RelatedItems.Add(CreateItem(Path.Combine(_temp.Path, "track01.bin")));

        foreach (var item in new[] { plain, set, mixed, single })
        {
            vm.AddPreviewItem(item, _temp.Path);
        }

        var rows = vm.AllPreviewItems.ToDictionary(r => r.SourceName);
        rows["Game.zip"].HasRelatedFiles.Should().BeFalse();
        rows["Game.zip"].RelatedFilesBadge.Should().BeEmpty();
        rows["Game.zip"].RelatedCountText.Should().BeEmpty("an empty cell keeps the column readable");
        rows["Game.zip"].RelatedFiles.Should().BeEmpty();
        rows["kinst.zip"].RelatedFilesBadge.Should().Be("+ 1 CHD");
        rows["Game (Track 1).bin"].RelatedFilesBadge.Should().Be("+ 2 fichiers, 1 CHD", "only the files of a descriptor are its tracks");
        rows["Game.gdi"].RelatedFilesBadge.Should().Be("+ 1 piste");
    }

    [Fact]
    public void ChdOfAMameSet_ShouldBeDescribedInTheDetailPanel()
    {
        var vm = CreateViewModel();
        var set = CreateItem(Path.Combine(_temp.Path, "kinst.zip"));
        var disk = CreateItem(Path.Combine(_temp.Path, "kinst", "kinst.chd"), 1536);
        disk.Chd = CreateChd();
        disk.FinalTargetPath = Path.Combine(_temp.Path, "dst", "kinst", "kinst.chd");
        set.RelatedItems.Add(disk);

        vm.AddPreviewItem(set, Path.Combine(_temp.Path, "dst"));

        var file = vm.AllPreviewItems.Single().RelatedFiles.Should().ContainSingle().Subject;
        file.Name.Should().Be("kinst.chd");
        file.SizeText.Should().Be("1.50 Ko");
        file.TargetText.Should().Be(Path.Combine("kinst", "kinst.chd"));
        file.ChdText.Should().Be("CHD v5 : CD, 2 piste(s) dont 1 audio");
    }

    [Fact]
    public void Badge_ShouldFollowTheLanguage()
    {
        var vm = CreateViewModel();
        var cue = CreateItem(Path.Combine(_temp.Path, "Disc.cue"));
        cue.RelatedItems.Add(CreateItem(Path.Combine(_temp.Path, "Disc (Track 1).bin")));
        cue.RelatedItems.Add(CreateItem(Path.Combine(_temp.Path, "Disc (Track 2).bin")));
        vm.AddPreviewItem(cue, _temp.Path);
        var row = vm.AllPreviewItems.Single();
        row.RelatedFilesBadge.Should().Be("+ 2 pistes");

        _services.I18n.SetLocale("en");
        vm.RefreshTranslations();

        row.RelatedFilesBadge.Should().Be("+ 2 tracks");
        vm.PreviewColumnHeaders[PreviewTableViewModel.RelatedColumnIndex].Should().Be("Related");
    }

    [Fact]
    public void ColumnOfRelatedFiles_ShouldSortByTheirNumber()
    {
        var vm = CreateViewModel();
        foreach (var (name, count) in new[] { ("A.cue", 2), ("B.cue", 12), ("C.zip", 0), ("D.cue", 1) })
        {
            var item = CreateItem(Path.Combine(_temp.Path, name));
            for (var i = 0; i < count; i++)
            {
                item.RelatedItems.Add(CreateItem(Path.Combine(_temp.Path, $"{name} (Track {i + 1}).bin")));
            }

            vm.AddPreviewItem(item, _temp.Path);
        }

        vm.SortPreviewByColumn(PreviewTableViewModel.RelatedColumnIndex);
        vm.FilteredPreviewItems.Select(r => r.SourceName).Should().Equal(
            ["C.zip", "D.cue", "A.cue", "B.cue"], "12 tracks come after 2, which a sort of the texts would not do");

        vm.SortPreviewByColumn(PreviewTableViewModel.RelatedColumnIndex);
        vm.FilteredPreviewItems.Select(r => r.SourceName).Should().Equal("B.cue", "A.cue", "D.cue", "C.zip");
    }
}
