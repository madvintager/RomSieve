using System.IO.Hashing;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RomSieve.App.ViewModels;
using RomSieve.App.Views;
using RomSieve.Core.Models;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// Screenshots of the user guide and of the README (docs/images), taken from a real simulation of a small Amiga TOSEC
/// collection with its DAT, the France preset and 1G1R, and of the preferences window. Only with
/// ROMSIEVE_DOC_CAPTURE_DIR (output folder, usually docs/images) and ROMSIEVE_HEADLESS_SKIA=1 (rendering). English
/// images go to the folder, French ones to its fr/ subfolder; each one exists in the light and the dark theme (suffix
/// _dark).
/// </summary>
public sealed partial class GuiTests
{
    private const double DocScale = 2;

    /// <summary>Files of the sample collection; the ones marked absent are only listed in the DAT.</summary>
    private static readonly (string Name, bool Present)[] DocCollection =
    [
        ("Another World (1991)(Delphine)(FR)[!].adf", true),
        ("Another World (1991)(Delphine)(GB)[!].adf", true),
        ("Another World (1991)(Delphine)(GB)[cr Quartex].adf", true),
        ("Cannon Fodder (1993)(Virgin)(GB)(Disk 1 of 3)[!].adf", true),
        ("Cannon Fodder (1993)(Virgin)(GB)(Disk 2 of 3)[!].adf", true),
        ("Cannon Fodder (1993)(Virgin)(GB)(Disk 3 of 3)[!].adf", true),
        ("Dune (1992)(Cryo)(FR)[!].adf", true),
        ("Dune (1992)(Cryo)(GB)[!].adf", true),
        ("Lemmings (1991)(Psygnosis)(DE)[a].adf", true),
        ("Lemmings (1991)(Psygnosis)(FR)[!].adf", true),
        ("Lemmings (1991)(Psygnosis)(GB)[!].adf", true),
        ("Lionheart (1993)(Thalion)(GB)[!].adf", false),
        ("Pinball Dreams (1992)(21st Century)(GB)[!].adf", true),
        ("Pinball Dreams (1992)(21st Century)(GB)[h Sharks].adf", true),
        ("Prince of Persia (1990)(Broderbund)(FR)[!].adf", true),
        ("Prince of Persia (1990)(Broderbund)(US)[!].adf", true),
        ("Secret of Monkey Island, The (1990)(Lucasfilm)(FR)(Disk 1 of 4)[!].adf", true),
        ("Secret of Monkey Island, The (1990)(Lucasfilm)(FR)(Disk 2 of 4)[!].adf", true),
        ("Secret of Monkey Island, The (1990)(Lucasfilm)(FR)(Disk 3 of 4)[!].adf", true),
        ("Secret of Monkey Island, The (1990)(Lucasfilm)(FR)(Disk 4 of 4)[!].adf", true),
        ("Sensible Soccer v1.1 (1992)(Renegade)(GB)[!].adf", true),
        ("Shadow of the Beast (1989)(Psygnosis)(GB)(Disk 1 of 2)[!].adf", true),
        ("Shadow of the Beast (1989)(Psygnosis)(GB)(Disk 2 of 2)[!].adf", true),
        ("Speedball 2 - Brutal Deluxe (1990)(Bitmap Brothers)(GB)[!].adf", true),
        ("Speedball 2 - Brutal Deluxe (1990)(Bitmap Brothers)(GB)[t +2 Skid Row].adf", true),
        ("Turrican II - The Final Fight (1991)(Rainbow Arts)(DE)[!].adf", true),
        ("Turrican II - The Final Fight (1991)(Rainbow Arts)(GB)[b].adf", true),
        ("Zool (1992)(Gremlin)(GB)[!].adf", false)
    ];

