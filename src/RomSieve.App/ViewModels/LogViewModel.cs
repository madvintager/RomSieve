using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;

namespace RomSieve.App.ViewModels;

/// <summary>
/// Execution log panel: bounded list of structured entries, translated again on a language switch. While a run writes
/// its log to a file (<see cref="RunFile"/>), every entry also goes there, beyond the bound.
/// </summary>
public sealed partial class LogViewModel(ILocalizationService localization) : ViewModelBase
{
    /// <summary>Maximum number of entries kept to prevent memory bloat and interface lag.</summary>
    public const int MaxEntries = 2000;

    public ObservableCollection<LogEntryViewModel> Entries { get; } = [];

    /// <summary>Log file of the running operation, which receives every entry appended; <c>null</c> between runs.</summary>
    public RunLogFile? RunFile { get; set; }

    /// <summary>Full log text (concatenation of the entries).</summary>
    public string Text => string.Join(Environment.NewLine, Entries.Select(e => e.FormattedLine));

    public void Append(LogLevel level, LocalizedMessage message)
    {
        while (Entries.Count >= MaxEntries)
        {
            Entries.RemoveAt(0);
        }

        var entry = LogEntryViewModel.Create(level, message, localization);
        RunFile?.WriteLine(entry.FormattedLine);
        Entries.Add(entry);
        OnPropertyChanged(nameof(Text));
    }

    /// <summary>Appends a batch of messages, dropping the oldest entries beyond <see cref="MaxEntries"/>.</summary>
    public void AppendBatch(IReadOnlyList<LogMessage> batch)
    {
        if (batch.Count == 0)
        {
            return;
        }

        // The file keeps the whole batch, the panel its last entries
        if (RunFile is { } file)
        {
            foreach (var msg in batch)
            {
                file.WriteLine(LogEntryViewModel.Create(msg.Level, msg.Message, localization).FormattedLine);
            }
        }

        IEnumerable<LogMessage> toAdd = batch;
        if (batch.Count >= MaxEntries)
        {
            Entries.Clear();
            toAdd = batch.TakeLast(MaxEntries);
        }
        else
        {
            var overflow = (Entries.Count + batch.Count) - MaxEntries;
            for (var i = 0; i < overflow && Entries.Count > 0; i++)
            {
                Entries.RemoveAt(0);
            }
        }

        foreach (var msg in toAdd)
        {
            Entries.Add(LogEntryViewModel.Create(msg.Level, msg.Message, localization));
        }

        OnPropertyChanged(nameof(Text));
    }

    [RelayCommand]
    public void Clear()
    {
        Entries.Clear();
        OnPropertyChanged(nameof(Text));
    }

    /// <summary>Translates every entry again from its structured message.</summary>
    public void RefreshTranslations()
    {
        for (var i = 0; i < Entries.Count; i++)
        {
            Entries[i] = Entries[i].Retranslate(localization);
        }

        OnPropertyChanged(nameof(Text));
    }
}
