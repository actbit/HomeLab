using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using HomeLab.NativeClient.Services;
using HomeLab.NativeClient.ViewModels;
using HomeLab.NativeClient.Views;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;

namespace HomeLab.NativeClient
{
    public partial class App : Application
    {
        public static IServiceProvider Services { get; private set; } = null!;

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            // DIコンテナの設定
            var services = new ServiceCollection();
            ConfigureServices(services);
            Services = services.BuildServiceProvider();

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new MainWindow
                {
                    DataContext = Services.GetRequiredService<MainViewModel>()
                };
            }
            else if (ApplicationLifetime is IActivityApplicationLifetime activityLifetime)
            {
                activityLifetime.MainViewFactory = () => new MainView
                {
                    DataContext = Services.GetRequiredService<MainViewModel>()
                };
            }
            else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
            {
                singleViewPlatform.MainView = new MainView
                {
                    DataContext = Services.GetRequiredService<MainViewModel>()
                };
            }

            base.OnFrameworkInitializationCompleted();
        }

        private static void ConfigureServices(IServiceCollection services)
        {
            // HTTP クライアント
            services.AddHttpClient<ApiService>(client =>
            {
                client.BaseAddress = new Uri("https://localhost:5001");
            });

            // サービス
            services.AddSingleton<AuthService>();

            // ViewModels
            services.AddTransient<MainViewModel>();
            services.AddTransient<LoginViewModel>();
            services.AddTransient<DashboardViewModel>();
        }
    }
}
