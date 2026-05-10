using System.Reactive;
using System.Threading.Tasks;
using ReactiveUI;
using Stella.Core.Interfaces; 

namespace Stella.UI.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private readonly ILLMService? _llmService;
    private string? _userPrompt = string.Empty;
    private string? _generatedCode = "Введите запрос и нажмите 'Сгенерировать'";

    
    public string? UserPrompt
    {
        get => _userPrompt;
        set => this.RaiseAndSetIfChanged(ref _userPrompt, value);
    }

    
    public string? GeneratedCode
    {
        get => _generatedCode;
        set => this.RaiseAndSetIfChanged(ref _generatedCode, value);
    }

    
    public ReactiveCommand<Unit, Unit> GenerateCommand { get; }

    
    public MainWindowViewModel(ILLMService llmService)
    {
        _llmService = llmService;

        GenerateCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (string.IsNullOrWhiteSpace(UserPrompt)) return;

            GeneratedCode = "⏳ Стелла анализирует запрос и пишет код...";
            
            try 
            {
               
                GeneratedCode = await _llmService.GenerateCodeAsync(UserPrompt, "default");
            }
            catch (System.Exception ex)
            {
                GeneratedCode = $"❌ Ошибка связи с ИИ: {ex.Message}";
            }
        });
    }

    
    public MainWindowViewModel() { } 
}