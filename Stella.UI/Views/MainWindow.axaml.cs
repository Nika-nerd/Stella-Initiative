using Avalonia.Controls;
using Avalonia.Interactivity;
using Stella.UI.ViewModels;
using System.Threading.Tasks;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace Stella.UI.Views;

public partial class MainWindow : Window
{
    
    
  
    public MainWindow()
    {
        InitializeComponent();
    }
    
    private void OnOpenLogClick(object sender, RoutedEventArgs e)
    {
        var overlay = this.FindControl<Grid>("ErrorLogOverlay");
        if (overlay != null)
        {
            overlay.IsVisible = true;
        }
    }

    private void OnCloseLogClick(object sender, RoutedEventArgs e)
    {
        var overlay = this.FindControl<Grid>("ErrorLogOverlay");
        if (overlay != null)
        {
            overlay.IsVisible = false;
        }
    }


    private async void OnGenButtonClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            await vm.StartGenerationProcess();
        }
    }
    
    private async void OnSelectFolderClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select the root folder of the Rust project (where Cargo.toml is located)",
                AllowMultiple = false
            });

            if (folders.Count > 0)
            {
                vm.ProjectPath = folders[0].Path.LocalPath;
            }
        }
    }
    
    private async void OnCopyButtonClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            var pureCode = vm.ExtractCode(vm.GeneratedCode ?? "");

            if (!string.IsNullOrEmpty(pureCode))
            {
                var clipBoard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipBoard != null)
                {
                    await clipBoard.SetTextAsync(pureCode);

                    if (sender is Button btn)
                    {
                        var oldContent = btn.Content;
                        btn.Content = "Copied!";
                        await Task.Delay(1500);
                        btn.Content = oldContent;
                    }
                }
            }
        }
    }
}