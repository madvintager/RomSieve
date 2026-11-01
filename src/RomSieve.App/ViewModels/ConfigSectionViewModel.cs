using CommunityToolkit.Mvvm.ComponentModel;
using RomSieve.Core.Models;

namespace RomSieve.App.ViewModels;

/// <summary>
/// Section of the sort configuration shown in one tab: it owns its options and exchanges them with the
/// <see cref="SortSettings"/>, which the Core reads from and writes to <c>config.json</c> and turns into a <see cref="SortConfig"/>.
/// </summary>
public abstract partial class ConfigSectionViewModel : ViewModelBase
{
    /// <summary>Whether an operation is running, during which the options cannot be edited.</summary>
    [ObservableProperty]
    private bool _isRunning;

    partial void OnIsRunningChanged(bool value) => OnRunningStateChanged();

    /// <summary>Lets a section refresh its enabled states when an operation starts or ends.</summary>
    protected virtual void OnRunningStateChanged()
    {
    }

    /// <summary>Copies the options of the section into the settings.</summary>
    public abstract void WriteTo(SortSettings settings);

    /// <summary>Shows the options of the section from the settings.</summary>
    public abstract void ReadFrom(SortSettings settings);

    /// <summary>Translates the labels of the section again after a language switch.</summary>
    public virtual void RefreshTranslations()
    {
    }
}
