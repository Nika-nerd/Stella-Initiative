using Avalonia.Controls;
using Avalonia.Interactivity;
using Stella.UI.ViewModels;
using System.Threading.Tasks;
using Avalonia.Input.Platform;


namespace Stella.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnGenButtonClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
           
            Task.Run(() => vm.StartGenerationProcess());
        }
    }
    
    private async void OnCopyButtonClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            var pureCode = typeof(MainWindowViewModel)
                .GetMethod("ExtractCode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.Invoke(vm, new object[] {vm.GeneratedCode ?? ""}) as string;

            if (!string.IsNullOrEmpty(pureCode))
            {
                var clipBoard = TopLevel.GetTopLevel(this)?.Clipboard;
                await clipBoard.SetTextAsync(pureCode);

                if (sender is Button btn)
                {
                    var oldContent = btn.Content;

                    btn.Content = "Скопировано!";
                    
                    await Task.Delay(1500);
                    btn.Content = oldContent;
                }
            }
        }
    }
}