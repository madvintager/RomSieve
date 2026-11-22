using RomSieve.App.ViewModels;
using RomSieve.Core.Models;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// Checksum and description of the selected row in the detail panel: CRC32 of a file, SHA-1 of a CHD, nothing for a
/// whole archive, and what a MAME set holds compared with its machine.
/// </summary>
public sealed class PreviewDetailPanelTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("fr");

    public void Dispose() => _temp.Dispose();

    private ProcessItem CreateItem(string name, string crc = "", MameSetInfo? mameSet = null, ChdInfo? chd = null) =>
        new()
        {
            SourcePath = Path.Combine(_temp.Path, name),
            FileSize = 1000,
            Parsed = _services.NoIntroParser.ParseRomFilename(name),
            Crc32 = crc,
            IsWholeArchive = mameSet is not null && chd is null,
            MameSet = mameSet,
            Chd = chd
        };

    private PreviewItemViewModel CreateRow(ProcessItem item) => new(item, _services.I18n, _services.FilterService, _temp.Path);

    [Fact]
    public void APatchedVersionOfAnOriginUnderAnOldInesHeader_ShouldSayIt()
    {
        var origin = CreateItem("Game (USA).nes", "1a2b3c4d");
        var patch = Path.Combine(_temp.Path, "hack.bps");
        var patched = CreateItem("Game (USA) [hack].nes");
        patched.Patch = new PatchedVersion(new PatchFile(patch), new PatchHeader(PatchFormat.Bps), origin, 0, origin.FileSize, 0x1a2b3c4d)
        {
            UsesOldInesHeader = true
        };

        var row = CreateRow(patched);

        row.DetailPatchText.Should().Be($"{patch} (BPS), appliqué à Game (USA).nes (CRC32 1A2B3C4D, sous un ancien en-tête iNES 1.0)");
    }

    [Fact]
    public void FileWithACrc_ShouldShowItsCrc32()
    {
        var row = CreateRow(CreateItem("Game (Europe).sfc", "1a2b3c4d"));

        row.DetailChecksumLabel.Should().Be("CRC32");
        row.DetailChecksumText.Should().Be("1A2B3C4D");
        row.MameSetText.Should().BeEmpty();
        row.DetailChdText.Should().BeEmpty();
    }

    [Fact]
    public void FileWithoutCrc_ShouldShowNoChecksum()
    {
        var row = CreateRow(CreateItem("Game (Europe).sfc"));

        row.DetailChecksumLabel.Should().BeEmpty();
        row.DetailChecksumText.Should().BeEmpty();
        row.Crc32Text.Should().Be("-", "the table column keeps its placeholder");
    }

    [Fact]
    public void CompleteMameSet_ShouldShowItsMachineInsteadOfAChecksum()
    {
        var row = CreateRow(CreateItem("brubber.zip", mameSet: new MameSetInfo("brubber", 0, 0, 0)));

        row.DetailChecksumText.Should().BeEmpty("a set is a whole archive, without checksum of its own");
        row.MameSetText.Should().Be("brubber : complet");
    }

    [Fact]
    public void MameSet_ShouldDescribeWhatItLacksAndWhatItNeeds()
    {
        var set = new MameSetInfo("kof98", MissingRoms: 3, MissingDisks: 1, UnknownMembers: 2, RequiredSet: "neogeo")
        {
            HeldClones = ["kof98a", "kof98h", "kof98k", "kof98ka"],
            RequiresBios = true,
            MissingDevices = ["z80", "ym2610"],
            RequiredSets = ["pgm"]
        };

        var row = CreateRow(CreateItem("kof98.zip", mameSet: set));

        row.MameSetText.Should().Be(
            "kof98 : incomplet (3 ROM manquante(s), 1 CHD manquant(s), BIOS neogeo absent de la source, "
            + "devices absents de la source : z80, ym2610, a besoin de pgm, contient 4 clone(s), 2 fichier(s) inconnu(s) du DAT)");
    }

    [Fact]
    public void SplitClone_ShouldNameTheSetItDependsOn()
    {
        var set = new MameSetInfo("mslug2", MissingRoms: 5, MissingDisks: 0, UnknownMembers: 0, RequiredSet: "mslug");

        CreateRow(CreateItem("mslug2.zip", mameSet: set)).MameSetText
            .Should().Be("mslug2 : incomplet (5 ROM manquante(s), set split, dépend du set mslug)");
    }

    [Fact]
    public void SetCompletedByItsBios_ShouldNameTheBios()
    {
        var set = new MameSetInfo("mslug", 0, 0, 0) { BiosSet = "neogeo", RequiredSets = ["neogeo"] };

        CreateRow(CreateItem("mslug.zip", mameSet: set)).MameSetText.Should().Be("mslug : complet (a besoin de neogeo)");
    }

    [Fact]
    public void Chd_ShouldShowItsSha1AndItsMedia()
    {
        var chd = new ChdInfo(5, 1000, new string('A', 40), new string('b', 40), ParentSha1: null, ChdMediaKind.HardDisk, []);
        var row = CreateRow(CreateItem("kinst.chd", mameSet: new MameSetInfo("kinst", 0, 0, 0), chd: chd));

        row.DetailChecksumLabel.Should().Be("SHA-1");
        row.DetailChecksumText.Should().Be(new string('a', 40));
        row.DetailChdText.Should().Be("CHD v5 : disque dur");
        row.MameSetText.Should().Be("kinst : complet");
    }

    [Fact]
    public void MameSetDescription_ShouldFollowTheLanguage()
    {
        var row = CreateRow(CreateItem("mslug2.zip", mameSet: new MameSetInfo("mslug2", 5, 0, 0, RequiredSet: "mslug")));

        _services.I18n.SetLocale("en");
        try
        {
            row.RefreshFromItem();
            row.MameSetText.Should().Be("mslug2: incomplete (5 missing ROM(s), split set, depends on set mslug)");
        }
        finally
        {
            _services.I18n.SetLocale("fr");
        }
    }

    [Fact]
    public void MameSet_ShouldDescribeTheCabinetOfItsMachine()
    {
        var item = CreateItem("19xx.zip", mameSet: new MameSetInfo("19xx", 0, 0, 0));
        item.Parsed = item.Parsed with
        {
            Machine = new MameMachineInfo(false, false, false, MameDriverStatus.Good,
                Cabinet: new MameCabinet(MameOrientation.Vertical, MameScreenType.Raster, 2, MameControls.Joystick | MameControls.Spinner, 2))
        };
        var row = CreateRow(item);

        row.MameSetText.Should().Be(
            "19xx : complet" + Environment.NewLine + "Écran raster vertical, 2 joueur(s), Joystick, Spinner, 2 bouton(s)");

        // What the DAT does not say is left out; a machine it says nothing of has no second line
        item.Parsed = item.Parsed with
        {
            Machine = item.Parsed.Machine! with { Cabinet = new MameCabinet(MameOrientation.Horizontal, MameScreenType.Unknown, 0, MameControls.None, 0) }
        };
        row.RefreshFromItem();
        row.MameSetText.Should().Be("19xx : complet" + Environment.NewLine + "Écran horizontal");
        item.Parsed = item.Parsed with { Machine = item.Parsed.Machine! with { Cabinet = null } };
        row.RefreshFromItem();
        row.MameSetText.Should().Be("19xx : complet");

        // Category and rating come first, from the category file and the rating file
        item.Parsed = item.Parsed with
        {
            Machine = item.Parsed.Machine! with
            {
                Category = new MameCategory("Shooter", "Flying Vertical", false, true),
                Rating = new MameRating(80, "80 to 90 (Very Good)")
            }
        };
        row.RefreshFromItem();
        row.MameSetText.Should().Be("19xx : complet" + Environment.NewLine + "Shooter / Flying Vertical, note 80 to 90 (Very Good)");
        item.Parsed = item.Parsed with { Machine = item.Parsed.Machine! with { Category = null, Rating = MameRating.Unrated } };
        row.RefreshFromItem();
        row.MameSetText.Should().Be("19xx : complet" + Environment.NewLine + "non noté");
    }

    [Theory]
    [InlineData("Ultima IV (1986)(Origin)(Disk 1 of 2 Side B).d64", "1 sur 2, face B")]
    [InlineData("Ultima IV (1986)(Origin)(Disk 2 of 2).d64", "2 sur 2")]
    [InlineData("Law of the West (1985)(Accolade)(Side A).d64", "Side A")]
    public void TosecDisk_ShouldShowItsSide(string name, string expected)
    {
        // The two sides of a disk showed the same "1 sur 2"
        var parsed = _services.TosecParser.ParseTosecFilename(name);

        PreviewItemViewModel.FormatItemDisk(parsed, _services.I18n).DisplayText.Should().Be(expected);
    }

    [Fact]
    public void SidesOfADisk_ShouldSortSideAFirst()
    {
        var sideA = PreviewItemViewModel.FormatItemDisk(_services.TosecParser.ParseTosecFilename("U (1986)(O)(Disk 1 of 2 Side A).d64"), _services.I18n).SortKey;
        var sideB = PreviewItemViewModel.FormatItemDisk(_services.TosecParser.ParseTosecFilename("U (1986)(O)(Disk 1 of 2 Side B).d64"), _services.I18n).SortKey;

        sideA.CompareTo(sideB).Should().BeNegative();
    }

    [Fact]
    public void NoIntroDiskWithoutTotal_ShouldShowItsSide()
    {
        var parsed = _services.NoIntroParser.ParseRomFilename("Game (Japan) (Disk 1 Side B).fds", null, 0, NamingConvention.NoIntro);

        PreviewItemViewModel.FormatItemDisk(parsed, _services.I18n).DisplayText.Should().Be("1, face B");
    }

    [Fact]
    public void SingleDiscGameWithADiscNumberInItsTitle_ShouldShowNoDisk()
    {
        // "Demonstration Disc 2.0" is part of the title, not a disc number: the game is not multi-disc, and the kind
        // of media ("cd") of a Redump name is never shown as a disk.
        var name = "Demo Disc, The - Commodore Amiga CD32 Demonstration Disc 2.0 (Europe).cue";
        var game = new DatGame { Name = name[..^4], Description = name[..^4], Roms = [new DatRomEntry { Name = name, Size = 1, Crc = "00000001" }, new DatRomEntry { Name = name[..^4] + " (Track 1).bin", Size = 2, Crc = "00000002" }] };
        var parsed = _services.NoIntroParser.ParseRomFilename(name, null, 0, NamingConvention.Redump) with { IsMultidiskOverride = game.IsMultidisk };

        PreviewItemViewModel.FormatItemDisk(parsed, _services.I18n).DisplayText.Should().BeEmpty();
        PreviewItemViewModel.FormatItemDisk(parsed with { IsMultidiskOverride = true }, _services.I18n).DisplayText.Should().BeEmpty();
    }
}
