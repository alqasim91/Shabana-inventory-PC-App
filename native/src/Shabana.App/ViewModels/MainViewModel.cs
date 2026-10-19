using CommunityToolkit.Mvvm.ComponentModel;
using Shabana.Core.Resources;

namespace Shabana.App.ViewModels;

/// <summary>Placeholder shell view-model: proves CommunityToolkit.Mvvm generators and Labels work under -warnaserror.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private string _heading = Labels.OrgName;
}
