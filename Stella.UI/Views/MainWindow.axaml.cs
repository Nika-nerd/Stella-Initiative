using Avalonia.Controls;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Avalonia.Interactivity;
using Stella.UI.ViewModels;
using System;

namespace Stella.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

   
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is MainWindowViewModel vm)
        {
           
            var genButton = this.FindControl<Button>("GenButton");
            if (genButton != null)
            {
                genButton.Click += async (s, e) => 
                {
                    var inputBox = this.FindControl<TextBox>("InputBox");
                    var outputBlock = this.FindControl<SelectableTextBlock>("OutputBlock");

                    if (inputBox != null && outputBlock != null)
                    {
                        vm.UserPrompt = inputBox.Text;
                        outputBlock.Text = "⏳ Стелла думает...";
                        
                        await vm.GenerateCommand.Execute();
                        
                        outputBlock.Text = vm.GeneratedCode;
                    }
                };
            }
        }
    }
}