    [Fact]
    public async Task DocumentationScreenshots_WhenRequested()
    {
        var outputDir = Environment.GetEnvironmentVariable("ROMSIEVE_DOC_CAPTURE_DIR");
        if (string.IsNullOrEmpty(outputDir) || Environment.GetEnvironmentVariable("ROMSIEVE_HEADLESS_SKIA") != "1")
        {
            return;
        }

        var (source, destination, dat) = CreateDocCollection();
        MainViewModel? vm = null;
        MainWindow? window = null;
        SortConfig? config = null;

        await RunOnUiThreadAsync(() =>
        {
            vm = CreateViewModel();
            vm.Directories.SourceDir = source;
            vm.Directories.DestinationDir = destination;
            vm.Directories.DatFilePath = dat;
            vm.Directories.Pattern = "{publisher}/{title}/";
            vm.Priorities.Apply1G1RPreset("france");
            config = vm.BuildConfigUnchecked();
        });

        // The simulation runs out of the UI thread and without any open window, then its result is shown
        // (see the pitfall of the Headless tests)
        var result = await new CoreTestServices("en").Processor.ExecuteAsync(config!, isDryRun: true);

        await RunOnUiThreadAsync(() =>
        {
            window = new MainWindow(vm!) { Width = 1280, Height = 960 };
            window.Show();
            try
            {
                vm!.Execution.PlayRunForTests(config!, isDryRun: true, result);
                foreach (var (locale, folder) in new[] { ("en", outputDir), ("fr", Path.Combine(outputDir, "fr")) })
                {
                    vm.UiLocale = locale;
                    ShowSamplePaths(vm, dat, source);
                    // The sample paths replace the temporary folders the simulation ran with: the summary of the sort
                    // shows them as if the simulation had used them. No row is selected, since the detail panel would
                    // show the full temporary paths
                    vm.Execution.AdoptCurrentSettingsAsSimulatedForTests();
                    vm.SelectedPreviewItem = null;
                    Directory.CreateDirectory(folder);
                    foreach (var theme in new[] { UiTheme.Light, UiTheme.Dark })
                    {
                        vm.UiTheme = theme;
                        var suffix = theme == UiTheme.Dark ? "_dark" : string.Empty;

                        vm.Navigation.ShowResults();
                        HideSampleSourceIndicator(vm);
                        Settle(window!);
                        var frame = RenderWindow(window!);
                        Save(frame, null, Path.Combine(folder, $"window{suffix}.png"));
                        Save(frame, BoundsIn(window!, PreviewTabs(window!)), Path.Combine(folder, $"simulation_preview{suffix}.png"));
                        Save(frame, BoundsIn(window!, StatsBanner(window!)), Path.Combine(folder, $"stats_banner{suffix}.png"));

                        // View grouped by game, with a version chosen by hand in the first game. The game is
                        // recalculated with the destination of the simulation, under which its targets are shown
                        vm.Directories.DestinationDir = destination;
                        vm.PreviewTable.IsGroupedByGame = true;
                        vm.PreviewTable.Force1G1RWinner(vm.PreviewTable.AllPreviewItems.Single(r => r.SourceName == "Another World (1991)(Delphine)(GB)[!].adf"));
                        vm.SelectedPreviewItem = null;
                        Settle(window!);
                        frame = RenderWindow(window!);
                        Save(frame, BoundsIn(window!, PreviewTabs(window!)), Path.Combine(folder, $"simulation_grouped{suffix}.png"));
                        vm.PreviewTable.ClearAll1G1ROverrides();
                        vm.PreviewTable.IsGroupedByGame = false;
                        ShowSamplePaths(vm, dat, source);
                        vm.Execution.AdoptCurrentSettingsAsSimulatedForTests();

                        // The pages of the settings steps, down to the end of their content
                        foreach (var (step, image) in new[]
                                 {
                                     (WorkflowStep.Collection, "collection"),
                                     (WorkflowStep.Filters, "filters"),
                                     (WorkflowStep.Languages, "1g1r_settings"),
                                     (WorkflowStep.Output, "output_settings")
                                 })
                        {
                            vm.Navigation.CurrentStep = step;
                            HideSampleSourceIndicator(vm);
                            Settle(window!);
                            frame = RenderWindow(window!);
                            Save(frame, StepPageBounds(window!, step), Path.Combine(folder, $"{image}{suffix}.png"));
                        }

                        var preferences = new PreferencesWindow { DataContext = vm.Preferences };
                        preferences.Show();
                        try
                        {
                            Settle(preferences);
                            Save(RenderWindow(preferences), null, Path.Combine(folder, $"preferences{suffix}.png"));
                        }
                        finally
                        {
                            preferences.Close();
                        }
                    }
                }
            }
            finally
            {
                MainWindow.ApplyTheme(UiTheme.Light);
                window!.Close();
            }
        });
    }

