using CommunityToolkit.Mvvm.ComponentModel;
using SoundRecorder.Infrastructure.Models;

namespace SoundRecorder.Desktop;

public sealed partial class ModelFamilyViewModel : ObservableObject
{
    public ModelFamilyViewModel(IReadOnlyList<ModelCatalogEntry> variants)
    {
        Variants = variants;
        Family = variants[0].Family;
        Languages = string.Join(", ", variants[0].Languages);
        selectedVariant = variants[0];
    }

    public string Family { get; }
    public string Languages { get; }
    public IReadOnlyList<ModelCatalogEntry> Variants { get; }

    [ObservableProperty]
    private ModelCatalogEntry selectedVariant;
}
