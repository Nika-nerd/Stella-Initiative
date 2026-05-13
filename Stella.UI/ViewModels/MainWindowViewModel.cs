using System;
using System.Linq;
using System.Threading.Tasks;
using ReactiveUI;
using Stella.Core.Interfaces;
using Avalonia.Threading;

namespace Stella.UI.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private readonly ICodeValidator _validator;
    private readonly ILLMService _llmService;
    private string? _userPrompt = string.Empty;
    private string? _generatedCode = "Введите запрос и нажмите 'Сгенерировать'";
    private string? _validationStatus = string.Empty;
    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => this.RaiseAndSetIfChanged(ref _isBusy, value); }


    public string? ValidationStatus { get => _validationStatus; set => this.RaiseAndSetIfChanged(ref _validationStatus, value); }
    public string? UserPrompt { get => _userPrompt; set => this.RaiseAndSetIfChanged(ref _userPrompt, value); }
    public string? GeneratedCode { get => _generatedCode; set => this.RaiseAndSetIfChanged(ref _generatedCode, value); }

    public MainWindowViewModel(ILLMService llmService, ICodeValidator validator)
    {
        _llmService = llmService;
        _validator = validator;
    }

    
    public async Task StartGenerationProcess()
    {
        if (string.IsNullOrWhiteSpace(UserPrompt) || IsBusy) return;

        
        Dispatcher.UIThread.Post(() => {
            GeneratedCode = "⏳ Стелла анализирует запрос...";
            ValidationStatus = "⏳ Проверка запущена...";
        });

        try
        {
            var response = await _llmService.GenerateCodeAsync(UserPrompt, "default");
            var code = ExtractCode(response);
            var result = await _validator.ValidateAsync(code);

            Dispatcher.UIThread.Post(() =>
            {
                GeneratedCode = response;
                if (result.IsSuccess)
                {
                    ValidationStatus = "Код валиден";
                }
                else
                {
                    var firstError = result.Issues.FirstOrDefault()?.Message ?? "Неизвестная ошибка";

                    ValidationStatus = $"Ошибок: {result.Issues.Count}. Первая: {firstError}";
                }
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                GeneratedCode = $"❌ Ошибка: {ex.Message}";
                ValidationStatus = "🚨 Сбой";
            });
        }

        finally
        {
            Dispatcher.UIThread.Post(() => IsBusy =  false);
        }
    }

    private string ExtractCode(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        string startTag = "```rust";
        int startIndex = markdown.IndexOf(startTag);
        if (startIndex == -1)
        {
            startTag = "``";
            startIndex = markdown.IndexOf(startTag);
        }

        if (startIndex != -1)
        {
            int codeStart = startIndex + startTag.Length;
            
            int endIndex = markdown.IndexOf("```", codeStart);

            if (endIndex > codeStart)
            {
                return markdown.Substring(codeStart, endIndex - codeStart).Trim();
            }
        }
        
        return markdown.Trim();
    }
    
    
}