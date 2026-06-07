using CommunityToolkit.Mvvm.ComponentModel;

namespace HomeLab.NativeClient.ViewModels;

/// <summary>
/// 全ViewModelの基底クラス
/// </summary>
public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;
}
