using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomeLab.NativeClient.Services;
using Microsoft.Extensions.DependencyInjection;

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
        // 初期表示はログイン画面
        CurrentView = new LoginViewModel(authService);
    }

    partial void OnIsLoggedInChanged(bool value)
    {
        UpdateGreeting();
    }

    partial void OnCurrentViewChanged(ViewModelBase value)
    {
        // ログイン成功時の検知
        if (value is DashboardViewModel && !IsLoggedIn)
        {
            IsLoggedIn = true;
        }
    }

    private void UpdateGreeting()
    {
        Greeting = IsLoggedIn
            ? "ようこそ - HomeLab SESAME Controller"
            : "HomeLab SESAME Controller";
    }

    [RelayCommand]
    private void NavigateDashboard()
    {
        CurrentView = new DashboardViewModel(
            App.Services!.GetRequiredService<ApiService>());
    }

    [RelayCommand]
    private void Logout()
    {
        _authService.Logout();
        IsLoggedIn = false;
        CurrentView = new LoginViewModel(_authService);
    }
}
