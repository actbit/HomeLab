using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomeLab.NativeClient.Services;

namespace HomeLab.NativeClient.ViewModels;

public partial class LoginViewModel : ViewModelBase
{
    private readonly AuthService _authService;

    [ObservableProperty]
    private string _email = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _totpCode = string.Empty;

    [ObservableProperty]
    private bool _showTotpField;

    [ObservableProperty]
    private bool _isLoginMode = true; // true=Login, false=Register

    [ObservableProperty]
    private string _displayName = string.Empty;

    public LoginViewModel(AuthService authService)
    {
        _authService = authService;
    }

    [RelayCommand]
    private async Task SubmitAsync()
    {
        IsBusy = true;
        ErrorMessage = null;

        try
        {
            bool success;

            if (IsLoginMode)
            {
                success = await _authService.LoginWithPasswordAsync(Email, Password, ShowTotpField ? TotpCode : null);
            }
            else
            {
                success = await _authService.RegisterAsync(Email, Password, DisplayName);
            }

            if (!success)
            {
                ErrorMessage = IsLoginMode ? "ログインに失敗しました" : "登録に失敗しました";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"エラー: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoginWithPasskeyAsync()
    {
        IsBusy = true;
        ErrorMessage = null;

        try
        {
            var success = await _authService.LoginWithPasskeyAsync();
            if (!success)
            {
                ErrorMessage = "Passkey認証に失敗しました";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Passkeyエラー: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ToggleMode()
    {
        IsLoginMode = !IsLoginMode;
        ErrorMessage = null;
    }
}
