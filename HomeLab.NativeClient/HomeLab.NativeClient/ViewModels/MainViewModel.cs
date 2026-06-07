using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomeLab.NativeClient.Services;

namespace HomeLab.NativeClient.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly AuthService _authService;

    [ObservableProperty]
    private ViewModelBase _currentView = null!;

    [ObservableProperty]
    private string _greeting = "HomeLab SESAME Controller";

    [ObservableProperty]
    private bool _isLoggedIn;

    public MainViewModel(AuthService authService)
    {
        _authService = authService;
        UpdateGreeting();
    }

    partial void OnIsLoggedInChanged(bool value)
    {
        UpdateGreeting();
    }

    private void UpdateGreeting()
    {
        Greeting = IsLoggedIn
            ? "ようこそ - HomeLab SESAME Controller"
            : "HomeLab SESAME Controller";
    }

    [RelayCommand]
    private void Logout()
    {
        _authService.Logout();
        IsLoggedIn = false;
    }
}
