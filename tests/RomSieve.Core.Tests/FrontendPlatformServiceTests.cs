using System.IO;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using Xunit;

namespace RomSieve.Core.Tests;

/// <summary>
/// The frontend table (<c>Resources/frontends/platforms.json</c>) and its service: every folder is a safe single
/// segment, valid under Windows as under Linux; each real DAT name finds its platform, whatever its variant, date or
/// TOSEC category; an unknown name finds nothing; an Amiga release goes to the folder of its machine.
/// </summary>
public sealed class FrontendPlatformServiceTests
{
    private static readonly FrontendPlatformService Service = new();
    private static readonly char[] WindowsForbidden = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly FrontendTarget[] Frontends =
        [FrontendTarget.Batocera, FrontendTarget.RetroPie, FrontendTarget.EsDe, FrontendTarget.Recalbox, FrontendTarget.MiSTer];

    [Fact]
    public void Table_ShouldBeEmbeddedAndRegistered()
    {
        Service.Platforms.Should().HaveCountGreaterThan(50);

        using var provider = new ServiceCollection().AddRomSieveCore().BuildServiceProvider();
        provider.GetRequiredService<IFrontendPlatformService>().Platforms.Should().HaveCount(Service.Platforms.Count);
    }

    [Fact]
    public void TranslationCatalogs_ShouldNotTakeTheTableForALanguage()
    {
        LocalizationService.Translations.Keys.Should().BeEquivalentTo("en", "es", "fr");
    }

    [Fact]
    public void EveryFolder_ShouldBeASafeSingleSegment_UnderWindowsAndLinux()
    {
        var organizer = new CoreTestServices().Organizer;
        var folders = Service.Platforms.SelectMany(p => p.Folders.Select(f => (p.Id, Frontend: f.Key, Name: f.Value))).ToList();
        folders.Should().NotBeEmpty();

        foreach (var (id, frontend, name) in folders)
        {
            var because = $"{frontend} folder of {id} is '{name}'";
            PathOrganizerService.IsSafeRelativeName(name).Should().BeTrue(because);
            name.Should().NotBeNullOrWhiteSpace(because).And.NotBe(".", because).And.NotBe("..", because);
            name.IndexOfAny(WindowsForbidden).Should().Be(-1, because);
            name.Should().NotContainAny(["\0", "\t", "\n"], because);
            name.Should().Be(name.Trim().TrimEnd('.'), because);
            organizer.SanitizePathSegment(name).Should().Be(name, because);
            new[] { DestinationGuard.QuarantineDirectoryName, DestinationGuard.MarkerFileName }
                .Should().NotContain(reserved => reserved.Equals(name, StringComparison.OrdinalIgnoreCase), because);
        }
    }

    [Fact]
    public void Archives_ShouldBeKnownExtensions_OfAFrontendThatHasAFolder()
    {
        foreach (var platform in Service.Platforms)
        {
            foreach (var (frontend, archives) in platform.Archives)
            {
                platform.Folders.Should().ContainKey(frontend, $"{platform.Id} has archives for {frontend}");
                archives.Should().BeSubsetOf(["zip", "7z"], $"archives of {platform.Id} for {frontend}");
                archives.Should().OnlyHaveUniqueItems();
            }
        }
    }

    [Fact]
    public void MiSTer_ShouldNeverTakeAnArchiveOfACdPlatform_NorA7z()
    {
        foreach (var id in new[] { "psx", "saturn", "megacd", "pcenginecd", "neogeocd", "3do", "cdi", "amigacd32" })
        {
            Platform(id).Archives[FrontendTarget.MiSTer].Should().BeEmpty(id);
        }

        Service.Platforms.Where(p => p.Archives.ContainsKey(FrontendTarget.MiSTer))
            .Should().OnlyContain(p => !p.Archives[FrontendTarget.MiSTer].Contains("7z"));
    }

    [Fact]
    public void EveryPlatform_ShouldBeReachable_AndEveryNameOfTheTableFindItsPlatform()
    {
        foreach (var platform in Service.Platforms)
        {
            var reachable = platform.DatNames.Count > 0
                || platform.Conventions.Count > 0
                || Service.Platforms.Any(p => p.AgaPlatformId == platform.Id);
            reachable.Should().BeTrue(platform.Id);
            platform.Folders.Should().NotBeEmpty(platform.Id);

            foreach (var name in platform.DatNames)
            {
                Service.FindPlatform(name)!.Id.Should().Be(platform.Id, name);
            }
        }
    }

