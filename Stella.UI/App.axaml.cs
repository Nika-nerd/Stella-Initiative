using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Stella.Core.Interfaces;
using Stella.Infrastructure.Services;
using Stella.UI.ViewModels;
using Stella.UI.Views;

namespace Stella.UI;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();

        services.AddHttpClient<ILLMService, OllamaChatService>(client =>
        {
            client.Timeout = TimeSpan.FromMinutes(5);
        });

        services.AddSingleton<ICodeValidator, DockerValidationService>();

        services.AddSingleton<MainWindowViewModel>();
        
        var serviceProvider = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = serviceProvider.GetRequiredService<MainWindowViewModel>();
            
            
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}