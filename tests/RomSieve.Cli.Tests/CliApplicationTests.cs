using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Cli.Tests;

/// <summary>
/// Command line: simulation by default, real sort and its safeguards (non-empty destination, purge, overlap),
/// undo, profiles, exports, exit codes and translated messages. Every run uses a configuration of a temporary
/// directory, so that neither the configuration, nor the journals, nor the CRC cache of the user are touched.
/// </summary>
public sealed class CliApplicationTests : IDisposable
{
    private const string AlphaName = "Alpha (1990)(Ocean).adf";
    private const string BetaName = "Beta (1991)(Ocean).adf";

    private readonly TempDirectoryFixture _temp = new();
    private readonly string _configPath;
    private readonly string _source;
    private readonly string _destination;

    public CliApplicationTests()
    {
        _configPath = Path.Combine(_temp.CreateSubdirectory("config"), "config.json");
        _source = _temp.CreateSubdirectory("source");
        _destination = Path.Combine(_temp.Path, "destination");
        File.WriteAllText(Path.Combine(_source, AlphaName), "alpha data");
        File.WriteAllText(Path.Combine(_source, BetaName), "beta data");
    }

    public void Dispose() => _temp.Dispose();

    private sealed record CliResult(int ExitCode, string Output, string Error);

    private async Task<CliResult> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await new CliApplication(output, error, _configPath).RunAsync(args);
        return new CliResult(exitCode, output.ToString(), error.ToString());
    }

    private void WriteConfig(Action<SortSettings>? configure = null, string locale = "en", string? path = null)
    {
        var settings = new SortSettings { SourceDir = _source, DestinationDir = _destination };
        configure?.Invoke(settings);
        var data = new Dictionary<string, object?>(StringComparer.Ordinal) { ["ui_locale"] = locale };
        SortSettingsSerializer.Write(settings, data);
        var target = path ?? _configPath;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, JsonSerializer.Serialize(data));
    }

    /// <summary>Files of the destination, without the RomSieve marker and the quarantine of the purges.</summary>
    private string[] DestinationFiles() =>
        Directory.Exists(_destination)
            ? Directory.EnumerateFiles(_destination, "*", SearchOption.AllDirectories)
                .Where(f => !DestinationGuard.IsGuardPath(f))
                .Select(Path.GetFileName).OfType<string>().Order().ToArray()
            : [];

    private string[] QuarantinedFiles() =>
        Directory.Exists(Path.Combine(_destination, DestinationGuard.QuarantineDirectoryName))
            ? Directory.EnumerateFiles(Path.Combine(_destination, DestinationGuard.QuarantineDirectoryName), "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName).OfType<string>().Order().ToArray()
            : [];

    private string WriteDat()
    {
        static string Crc(string text) => Crc32.HashToUInt32(Encoding.UTF8.GetBytes(text)).ToString("x8");
        return _temp.WriteText("set.dat", $"""
            <?xml version="1.0"?>
            <datafile>
              <header><name>Test set</name><description>Test set</description><version>1</version></header>
              <game name="Alpha (1990)(Ocean)"><description>Alpha</description><rom name="{AlphaName}" size="10" crc="{Crc("alpha data")}"/></game>
              <game name="Gamma (1992)(Ocean)"><description>Gamma</description><rom name="Gamma (1992)(Ocean).adf" size="5" crc="12345678"/></game>
            </datafile>
            """);
    }

    // =========================================================================
    // Simulation and real sort
    // =========================================================================

    [Fact]
    public async Task Run_ShouldOnlySimulateByDefault()
    {
        WriteConfig();

        var result = await RunAsync("run");

        result.ExitCode.Should().Be(ExitCodes.Success, result.Error);
        Directory.Exists(_destination).Should().BeFalse("a simulation writes nothing");
        result.Output.Should().Contain("Summary: 2 files scanned").And.Contain("Simulation finished");
        File.Exists(Path.Combine(_source, AlphaName)).Should().BeTrue();
    }

    [Fact]
    public async Task Execute_ShouldSortAndUndoShouldRequireAConfirmation()
    {
        WriteConfig();

        var sort = await RunAsync("run", "--execute");
        sort.ExitCode.Should().Be(ExitCodes.Success, sort.Error);
        DestinationFiles().Should().Equal(AlphaName, BetaName);
        Directory.EnumerateFiles(Path.Combine(_temp.Path, "config", "journals")).Should().ContainSingle("the journal lives next to the configuration");

        var unconfirmed = await RunAsync("undo");
        unconfirmed.ExitCode.Should().Be(ExitCodes.ConfirmationRequired);
        unconfirmed.Output.Should().Contain("Last sort:");
        DestinationFiles().Should().HaveCount(2);

        var undo = await RunAsync("undo", "--yes");
        undo.ExitCode.Should().Be(ExitCodes.Success, undo.Error);
        undo.Output.Should().Contain("Operations undone: 3 out of 3", "two copies and the marker of the new destination");
        DestinationFiles().Should().BeEmpty();
        File.Exists(Path.Combine(_source, AlphaName)).Should().BeTrue("a copy never touches the source");

        (await RunAsync("undo", "--yes")).ExitCode.Should().Be(ExitCodes.NothingToUndo);
    }

    [Fact]
    public async Task PartialUndo_ShouldBeResumedByTheNextUndo()
    {
        WriteConfig(s => s.TransferMode = TransferMode.Move);
        (await RunAsync("run", "--execute")).ExitCode.Should().Be(ExitCodes.Success);
        var alpha = Path.Combine(_source, AlphaName);
        File.WriteAllText(alpha, "in the way");

        var partial = await RunAsync("undo", "--yes");

        partial.ExitCode.Should().Be(ExitCodes.CompletedWithErrors);
        partial.Output.Should().Contain("1 operations can be retried");

        File.Delete(alpha);
        var summary = await RunAsync("undo");
        summary.ExitCode.Should().Be(ExitCodes.ConfirmationRequired);
        summary.Output.Should().Contain("left 1 operations to retry");

        var resumed = await RunAsync("undo", "--yes");
        resumed.ExitCode.Should().Be(ExitCodes.Success, resumed.Output + resumed.Error);
        resumed.Output.Should().Contain("Operations undone: 1 out of 1");
        File.ReadAllText(alpha).Should().Be("alpha data");
        (await RunAsync("undo", "--yes")).ExitCode.Should().Be(ExitCodes.NothingToUndo);
    }

    [Fact]
    public async Task CommandLineOptions_ShouldReplaceThoseOfTheConfiguration()
    {
        WriteConfig(s => s.SourceDir = Path.Combine(_temp.Path, "elsewhere"));
        var otherDestination = Path.Combine(_temp.Path, "other");

        var result = await RunAsync("run", "--execute", "--source", _source, "--destination", otherDestination, "--mode", "move");

        result.ExitCode.Should().Be(ExitCodes.Success, result.Error);
        Directory.EnumerateFiles(otherDestination, "*", SearchOption.AllDirectories).Where(f => !DestinationGuard.IsGuardPath(f)).Should().HaveCount(2);
        File.Exists(Path.Combine(_source, AlphaName)).Should().BeFalse("the files were moved");
    }

    // =========================================================================
    // Safeguards
    // =========================================================================

    [Fact]
    public async Task CraftedArchive_ShouldWriteNothingOutsideTheDestination()
    {
        // A member named after a path next to the destination, as an archive found on the Internet may hold
        var escaped = Path.Combine(_temp.Path, "escaped (1990)(Pub).adf");
        using (var zip = ZipFile.Open(Path.Combine(_source, "crafted.zip"), ZipArchiveMode.Create))
        {
            using var stream = zip.CreateEntry(escaped.Replace('\\', '/')).Open();
            stream.Write("crafted"u8);
        }

        WriteConfig();

        var result = await RunAsync("run", "--execute");

        result.ExitCode.Should().Be(ExitCodes.CompletedWithErrors, result.Error);
        File.Exists(escaped).Should().BeFalse();
        result.Output.Should().Contain("[ERROR]").And.Contain("unsafe or invalid name");
        DestinationFiles().Should().Equal(AlphaName, BetaName);
    }

    [Fact]
    public async Task UnreadableFolderOfTheSource_ShouldBeSkippedWithAWarning()
    {
        // A folder of the system, such as System Volume Information at the root of a disk, used to stop the whole scan
        if (!_temp.TryCreateUnreadableDirectory(Path.Combine("source", "locked")))
        {
            return;
        }

        WriteConfig();

        var result = await RunAsync("run");

        result.ExitCode.Should().Be(ExitCodes.Success, result.Error);
        result.Output.Should().Contain("[WARN] Unreadable folders skipped (1)").And.Contain("Summary: 2 files scanned");
    }

    [Fact]
    public async Task DestinationAtTheRootOfADisk_ShouldBeEmptyWithItsSystemFoldersOnly()
    {
        // The root of a USB stick before any sort: it needed --append or a purge, which tried to move these folders
        var volume = _temp.WriteText(Path.Combine("destination", "System Volume Information", "WPSettings.dat"), "volume");
        var recycleBin = _temp.WriteText(Path.Combine("destination", "$RECYCLE.BIN", "desktop.ini"), "recycle bin");
        WriteConfig();

        var result = await RunAsync("run", "--execute");

        result.ExitCode.Should().Be(ExitCodes.Success, result.Error);
        DestinationFiles().Should().Contain([AlphaName, BetaName]);
        File.ReadAllText(volume).Should().Be("volume");
        File.ReadAllText(recycleBin).Should().Be("recycle bin");
    }

    [Fact]
    public async Task FilesAlreadyInTheDestination_ShouldBeSaidSoInTheSummary()
    {
        WriteConfig(s => s.CollisionMode = CollisionMode.Skip);
        var sorted = await RunAsync("run", "--execute");
        sorted.ExitCode.Should().Be(ExitCodes.Success, sorted.Error);
        sorted.Output.Should().NotContain("already in the destination");

        var simulated = await RunAsync("run");

        simulated.ExitCode.Should().Be(ExitCodes.Success, simulated.Error);
        simulated.Output.Should().Contain("Summary: 2 files scanned, 0 kept")
            .And.Contain("Of which 2 duplicates already in the destination, not written again")
            .And.Contain("--purge --yes");
    }

    [Fact]
    public async Task NonEmptyDestination_ShouldRequireAppendOrAConfirmedPurge()
    {
        WriteConfig();
        var existing = _temp.WriteText(Path.Combine("destination", "keep.txt"), "existing");

        var refused = await RunAsync("run", "--execute");
        refused.ExitCode.Should().Be(ExitCodes.ConfirmationRequired);
        refused.Error.Should().Contain("--append").And.Contain("--purge --yes");
        DestinationFiles().Should().Equal("keep.txt");

        var unconfirmedPurge = await RunAsync("run", "--execute", "--purge");
        unconfirmedPurge.ExitCode.Should().Be(ExitCodes.ConfirmationRequired);
        File.Exists(existing).Should().BeTrue("the purge was not confirmed");

        var appended = await RunAsync("run", "--execute", "--append");
        appended.ExitCode.Should().Be(ExitCodes.Success, appended.Error);
        DestinationFiles().Should().Equal(AlphaName, BetaName, "keep.txt");
    }

    [Fact]
    public async Task NonEmptyDestination_CompletedByTheConfiguration_ShouldStillRequireAppend()
    {
        // The interface asks before completing a destination, even with this option: the command line asks for --append
        WriteConfig(s => s.AppendToDestination = true);
        _temp.WriteText(Path.Combine("destination", "keep.txt"), "existing");

        var refused = await RunAsync("run", "--execute");

        refused.ExitCode.Should().Be(ExitCodes.ConfirmationRequired);
        refused.Error.Should().Contain("--append");
        DestinationFiles().Should().Equal("keep.txt");
    }

    [Fact]
    public async Task ConfirmedPurge_ShouldPrevailOverTheCompletionOfTheConfiguration()
    {
        // It used to be ignored without a word: the sort completed the destination instead of purging it
        WriteConfig(s => s.AppendToDestination = true);
        DestinationGuard.WriteMarker(_destination);
        _temp.WriteText(Path.Combine("destination", "old.txt"), "old");

        var purged = await RunAsync("run", "--execute", "--purge", "--yes");

        purged.ExitCode.Should().Be(ExitCodes.Success, purged.Error);
        DestinationFiles().Should().Equal(AlphaName, BetaName);
        QuarantinedFiles().Should().Equal("old.txt");
    }

    [Fact]
    public async Task PurgeOfAForeignDestination_ShouldNeedForceAndMoveTheContentToTheQuarantine()
    {
        WriteConfig();
        var old = _temp.WriteText(Path.Combine("destination", "old.txt"), "old");

        var refused = await RunAsync("run", "--execute", "--purge", "--yes");
        refused.ExitCode.Should().Be(ExitCodes.ConfirmationRequired);
        refused.Error.Should().Contain("--force-foreign-purge");
        File.Exists(old).Should().BeTrue();

        var result = await RunAsync("run", "--execute", "--purge", "--yes", "--force-foreign-purge");

        result.ExitCode.Should().Be(ExitCodes.Success, result.Error);
        DestinationFiles().Should().Equal(AlphaName, BetaName);
        QuarantinedFiles().Should().Equal("old.txt");

        var undo = await RunAsync("undo", "--yes");
        undo.ExitCode.Should().Be(ExitCodes.Success, undo.Output + undo.Error);
        File.ReadAllText(old).Should().Be("old", "undoing the sort puts the purged content back");
        Directory.EnumerateFileSystemEntries(_destination).Should().Equal(old);
    }

    [Fact]
    public async Task PurgeOfADestinationMarkedByAPreviousSort_ShouldOnlyNeedYes()
    {
        WriteConfig();
        (await RunAsync("run", "--execute")).ExitCode.Should().Be(ExitCodes.Success);
        File.Exists(Path.Combine(_destination, DestinationGuard.MarkerFileName)).Should().BeTrue();

        var result = await RunAsync("run", "--execute", "--purge", "--yes");

        result.ExitCode.Should().Be(ExitCodes.Success, result.Error);
        DestinationFiles().Should().Equal(AlphaName, BetaName);
        QuarantinedFiles().Should().Equal(AlphaName, BetaName);
    }

    [Fact]
    public async Task Quarantine_ShouldBeShownThenEmptiedOnlyWithYes()
    {
        WriteConfig();
        (await RunAsync("run", "--execute")).ExitCode.Should().Be(ExitCodes.Success);
        (await RunAsync("quarantine")).Output.Should().Contain($"No quarantine in {_destination}.");
        (await RunAsync("run", "--execute", "--purge", "--yes")).ExitCode.Should().Be(ExitCodes.Success);

        var shown = await RunAsync("quarantine");
        shown.ExitCode.Should().Be(ExitCodes.Success, shown.Error);
        shown.Output.Should().Contain("1 purge(s), 2 file(s)");
        QuarantinedFiles().Should().Equal(AlphaName, BetaName);

        var refused = await RunAsync("quarantine", "--empty");
        refused.ExitCode.Should().Be(ExitCodes.ConfirmationRequired);
        refused.Error.Should().Contain("--yes");
        QuarantinedFiles().Should().Equal([AlphaName, BetaName], "nothing is deleted without --yes");

        var emptied = await RunAsync("quarantine", "--empty", "--yes");
        emptied.ExitCode.Should().Be(ExitCodes.Success, emptied.Error);
        emptied.Output.Should().Contain("Quarantine emptied: 2 file(s) deleted");
        QuarantinedFiles().Should().BeEmpty();
        DestinationFiles().Should().Equal([AlphaName, BetaName], "only the quarantine is deleted");
    }

    [Fact]
    public async Task Quarantine_OfAForeignFolder_ShouldNeverBeEmptied()
    {
        WriteConfig();
        var kept = Path.Combine(_destination, DestinationGuard.QuarantineDirectoryName, "kept.adf");
        Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
        File.WriteAllText(kept, "kept");

        var result = await RunAsync("quarantine", "--destination", _destination, "--empty", "--yes");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("is not a destination of RomSieve");
        File.Exists(kept).Should().BeTrue();
        (await RunAsync("quarantine", "--destination", Path.Combine(_temp.Path, "missing"))).ExitCode.Should().Be(ExitCodes.InvalidConfiguration);
    }

    [Theory]
    [InlineData("run", "--purge", "--yes")]
    [InlineData("run", "--execute", "--purge", "--yes", "--append")]
    [InlineData("run", "--report", "report.txt")]
    [InlineData("run", "--miss-list", "missing.xml")]
    [InlineData("run", "--mode", "teleport")]
    [InlineData("run", "--unknown")]
    [InlineData("run", "--profile", "../escape")]
    [InlineData("--lang", "de", "run")]
    [InlineData()]
    public async Task InvalidArguments_ShouldBeRejectedBeforeAnythingIsWritten(params string[] args)
    {
        WriteConfig();
        _temp.WriteText(Path.Combine("destination", "keep.txt"), "existing");

        var result = await RunAsync(args);

        result.ExitCode.Should().Be(ExitCodes.InvalidArguments);
        result.Error.Should().NotBeEmpty();
        DestinationFiles().Should().Equal("keep.txt");
    }

    [Fact]
    public async Task ChdFolderOption_ShouldBeCheckedLikeTheSourceForAMameSort()
    {
        // --chd replaces the folder of the configuration, and a destination containing it is refused
        var (mameSource, dat) = WriteMameSet();
        WriteConfig(s =>
        {
            s.SourceDir = mameSource;
            s.DatFile = dat;
        });
        var chd = Path.Combine(_destination, "chd");
        Directory.CreateDirectory(chd);

        var result = await RunAsync("run", "--chd", chd);

        result.ExitCode.Should().Be(ExitCodes.InvalidConfiguration);
        result.Error.Should().Contain("CHD");
    }

    [Fact]
    public async Task ChdFolder_ShouldBeIgnoredByASortOfAnotherConvention()
    {
        // The CHD folder of the last MAME sort stays in the configuration: a TOSEC sort never reads it
        WriteConfig(s => s.ChdDir = Path.Combine(_destination, "chd"));
        Directory.CreateDirectory(Path.Combine(_destination, "chd"));

        var result = await RunAsync("run");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Error.Should().BeEmpty();
    }

    // A MAME set in its own source, with its machine in a -listxml DAT: the source and the DAT
    private (string Source, string Dat) WriteMameSet()
    {
        var mameSource = _temp.CreateSubdirectory("mame");
        var rom = Encoding.ASCII.GetBytes("ATARI TETRIS");
        using (var zip = System.IO.Compression.ZipFile.Open(Path.Combine(mameSource, "atetris.zip"), System.IO.Compression.ZipArchiveMode.Create))
        {
            using var stream = zip.CreateEntry("atetris.rom").Open();
            stream.Write(rom);
        }

        var dat = _temp.WriteText("mame.xml", $"""
            <?xml version="1.0"?>
            <mame build="0.289 (test)">
            	<machine name="atetris"><description>Tetris (set 1)</description><rom name="atetris.rom" size="{rom.Length}" crc="{Crc32.HashToUInt32(rom):x8}"/><driver status="good"/></machine>
            </mame>
            """);
        return (mameSource, dat);
    }

    [Fact]
    public async Task CategoriesOption_ShouldReplaceTheCategoryFileOfTheConfiguration()
    {
        var (mameSource, dat) = WriteMameSet();
        var categories = _temp.WriteText("catlist.ini", "[Arcade: Puzzle / Drop]\natetris\n");
        WriteConfig(s =>
        {
            s.SourceDir = mameSource;
            s.DatFile = dat;
            s.MameCategoryFile = Path.Combine(_temp.Path, "missing.ini");
            s.MameExcludedGenres = ["Puzzle"];
        });

        var result = await RunAsync("run", "--categories", categories);

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("MAME categories: 1 machines in 1 genres");
        result.Output.Should().Contain("Excluded Puzzle genre");
    }

    [Fact]
    public async Task RatingsOption_ShouldReplaceTheRatingFileOfTheConfiguration()
    {
        var (mameSource, dat) = WriteMameSet();
        var ratings = _temp.WriteText("bestgames.ini", "[80 to 90 (Very Good)]\natetris\n");
        WriteConfig(s =>
        {
            s.SourceDir = mameSource;
            s.DatFile = dat;
            s.MameMinimumRating = 90;
        });

        var result = await RunAsync("run", "--ratings", ratings);

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("MAME ratings: 1 machines rated");
        result.Output.Should().Contain("Rating too low (80 to 90 (Very Good), at least 90)");
    }

    [Fact]
    public async Task DestinationInsideTheSource_ShouldBeRefused()
    {
        WriteConfig(s => s.DestinationDir = Path.Combine(_source, "sorted"));

        var result = await RunAsync("run", "--execute");

        result.ExitCode.Should().Be(ExitCodes.InvalidConfiguration);
        Directory.Exists(Path.Combine(_source, "sorted")).Should().BeFalse();
    }

    [Fact]
    public async Task MissingSourceConfigurationOrProfile_ShouldBeInvalidConfigurations()
    {
        WriteConfig(s => s.SourceDir = Path.Combine(_temp.Path, "missing"));
        (await RunAsync("run")).ExitCode.Should().Be(ExitCodes.InvalidConfiguration);

        (await RunAsync("run", "--config", Path.Combine(_temp.Path, "none.json"))).ExitCode.Should().Be(ExitCodes.InvalidConfiguration);
        (await RunAsync("run", "--profile", "absent")).ExitCode.Should().Be(ExitCodes.InvalidConfiguration);

        var corrupt = _temp.WriteText("corrupt.json", "{ not json");
        (await RunAsync("run", "--config", corrupt)).ExitCode.Should().Be(ExitCodes.InvalidConfiguration);

        WriteConfig(s => s.DatFile = Path.Combine(_temp.Path, "missing.dat"));
        (await RunAsync("run")).ExitCode.Should().Be(ExitCodes.InvalidConfiguration);
    }

    [Fact]
    public async Task WithoutConfiguration_TheSourceAndDestinationMustBeGiven()
    {
        var result = await RunAsync("run");

        result.ExitCode.Should().Be(ExitCodes.InvalidConfiguration);
        result.Error.Should().Contain("--source");

        (await RunAsync("run", "--source", _source, "--destination", _destination)).ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public async Task CancelledRun_ShouldReturnTheCancelledCode()
    {
        WriteConfig();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await new CliApplication(output, error, _configPath).RunAsync(["run", "--execute"], cancellation.Token);

        exitCode.Should().Be(ExitCodes.Cancelled);
        DestinationFiles().Should().BeEmpty();
    }

    // =========================================================================
    // Profiles
    // =========================================================================

    [Fact]
    public async Task Profile_ShouldGiveTheOptionsOfTheRun()
    {
        WriteConfig(s => s.SourceDir = Path.Combine(_temp.Path, "missing"));
        var profilePath = ConfigurationProfiles.GetPath(_configPath, "Zip collection");
        WriteConfig(s => s.TransferMode = TransferMode.Zip, path: profilePath);

        var list = await RunAsync("profiles");
        list.Output.Should().Contain("Zip collection");

        var result = await RunAsync("run", "--profile", "Zip collection", "--execute");

        result.ExitCode.Should().Be(ExitCodes.Success, result.Error);
        result.Output.Should().Contain(profilePath);
        DestinationFiles().Should().Equal("Alpha (1990)(Ocean).zip", "Beta (1991)(Ocean).zip");
    }

    [Fact]
    public async Task ProfilesOfAnotherConfiguration_ShouldBeListed()
    {
        var other = Path.Combine(_temp.CreateSubdirectory("portable"), "config.json");
        WriteConfig(path: ConfigurationProfiles.GetPath(other, "Amiga"));

        (await RunAsync("profiles")).Output.Should().Contain("No profile", "without configuration, the messages are in English");
        (await RunAsync("profiles", "--config", other)).Output.Should().Contain("Amiga");
    }

    // =========================================================================
    // Exports
    // =========================================================================

    [Fact]
    public async Task Exports_ShouldWriteTheReportTheMissListAndTheFixDat()
    {
        WriteConfig(s => s.DatFile = WriteDat());
        var json = Path.Combine(_temp.Path, "report.json");
        var csv = Path.Combine(_temp.Path, "report.csv");
        var missList = Path.Combine(_temp.Path, "missing.txt");
        var fixDat = Path.Combine(_temp.Path, "fix.dat");

        var result = await RunAsync("run", "--report", json, "--miss-list", missList, "--fixdat", fixDat);
        var csvResult = await RunAsync("run", "--report", csv);

        result.ExitCode.Should().Be(ExitCodes.Success, result.Error);
        csvResult.ExitCode.Should().Be(ExitCodes.Success, csvResult.Error);
        result.Output.Should().Contain("DAT: 1 ROMs present out of 2");
        File.ReadAllText(json).Should().Contain(AlphaName);
        File.ReadAllText(csv).Should().Contain(BetaName);
        File.ReadAllText(missList).Should().Contain("Gamma (1992)(Ocean)").And.NotContain(AlphaName);
        File.ReadAllText(fixDat).Should().Contain("fixDat_").And.Contain("Gamma (1992)(Ocean).adf");
    }

    [Fact]
    public async Task Dat_ShouldWriteTheFilteredDatOfTheCatalogWithoutReadingTheSource()
    {
        var datPath = WriteDat();
        WriteConfig(s =>
        {
            s.DatFile = datPath;
            s.SourceDir = Path.Combine(_temp.Path, "no-source");
        });
        var output = Path.Combine(_temp.Path, "filtered.xml");

        var result = await RunAsync("dat", "--output", output);

        result.ExitCode.Should().Be(ExitCodes.Success, result.Error);
        result.Output.Should().Contain($"Filtered DAT of the catalog written: 2 games kept out of 2 ({output})");

        // A game absent from the source is in the catalog all the same
        File.ReadAllText(output).Should().Contain(AlphaName).And.Contain("Gamma (1992)(Ocean).adf").And.Contain("Test set (RomSieve)");
        Directory.Exists(_destination).Should().BeFalse();
    }

    // A folder of DATs of two MAME software lists, each with a software named sonic, and a source with a folder per list
    private string WriteSoftwareLists()
    {
        static string Crc(string text) => Crc32.HashToUInt32(Encoding.UTF8.GetBytes(text)).ToString("x8");
        foreach (var list in new[] { "megadriv", "gamegear" })
        {
            _temp.WriteText(Path.Combine("lists", list + ".xml"), $"""
                <?xml version="1.0"?>
                <datafile>
                  <header><name>{list}</name><description>{list} cartridges</description></header>
                  <machine name="sonic"><description>Sonic The Hedgehog (Europe, USA)</description><year>1991</year><publisher>Sega</publisher><rom name="{list}.ic1" size="{list.Length + 5}" crc="{Crc(list + " rom")}"/></machine>
                </datafile>
                """);

            var folder = _temp.CreateSubdirectory(Path.Combine("softlists", list));
            using var zip = System.IO.Compression.ZipFile.Open(Path.Combine(folder, "sonic.zip"), System.IO.Compression.ZipArchiveMode.Create);
            using var stream = zip.CreateEntry(list + ".ic1").Open();
            stream.Write(Encoding.UTF8.GetBytes(list + " rom"));
        }

        return Path.Combine(_temp.Path, "lists");
    }

    [Fact]
    public async Task Run_WithAFolderOfDats_ShouldSortEachListIntoItsFolder()
    {
        var lists = WriteSoftwareLists();
        WriteConfig(s =>
        {
            s.SourceDir = Path.Combine(_temp.Path, "softlists");
            s.FlatDestination = true;
        });

        var result = await RunAsync("run", "--dat", lists, "--execute");

        result.ExitCode.Should().Be(ExitCodes.Success, result.Error);
        File.Exists(Path.Combine(_destination, "megadriv", "sonic.zip")).Should().BeTrue();
        File.Exists(Path.Combine(_destination, "gamegear", "sonic.zip")).Should().BeTrue();
    }

    [Fact]
    public async Task Dat_WithAFolderOfDats_ShouldWriteADatPerListAtTheRootOfTheOutputFolder()
    {
        var lists = WriteSoftwareLists();
        WriteConfig();
        var folder = Path.Combine(_temp.Path, "out");
        _temp.WriteText(Path.Combine("out", "nes.xml"), "<datafile/>");

        var result = await RunAsync("dat", "--dat", lists, "--output", folder);

        result.ExitCode.Should().Be(ExitCodes.Success, result.Error);
        result.Output.Should().Contain($"2 games kept out of 2 ({folder})");
        Directory.GetFiles(folder).Select(Path.GetFileName).Order().Should().Equal("gamegear.xml", "megadriv.xml", "nes.xml");

        // The DAT the export did not replace is told: a ROM manager that reads the folder reads it too
        result.Output.Should().Contain("1 DATs of the folder").And.Contain("nes.xml");
    }

    [Fact]
    public async Task Dat_WithAFolderOfDats_ShouldRefuseAFileAsOutput()
    {
        var lists = WriteSoftwareLists();
        WriteConfig();
        var output = Path.Combine(_temp.Path, "filtered.xml");

        var result = await RunAsync("dat", "--dat", lists, "--output", output);

        result.ExitCode.Should().Be(ExitCodes.InvalidArguments);
        result.Error.Should().Contain("a DAT per list");
        Path.Exists(output).Should().BeFalse();
    }

    [Fact]
    public async Task Dat_ShouldTakeTheDatOptionAndWriteTheFormatOfTheExtension()
    {
        WriteConfig();
        var output = Path.Combine(_temp.Path, "filtered.dat");

        var result = await RunAsync("dat", "--dat", WriteDat(), "-o", output);

        result.ExitCode.Should().Be(ExitCodes.Success, result.Error);
        File.ReadAllText(output).Should().StartWith("clrmamepro (");
    }

    [Fact]
    public async Task Dat_ShouldNeedADatAndAnOutputOfAKnownFormat()
    {
        WriteConfig();

        var withoutDat = await RunAsync("dat", "--output", Path.Combine(_temp.Path, "filtered.xml"));
        var missingDat = await RunAsync("dat", "--dat", Path.Combine(_temp.Path, "missing.xml"), "--output", Path.Combine(_temp.Path, "filtered.xml"));
        var badExtension = await RunAsync("dat", "--dat", WriteDat(), "--output", Path.Combine(_temp.Path, "filtered.txt"));
        var withoutOutput = await RunAsync("dat", "--dat", WriteDat());

        withoutDat.ExitCode.Should().Be(ExitCodes.InvalidConfiguration);
        withoutDat.Error.Should().Contain("The filtered DAT of the catalog requires a DAT file.");
        missingDat.ExitCode.Should().Be(ExitCodes.InvalidConfiguration);
        badExtension.ExitCode.Should().Be(ExitCodes.InvalidArguments);
        withoutOutput.ExitCode.Should().Be(ExitCodes.InvalidArguments);
        File.Exists(Path.Combine(_temp.Path, "filtered.xml")).Should().BeFalse();
    }

    [Fact]
    public async Task Exports_ShouldNeverReplaceTheDatRead()
    {
        WriteConfig();
        var dat = WriteDat();
        var before = File.ReadAllText(dat);

        // A DAT is recognized by its content: named as a report or a Miss List, it is still read
        var datAsJson = Path.Combine(_temp.Path, "set.json");
        var datAsTxt = Path.Combine(_temp.Path, "set.txt");
        File.Copy(dat, datAsJson);
        File.Copy(dat, datAsTxt);

        var catalog = await RunAsync("dat", "--dat", dat, "--output", dat);
        var report = await RunAsync("run", "--dat", datAsJson, "--report", datAsJson);
        var missList = await RunAsync("run", "--dat", datAsTxt, "--miss-list", datAsTxt);
        var fixDat = await RunAsync("run", "--dat", dat, "--fixdat", dat);

        catalog.ExitCode.Should().Be(ExitCodes.InvalidArguments, catalog.Output);
        catalog.Error.Should().Contain("is the DAT RomSieve read");
        foreach (var run in new[] { report, missList, fixDat })
        {
            run.ExitCode.Should().Be(ExitCodes.CompletedWithErrors, run.Output);
            run.Error.Should().Contain("is the DAT RomSieve read");
        }

        File.ReadAllText(dat).Should().Be(before);
        File.ReadAllText(datAsJson).Should().Be(before);
        File.ReadAllText(datAsTxt).Should().Be(before);
    }

    [Fact]
    public async Task MissListWithoutDat_ShouldBeRejected()
    {
        WriteConfig();

        var result = await RunAsync("run", "--miss-list", Path.Combine(_temp.Path, "missing.txt"));

        result.ExitCode.Should().Be(ExitCodes.InvalidArguments);
    }

    // =========================================================================
    // Language and help
    // =========================================================================

    [Fact]
    public async Task Messages_ShouldFollowTheLanguageOfTheInterfaceOrLang()
    {
        WriteConfig(locale: "fr");

        (await RunAsync("run")).Output.Should().Contain("Bilan : 2 fichiers analysés");
        (await RunAsync("run", "--lang", "en")).Output.Should().Contain("Summary: 2 files scanned");
        (await RunAsync("--lang=en", "run")).Output.Should().Contain("Summary:");
        (await RunAsync("run", "--lang", "es")).Output.Should().Contain("Resumen: 2 archivos analizados");
        (await RunAsync("run", "--help", "--lang", "es")).Output.Should().Contain("Ejecuta realmente la clasificación");
    }

    [Fact]
    public async Task Help_ShouldBeTranslated()
    {
        var french = await RunAsync("--help", "--lang", "fr");
        var english = await RunAsync("run", "--help", "--lang", "en");

        french.ExitCode.Should().Be(ExitCodes.Success);
        french.Output.Should().Contain("Simule le tri").And.Contain("Affiche l'aide");
        english.Output.Should().Contain("--execute").And.Contain("Actually runs the sort");
    }

    [Fact]
    public async Task QuietRun_ShouldOnlyShowTheSummary()
    {
        WriteConfig();

        var result = await RunAsync("run", "--quiet");

        result.Output.Should().NotContain("[INFO]").And.NotContain("Options read").And.Contain("Summary:");
    }

    [Theory]
    [InlineData(new[] { "--lang", "en" }, "en")]
    [InlineData(new[] { "run", "--lang=fr" }, "fr")]
    [InlineData(new[] { "--lang:en", "run" }, "en")]
    [InlineData(new[] { "run", "--lang" }, null)]
    [InlineData(new[] { "run" }, null)]
    public void FindOptionValue_ShouldReadTheThreeSyntaxes(string[] args, string? expected) =>
        CliApplication.FindOptionValue(args, "--lang").Should().Be(expected);

    [Fact]
    public async Task ConfigurationOfALaterVersion_ShouldBeReadWithAWarningEvenInQuietMode()
    {
        WriteConfig();
        var data = JsonSerializer.Deserialize<Dictionary<string, object?>>(File.ReadAllText(_configPath))!;
        data[ConfigSchema.VersionKey] = ConfigSchema.CurrentVersion + 1;
        File.WriteAllText(_configPath, JsonSerializer.Serialize(data));

        var result = await RunAsync("run", "--quiet");

        result.ExitCode.Should().Be(ExitCodes.Success, result.Output + result.Error);
        result.Output.Should().Contain($"[WARN] {_configPath} was written by a newer version of RomSieve")
            .And.Contain($"format {ConfigSchema.CurrentVersion + 1}");
    }

    [Fact]
    public async Task ConfigurationOfTheCurrentVersion_ShouldBeReadWithoutWarning()
    {
        WriteConfig();

        var result = await RunAsync("run");

        result.ExitCode.Should().Be(ExitCodes.Success, result.Output + result.Error);
        result.Output.Should().NotContain("newer version");
    }

    [Fact]
    public async Task SortWithoutJournal_ShouldStopBeforeWritingWithExitCode4()
    {
        WriteConfig(s => s.TransferMode = TransferMode.Move);

        // A file where the folder of the journals should be: the journal cannot be written
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_configPath)!, "journals"), "not a folder");

        var result = await RunAsync("run", "--execute", "--quiet");

        result.ExitCode.Should().Be(ExitCodes.CompletedWithErrors, result.Output + result.Error);
        result.Output.Should().Contain("[ERROR] Sort stopped before writing anything");
        DestinationFiles().Should().BeEmpty();
        File.Exists(Path.Combine(_source, AlphaName)).Should().BeTrue("a move without a journal could not be undone");
    }

    [Fact]
    public async Task UnexpectedError_ShouldWriteACrashReportNextToTheConfiguration()
    {
        WriteConfig();
        using var output = new FailingWriter();
        using var error = new StringWriter();

        var exitCode = await new CliApplication(output, error, _configPath).RunAsync(["run"]);

        exitCode.Should().Be(ExitCodes.UnexpectedError, error.ToString());
        var crashDirectory = Path.Combine(Path.GetDirectoryName(_configPath)!, "crashes");
        var report = Directory.GetFiles(crashDirectory).Should().ContainSingle().Subject;
        File.ReadAllText(report).Should().Contain("Origin: Command line: run")
            .And.Contain("System.IO.IOException: standard output closed");
        error.ToString().Should().Contain("Error: standard output closed")
            .And.Contain($"Crash report saved in: {report}");
    }

    /// <summary>Standard output that fails at the first write, as a closed pipe would.</summary>
    private sealed class FailingWriter : StringWriter
    {
        public override void Write(char value) => throw new IOException("standard output closed");

        public override void Write(string? value) => throw new IOException("standard output closed");

        public override void WriteLine(string? value) => throw new IOException("standard output closed");
    }
}