    // Header names and file names of the DATs of nointro_dat/, redump_dat/ and tosec_dat/, and names of the
    // No-Intro, Redump and TOSEC systems with their variants
    [Theory]
    [InlineData("Nintendo - Nintendo 64 (BigEndian)", "n64")]
    [InlineData("Nintendo - Nintendo 64 (BigEndian) (20260922-113213)", "n64")]
    [InlineData("Nintendo - Nintendo 64 (ByteSwapped)", "n64")]
    [InlineData("Commodore - Amiga CD32", "amigacd32")]
    [InlineData("Commodore - Amiga CD32 - Datfile (207) (2026-05-08 20-54-04)", "amigacd32")]
    [InlineData("Commodore - Amiga CD32 - Discs (207) (2026-05-08 20-54-04)", "amigacd32")]
    [InlineData("Sony - PlayStation", "psx")]
    [InlineData("Sony - PlayStation - Datfile (10914) (2026-06-15 11-55-46)", "psx")]
    [InlineData("Amstrad CPC - Games - [DSK]", "amstradcpc")]
    [InlineData("Atari ST - Games - [ST]", "atarist")]
    [InlineData("Commodore Amiga - Demos - Various - [ADF]", "amiga")]
    [InlineData("Commodore Amiga - Games - [ADF] (TOSEC-v2025-01-30_CM)", "amiga")]
    [InlineData("Commodore C64 - Games - Shoot'em Up - [D64]", "c64")]
    [InlineData("MSX MSX - Games - [ROM]", "msx")]
    [InlineData("MSX MSX2 - Games - [DSK]", "msx2")]
    [InlineData("Sinclair ZX Spectrum - Games - [TZX]", "zxspectrum")]
    [InlineData("Commodore Amiga CD32 - Games", "amigacd32")]
    [InlineData("Sega Mega Drive & Genesis - Games", "megadrive")]
    [InlineData("Nintendo - Game Boy", "gb")]
    [InlineData("Nintendo - Game Boy (Private)", "gb")]
    [InlineData("nintendo - game boy", "gb")]
    [InlineData("Nintendo  -  Game Boy  ", "gb")]
    [InlineData("Nintendo - Nintendo Entertainment System (Headered) (Parent-Clone)", "nes")]
    [InlineData("Nintendo - Nintendo Entertainment System (Headerless) (Retool)", "nes")]
    [InlineData("Nintendo - Super Nintendo Entertainment System", "snes")]
    [InlineData("Sega - Mega Drive - Genesis", "megadrive")]
    [InlineData("Sega - Master System - Mark III", "mastersystem")]
    [InlineData("Sega - Mega CD & Sega CD", "megacd")]
    [InlineData("NEC - PC Engine - TurboGrafx-16", "pcengine")]
    [InlineData("NEC - PC Engine CD & TurboGrafx CD", "pcenginecd")]
    [InlineData("Microsoft - MSX2", "msx2")]
    [InlineData("Bandai - WonderSwan Color", "wonderswancolor")]
    [InlineData("Sony - PlayStation Portable (Decrypted)", "psp")]
    [InlineData("FinalBurn Neo - Arcade Games", "fbneo")]
    public void RealDatNames_ShouldFindTheirPlatform(string datName, string platform)
    {
        Service.FindPlatform(datName)!.Id.Should().Be(platform);
    }

