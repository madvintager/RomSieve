using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using RomSieve.Core.Models;
using RomSieve.Core.Tests.Fixtures;
using Xunit;

namespace RomSieve.Core.Tests;

/// <summary>
/// Reports of a run (JSON and CSV), written as they go rather than built in memory: the same text as the serialization
/// of the whole report, and no partial file when the export stops.
/// </summary>
public sealed partial class ReportExportTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("en");

    public void Dispose() => _temp.Dispose();

    [GeneratedRegex("\"timestamp\": \"[^\"]*\"")]
    private static partial Regex TimestampRegex();

    private async Task<SortExecutionResult> SimulateAsync()
    {
        _temp.WriteText(Path.Combine("src", "Évasion (1990)(Ocean).adf"), "AAAA");
        _temp.WriteText(Path.Combine("src", "Game; B's (1991)(Ocean)[cr].adf"), "BBBBBB");
        _temp.WriteText(Path.Combine("src", "readme & notes.txt"), "?");
        return await _services.Processor.ExecuteAsync(
            new SortConfig { SourceDir = _temp.GetFilePath("src"), DestinationDir = _temp.GetFilePath("dst") }, isDryRun: true);
    }

    [Fact]
    public async Task JsonReport_ShouldBeTheSerializationOfTheWholeReport()
    {
        var result = await SimulateAsync();
        var path = _temp.GetFilePath("report.json");

        await _services.Processor.ExportReportToJsonAsync(result.Items, result.Stats, path);

        var written = File.ReadAllText(path, Encoding.UTF8);
        TimestampRegex().Replace(written, "\"timestamp\": \"\"")
            .Should().Be(TimestampRegex().Replace(SerializeWholeReport(result), "\"timestamp\": \"\""));
        File.ReadAllBytes(path).Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, "the JSON report has no byte order mark");
        using var document = JsonDocument.Parse(written);
        document.RootElement.GetProperty("files").GetArrayLength().Should().Be(3);
    }

    [Fact]
    public async Task CsvReport_ShouldHaveAHeaderAndALinePerFile()
    {
        var result = await SimulateAsync();
        var path = _temp.GetFilePath("report.csv");

        await _services.Processor.ExportReportToCsvAsync(result.Items, path);

        File.ReadAllBytes(path).Take(3).Should().Equal(new byte[] { 0xEF, 0xBB, 0xBF }, "a spreadsheet reads the UTF-8 of a CSV by its byte order mark");
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        lines.Should().HaveCount(4);
        lines[0].Should().StartWith("source_file;archive_member;source_path;crc32;size_bytes");
        lines.Should().Contain(line => line.StartsWith("Évasion (1990)(Ocean).adf;", StringComparison.Ordinal));
        lines.Should().Contain(line => line.StartsWith("\"Game; B's (1991)(Ocean)[cr].adf\";", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(".json")]
    [InlineData(".csv")]
    public async Task StoppedReport_ShouldLeaveNoPartialFile(string extension)
    {
        var result = await SimulateAsync();
        var path = _temp.GetFilePath("report" + extension);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => extension == ".json"
            ? _services.Processor.ExportReportToJsonAsync(result.Items, result.Stats, path, cts.Token)
            : _services.Processor.ExportReportToCsvAsync(result.Items, path, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(path).Should().BeFalse();
    }

    // The report as the previous versions built it in memory, then serialized at once
    private string SerializeWholeReport(SortExecutionResult result)
    {
        var stats = result.Stats;
        var summary = new Dictionary<string, object?>
        {
            ["total_scanned"] = stats.TotalScanned,
            ["total_bytes"] = stats.TotalBytes,
            ["valid_count"] = stats.ValidCount,
            ["valid_bytes"] = stats.ValidBytes,
            ["excluded_count"] = stats.ExcludedCount,
            ["excluded_bytes"] = stats.ExcludedBytes,
            ["unmatched_count"] = stats.UnmatchedCount,
            ["unmatched_bytes"] = stats.UnmatchedBytes,
            ["duplicate_count"] = stats.DuplicateCount,
            ["error_count"] = stats.ErrorCount,
            ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
        };
        var files = result.Items.Select(item => new Dictionary<string, object?>
        {
            ["source_file"] = Path.GetFileName(item.SourcePath),
            ["archive_member"] = item.ArchiveMember ?? string.Empty,
            ["source_path"] = item.SourcePath,
            ["crc32"] = item.Crc32,
            ["size_bytes"] = item.FileSize,
            ["is_tosec_conform"] = item.Parsed.IsTosecConform,
            ["title"] = item.Parsed.Title,
            ["version"] = item.Parsed.Version,
            ["publisher"] = item.Parsed.Publisher,
            ["date"] = item.Parsed.Date,
            ["system"] = item.Parsed.System ?? string.Empty,
            ["video"] = item.Parsed.Video ?? string.Empty,
            ["country"] = item.Parsed.Country ?? string.Empty,
            ["language"] = item.Parsed.Language ?? string.Empty,
            ["dump_status"] = item.Parsed.DumpStatusSummary,
            ["status"] = item.Status.ToValueString(),
            ["status_reason"] = _services.I18n.Format(item.StatusReason),
            ["is_1g1r_winner"] = item.Is1G1RWinner,
            ["winner_reference"] = item.WinnerReference ?? string.Empty,
            ["target_path"] = item.FinalTargetPath ?? item.TargetPath ?? string.Empty,
            ["error_message"] = item.ErrorMessage ?? string.Empty
        }).ToList();

        return JsonSerializer.Serialize(
            new Dictionary<string, object?> { ["summary"] = summary, ["files"] = files },
            new JsonSerializerOptions { WriteIndented = true });
    }
}