    /// <summary>Writes the sample collection, a duplicate and a non-conforming file, and the DAT of the collection.</summary>
    private (string Source, string Destination, string Dat) CreateDocCollection()
    {
        var root = Path.Combine(_tempDir, "doc");
        var source = Path.Combine(root, "Amiga (unsorted)");
        Directory.CreateDirectory(Path.Combine(source, "backup"));
        var games = new StringBuilder();
        // As in TOSEC DATs, one game per file, except the disks of a multi-disk game
        foreach (var group in DocCollection.GroupBy(f => DiskLabel().Replace(f.Name, string.Empty).Replace(".adf", string.Empty, StringComparison.Ordinal)))
        {
            var game = System.Security.SecurityElement.Escape(group.Key);
            games.Append($"  <game name=\"{game}\"><description>{game}</description>");
            foreach (var (name, present) in group)
            {
                var data = AdfImage(name);
                if (present)
                {
                    File.WriteAllBytes(Path.Combine(source, name), data);
                }

                games.Append($"<rom name=\"{System.Security.SecurityElement.Escape(name)}\" size=\"{data.Length}\" crc=\"{Crc32.HashToUInt32(data):x8}\"/>");
            }

            games.AppendLine("</game>");
        }

        File.Copy(Path.Combine(source, "Lemmings (1991)(Psygnosis)(FR)[!].adf"), Path.Combine(source, "backup", "Lemmings (1991)(Psygnosis)(FR)[!].adf"));
        File.WriteAllText(Path.Combine(source, "readme.txt"), "Downloaded from an old BBS");

        var dat = Path.Combine(root, "Commodore Amiga - Games (TOSEC).dat");
        File.WriteAllText(dat, $"""
            <?xml version="1.0"?>
            <datafile>
              <header><name>Commodore Amiga - Games</name><description>Commodore Amiga - Games (TOSEC)</description><version>2025-06-01</version><homepage>TOSEC</homepage></header>
            {games}</datafile>
            """);
        return (source, Path.Combine(root, "Amiga (sorted)"), dat);
    }

    /// <summary>A file of the size of an Amiga floppy disk image (880 KB), with a content of its own.</summary>
    private static byte[] AdfImage(string name)
    {
        var data = new byte[901_120];
        var seed = Encoding.UTF8.GetBytes(name);
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(seed[i % seed.Length] ^ (i / seed.Length));
        }

