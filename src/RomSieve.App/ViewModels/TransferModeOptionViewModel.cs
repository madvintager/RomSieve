using CommunityToolkit.Mvvm.ComponentModel;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.App.ViewModels;

/// <summary>
/// Entry of the transfer mode list: its name, a sentence saying what it does to the source and where it can write,
/// and the full details for its tooltip.
/// </summary>
public sealed partial class TransferModeOptionViewModel : ViewModelBase
{
    private readonly ILocalizationService _localization;
    private readonly string _labelKey;
    private readonly string _descriptionKey;
    private readonly string _detailsKey;

    public TransferModeOptionViewModel(ILocalizationService localization, TransferMode value, string labelKey, string descriptionKey, string detailsKey)
    {
        _localization = localization;
        Value = value;
        _labelKey = labelKey;
        _descriptionKey = descriptionKey;
        _detailsKey = detailsKey;
        _label = localization.Tr(labelKey);
        _description = localization.Tr(descriptionKey);
        _details = localization.Tr(detailsKey);
    }

    public TransferMode Value { get; }

    [ObservableProperty]
    private string _label;

    [ObservableProperty]
    private string _description;

    [ObservableProperty]
    private string _details;

    public void RefreshTranslations()
    {
        Label = _localization.Tr(_labelKey);
        Description = _localization.Tr(_descriptionKey);
        Details = _localization.Tr(_detailsKey);
    }
}