    // Another set of the same system, a manufacturer alone, a prefix of no system
    [Theory]
    [InlineData("Nintendo - Game Boy Advance (e-Reader)")]
    [InlineData("Sony - PlayStation Portable (PSN)")]
    [InlineData("Non-Redump - Sony - PlayStation")]
    [InlineData("Nintendo")]
    [InlineData("Commodore")]
    [InlineData("Unknown System - Games - [BIN]")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void UnknownNames_ShouldFindNothing(string? datName)
    {
        Service.FindPlatform(datName).Should().BeNull();
        Service.FindFolder(datName, NamingConvention.Auto, FrontendTarget.Batocera).Should().BeNull();
    }

    [Fact]
    public void MameMachines_ShouldBeFoundByTheirConvention_AndFinalBurnNeoByItsName()
    {
        Service.FindPlatform("MAME ROMs (merged) - mini", NamingConvention.Mame)!.Id.Should().Be("mame");
        Service.FindPlatform(null, NamingConvention.Mame)!.Id.Should().Be("mame");
        Service.FindPlatform("MAME ROMs (merged) - mini").Should().BeNull();
        Service.FindPlatform("megadriv", NamingConvention.MameSoftwareList).Should().BeNull();
        Service.FindPlatform("FinalBurn Neo - Arcade Games", NamingConvention.Mame)!.Id.Should().Be("fbneo");

        Service.FindFolder("MAME 0.289", NamingConvention.Mame, FrontendTarget.RetroPie)!.Name.Should().Be("arcade");
        Service.FindFolder("FinalBurn Neo - Arcade Games", NamingConvention.Mame, FrontendTarget.RetroPie)!.Name.Should().Be("fba");
        Service.FindFolder("MAME 0.289", NamingConvention.Mame, FrontendTarget.MiSTer).Should().BeNull("MAME has no MiSTer folder");
    }

    [Theory]
    [InlineData(FrontendTarget.Batocera, "psx")]
    [InlineData(FrontendTarget.RetroPie, "psx")]
    [InlineData(FrontendTarget.EsDe, "psx")]
    [InlineData(FrontendTarget.Recalbox, "psx")]
    [InlineData(FrontendTarget.MiSTer, "PSX")]
    public void Folder_ShouldBeTheOneOfTheFrontend(FrontendTarget frontend, string folder)
    {
        var found = Service.FindFolder("Sony - PlayStation", NamingConvention.Redump, frontend)!;

        found.Name.Should().Be(folder);
        found.Platform.Id.Should().Be("psx");
    }

    [Fact]
    public void Folders_ShouldFollowTheNamesEachFrontendGives()
    {
        Folder("Nintendo - Game Boy", FrontendTarget.MiSTer).Should().Be("GAMEBOY");
        Folder("Sega - Mega CD & Sega CD", FrontendTarget.Batocera).Should().Be("megacd");
        Folder("Sega - Mega CD & Sega CD", FrontendTarget.RetroPie).Should().Be("segacd");
        Folder("NEC - PC Engine CD & TurboGrafx CD", FrontendTarget.RetroPie).Should().Be("pcengine");
        Folder("NEC - PC Engine CD & TurboGrafx CD", FrontendTarget.MiSTer).Should().Be("TGFX16-CD");
        Folder("Bandai - WonderSwan", FrontendTarget.Batocera).Should().Be("wswan");
        Folder("Bandai - WonderSwan", FrontendTarget.RetroPie).Should().Be("wonderswan");
        Folder("Commodore - Amiga CD32", FrontendTarget.RetroPie).Should().Be("amiga");
        Folder("Nintendo - GameCube", FrontendTarget.RetroPie).Should().Be("gc");
    }

    [Fact]
    public void Folder_ShouldBeNull_WithoutAFrontend_OrForAPlatformTheFrontendLacks()
    {
        Service.FindFolder("Sony - PlayStation", NamingConvention.Redump, FrontendTarget.None).Should().BeNull();
        Service.FindFolder("Nintendo - Nintendo DS", NamingConvention.NoIntro, FrontendTarget.MiSTer).Should().BeNull();
        Service.FindFolder("Philips - CD-i", NamingConvention.Redump, FrontendTarget.RetroPie).Should().BeNull();
    }

    [Fact]
    public void Archives_ShouldSayWhatTheFrontendReads()
    {
        var retropie = Service.FindFolder("Sony - PlayStation", NamingConvention.Redump, FrontendTarget.RetroPie)!;
        retropie.AcceptsArchive(".zip").Should().BeFalse();

        var esde = Service.FindFolder("Sony - PlayStation", NamingConvention.Redump, FrontendTarget.EsDe)!;
        esde.AcceptsArchive("zip").Should().BeTrue();
        esde.AcceptsArchive(".7Z").Should().BeTrue();

        var mister = Service.FindFolder("Nintendo - Super Nintendo Entertainment System", NamingConvention.NoIntro, FrontendTarget.MiSTer)!;
        mister.AcceptsArchive(".zip").Should().BeTrue();
        mister.AcceptsArchive(".7z").Should().BeFalse();

        Service.FindFolder("Sony - PlayStation", NamingConvention.Redump, FrontendTarget.MiSTer)!.AcceptsArchive(".zip").Should().BeFalse();
        Service.FindFolder("Sony - PlayStation", NamingConvention.Redump, FrontendTarget.Batocera)!.AcceptsArchive(".zip").Should().BeFalse();
    }

    [Theory]
    [InlineData("AGA", FrontendTarget.Batocera, "amiga1200", "amiga-aga")]
    [InlineData("A1200", FrontendTarget.Batocera, "amiga1200", "amiga-aga")]
    [InlineData(null, FrontendTarget.Batocera, "amiga500", "amiga")]
    [InlineData("OCS-AGA", FrontendTarget.Batocera, "amiga500", "amiga")]
    [InlineData("ECS-AGA", FrontendTarget.Batocera, "amiga500", "amiga")]
    [InlineData("A500", FrontendTarget.Batocera, "amiga500", "amiga")]
    [InlineData("AGA", FrontendTarget.EsDe, "amiga1200", "amiga-aga")]
    [InlineData(null, FrontendTarget.EsDe, "amiga", "amiga")]
    [InlineData("AGA", FrontendTarget.Recalbox, "amiga1200", "amiga-aga")]
    [InlineData(null, FrontendTarget.Recalbox, "amiga600", "amiga")]
    [InlineData("AGA", FrontendTarget.RetroPie, "amiga", "amiga-aga")]
    [InlineData(null, FrontendTarget.RetroPie, "amiga", "amiga")]
    [InlineData("AGA", FrontendTarget.MiSTer, "Amiga500", "amiga-aga")]
    [InlineData(null, FrontendTarget.MiSTer, "Amiga500", "amiga")]
    public void AmigaRelease_ShouldGoToTheFolderOfItsMachine(string? system, FrontendTarget frontend, string folder, string platform)
    {
        var item = new ParsedRomName { RawFilename = "Game (1994)(Publisher).adf", System = system };

        var found = Service.FindFolder("Commodore Amiga - Games - [ADF]", NamingConvention.Tosec, frontend, item)!;

        found.Name.Should().Be(folder);
        found.Platform.Id.Should().Be(platform);
    }

    [Fact]
    public void AmigaDat_WithoutAnItem_ShouldGiveTheFolderOfTheOcsMachines()
    {
        Folder("Commodore - Amiga", FrontendTarget.Batocera).Should().Be("amiga500");
    }

    [Fact]
    public void OtherPlatforms_ShouldIgnoreTheChipsetOfAnItem()
    {
        var item = new ParsedRomName { RawFilename = "Game (1994)(Publisher)(AGA).iso", System = "AGA" };

        Service.FindFolder("Commodore - Amiga CD32", NamingConvention.Redump, FrontendTarget.Batocera, item)!.Name.Should().Be("amigacd32");
    }

    [Theory]
    [InlineData("Sony - PlayStation - Datfile (10914) (2026-06-15 11-55-46)", "Sony - PlayStation")]
    [InlineData("Nintendo - Game Boy (Private) (20260101-000000)", "Nintendo - Game Boy")]
    [InlineData("Nintendo - Game Boy (20260101)", "Nintendo - Game Boy")]
    [InlineData("Commodore Amiga - Games - [ADF] (TOSEC-v2025-01-30_CM)", "Commodore Amiga - Games - [ADF]")]
    [InlineData("Nintendo - Game Boy Advance (e-Reader) (Private)", "Nintendo - Game Boy Advance (e-Reader)")]
    [InlineData("  Sega -  32X ", "Sega - 32X")]
    [InlineData(null, "")]
    public void NormalizeDatName_ShouldOnlyDropWhatKeepsThePlatform(string? datName, string expected)
    {
        FrontendPlatformService.NormalizeDatName(datName).Should().Be(expected);
    }

    [Fact]
    public void InvalidTables_ShouldBeRefused()
    {
        var unknownFrontend = """{"platforms":[{"id":"x","dats":["X"],"folders":{"launchbox":"x"}}]}""";
        var unknownConvention = """{"platforms":[{"id":"x","conventions":["arcade"],"folders":{"batocera":"x"}}]}""";
        var noId = """{"platforms":[{"dats":["X"],"folders":{"batocera":"x"}}]}""";

        foreach (var json in new[] { unknownFrontend, unknownConvention, noId })
        {
            var read = () => FrontendPlatformService.ReadTable(new MemoryStream(Encoding.UTF8.GetBytes(json)));
            read.Should().Throw<Exception>(json);
        }

        var duplicateName = """{"platforms":[{"id":"a","dats":["Sega - 32X"],"folders":{}},{"id":"b","dats":["sega - 32x (Private)"],"folders":{}}]}""";
        var missingAga = """{"platforms":[{"id":"a","dats":["A"],"aga":"b","folders":{}}]}""";
        var duplicateId = """{"platforms":[{"id":"a","dats":["A"],"folders":{}},{"id":"a","dats":["B"],"folders":{}}]}""";
        foreach (var json in new[] { duplicateName, missingAga, duplicateId })
        {
            var table = FrontendPlatformService.ReadTable(new MemoryStream(Encoding.UTF8.GetBytes(json)));
            var create = () => new FrontendPlatformService(table);
            create.Should().Throw<InvalidDataException>(json);
        }
    }

    [Fact]
    public void FrontendValues_ShouldBeTheKeysOfTheTable()
    {
        foreach (var frontend in Frontends)
        {
            EnumExtensions.ParseFrontendTarget(frontend.ToConfigString()).Should().Be(frontend);
        }

        Frontends.Select(f => f.ToConfigString()).Should().Equal("batocera", "retropie", "es-de", "recalbox", "mister");
        EnumExtensions.ParseFrontendTarget("launchbox").Should().Be(FrontendTarget.None);
        EnumExtensions.ParseFrontendTarget(null).Should().Be(FrontendTarget.None);
        FrontendTarget.None.ToConfigString().Should().Be("none");
    }

    private static FrontendPlatform Platform(string id) => Service.Platforms.Single(p => p.Id == id);

    private static string? Folder(string datName, FrontendTarget frontend) =>
        Service.FindFolder(datName, NamingConvention.Auto, frontend)?.Name;
}