        return data;
    }

    /// <summary>
    /// Shows sample paths instead of the temporary folders of the test (which hold the name of the user), keeping the
    /// summary of the DAT read from the real file in the current language.
    /// </summary>
    private static void ShowSamplePaths(MainViewModel vm, string dat, string source)
    {
        var directories = vm.Directories;
        directories.DatFilePath = dat;

        // The summary comes from a read of the whole DAT off the UI thread, which ends on the UI thread
        while (!directories.DatLoadTask.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
        }

        var (info, tone, detected) = (directories.DatInfoText, directories.DatInfoTone, directories.DetectedDatConvention);
        directories.SourceDir = @"D:\Retro\Amiga\Unsorted";
        directories.DestinationDir = @"D:\Retro\Amiga\Sorted";
        directories.DatFilePath = @"D:\Retro\DAT\Commodore Amiga - Games (TOSEC).dat";
        directories.DatInfoText = info;
        directories.DatInfoTone = tone;
        directories.UpdateConventionBadge(detected);

        // The pattern preview shows the files of the sample collection, at the sample destination, with the
        // separators of its Windows paths
        directories.Preview.ShowSourceFilesForTests(source);
        directories.Preview.Examples.ReplaceAll(directories.Preview.Examples
            .Select(e => e with { TargetPath = e.TargetPath.Replace('/', '\\') })
            .ToList());

        HideSampleSourceIndicator(vm);
    }

    /// <summary>
    /// The sample source does not exist on this machine: the collection step would say so. Each change of step checks
    /// the source again, hence a call after each of them.
    /// </summary>
    private static void HideSampleSourceIndicator(MainViewModel vm) =>
        vm.Navigation.CollectionEntry.SetIndicator(StepIndicatorTone.None, string.Empty);

    [GeneratedRegex(@"\(Disk \d+ of \d+\)")]
    private static partial Regex DiskLabel();

    private static Control PreviewTabs(Window window) =>
        window.FindControl<AutoCompleteBox>("EditSearchPreview")!.FindAncestorOfType<TabControl>()!;

    private static Control StatsBanner(Window window) =>
        window.FindControl<ToggleButton>("ChipTotal")!.FindAncestorOfType<Border>()!;

    /// <summary>
    /// The page of a settings step, with the navigation on its left, cut below the end of its content (the languages
    /// step fills its page).
    /// </summary>
    private static Rect StepPageBounds(Window window, WorkflowStep step)
    {
        var page = window.FindControl<Border>($"Page{step}")!;
        var navigation = BoundsIn(window, window.FindControl<StackPanel>("NavigationPanel")!);
        var navigationHeight = BoundsIn(window, window.FindControl<ContentControl>("NavResults")!).Bottom - navigation.Y + 8;
        var bounds = BoundsIn(window, page);
        var height = page.Child is ScrollViewer { Content: Control content }
            ? content.Bounds.Height + page.Padding.Top + page.Padding.Bottom + 8
            // The languages step fills its page, its 1G1R card at the bottom: the whole page
            : bounds.Height;
        height = Math.Min(bounds.Height, Math.Max(height, navigationHeight));
        return new Rect(navigation.X, bounds.Y, bounds.Right - navigation.X, height);
    }

    private static Rect BoundsIn(Window window, Control control) =>
        new(control.TranslatePoint(default, window) ?? default, control.Bounds.Size);

    /// <summary>The whole window, at twice its size.</summary>
    private static RenderTargetBitmap RenderWindow(Window window)
    {
        var size = window.ClientSize;
        var bitmap = new RenderTargetBitmap(
            new PixelSize((int)(size.Width * DocScale), (int)(size.Height * DocScale)),
            new Vector(96 * DocScale, 96 * DocScale));
        bitmap.Render(window);
        return bitmap;
    }

    /// <summary>Saves the frame, or the region of it given in window coordinates.</summary>
    private static void Save(RenderTargetBitmap frame, Rect? region, string path)
    {
        if (region is not { } rect)
        {
            frame.Save(path);
            return;
        }

        using var crop = new RenderTargetBitmap(
            new PixelSize((int)(rect.Width * DocScale), (int)(rect.Height * DocScale)),
            new Vector(96 * DocScale, 96 * DocScale));
        using (var context = crop.CreateDrawingContext())
        {
            // The source rectangle is in pixels of the frame, the destination one in units of the crop
            var source = new Rect(rect.X * DocScale, rect.Y * DocScale, rect.Width * DocScale, rect.Height * DocScale);
            context.DrawImage(frame, source, new Rect(rect.Size));
        }

        crop.Save(path);
    }
}
