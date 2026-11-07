using System.Text.Json;
using System.Text.Json.Serialization;
using RomSieve.App;
using RomSieve.App.Abstractions;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// The command line reads <c>config.json</c> with <see cref="SortSettingsSerializer"/> alone: it must obtain the same
/// <see cref="Core.Models.SortConfig"/> and save the same options as the interface.
/// </summary>
public sealed class SettingsParityTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { Converters = { new JsonStringEnumConverter() } };

    public static TheoryData<string> Configurations =>
    [
        "{}",
        """{ "one_game_one_rom": true, "region_priority_mode": "country_first", "priority_languages": ["FR", "en", "fr"], "priority_countries": ["uk", "Asia"], "exclude_modifications": false }""",
        """
        { "source_dir": " C:/src ", "dat_file": " x.dat ", "naming_convention": "nointro", "transfer_mode": "move", "hashing_parallelism": 3,
          "flat_destination": true, "pattern": "", "nointro_exclude_hack": false, "filter_languages": false, "one_game_one_rom": true,
          "tiebreak_order_tosec": ["version", "bogus", "country", "language"], "preferred_video": "pal", "prefer_monolingual": false }
        """,
        """{ "recursive": "false", "hashing_parallelism": "4", "verify_copies": null, "transfer_mode": 3, "pattern": "  ", "priority_languages": "fr", "tiebreak_order_tosec": [] }""",
        """{ "filter_languages": false, "priority_languages": ["de"], "region_priority_mode": "country_first", "tiebreak_order_tosec": ["language", "country"] }""",
        """{ "dat_recursive": true, "excluded_dats": [" Nintendo - Game Boy ", "nintendo - game boy", "", "Sega - Mega Drive"], "folder_per_dat": false, "dat_folder_mirror": true }""",
        """{ "dat_recursive": "yes", "excluded_dats": "Nintendo - Game Boy", "folder_per_dat": null, "dat_folder_mirror": 1 }"""
    ];

    [Theory]
    [MemberData(nameof(Configurations))]
    public async Task CoreSerializer_ShouldMatchTheInterface(string json)
    {
        using var tmp = new TempDirectoryFixture();
        var path = tmp.WriteText("config.json", json);
        var data = await new ConfigurationService().LoadConfigFromFileAsync(path);
        data.Should().NotBeNull();

        var settings = SortSettingsSerializer.Read(data!);
        var vm = TestMainViewModel.Create(new HeadlessDialogService(), path);

        JsonSerializer.Serialize(SortSettingsSerializer.ToSortConfig(settings), JsonOptions)
            .Should().Be(JsonSerializer.Serialize(vm.BuildConfigUnchecked(), JsonOptions));

        var written = new Dictionary<string, object?>(StringComparer.Ordinal);
        SortSettingsSerializer.Write(settings, written);
        var saved = vm.ToConfigurationDictionary();
        foreach (var (key, value) in written)
        {
            JsonSerializer.Serialize(saved[key]).Should().Be(JsonSerializer.Serialize(value), key);
        }
    }
}
