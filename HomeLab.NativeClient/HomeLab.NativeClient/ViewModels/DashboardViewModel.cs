using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomeLab.NativeClient.Services;
using HomeLab.Shared.DTOs;

namespace HomeLab.NativeClient.ViewModels;

public partial class DashboardViewModel : ViewModelBase
{
    private readonly ApiService _apiService;

    [ObservableProperty]
    private ObservableCollection<DeviceListItem> _devices = [];

    [ObservableProperty]
    private DeviceListItem? _selectedDevice;

    [ObservableProperty]
    private bool _isRefreshing;

    public DashboardViewModel(ApiService apiService)
    {
        _apiService = apiService;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsRefreshing = true;
        ErrorMessage = null;

        try
        {
            var devices = await _apiService.GetDevicesAsync();
            if (devices is not null)
            {
                Devices = new ObservableCollection<DeviceListItem>(devices);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"デバイス一覧の取得に失敗: {ex.Message}";
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    private async Task ToggleLockAsync(Guid deviceId)
    {
        IsBusy = true;
        try
        {
            await _apiService.ToggleAsync(deviceId);
            await RefreshAsync(); // 状態をリフレッシュ
        }
        catch (Exception ex)
        {
            ErrorMessage = $"ロック操作に失敗: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UnlockAsync(Guid deviceId)
    {
        IsBusy = true;
        try
        {
            await _apiService.UnlockAsync(deviceId);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"解錠に失敗: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LockAsync(Guid deviceId)
    {
        IsBusy = true;
        try
        {
            await _apiService.LockAsync(deviceId);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"施錠に失敗: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
