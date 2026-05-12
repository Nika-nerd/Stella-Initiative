using System;
using System.Runtime.CompilerServices;
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
        if (string.IsNullOrWhiteSpace(UserPrompt)) return;

        
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
                GeneratedCode = code;
                ValidationStatus = result.IsSuccess 
                    ? "✅ Код валиден" 
                    : $"❌ Найдено ошибок: {result.Issues.Count}";
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => {
                GeneratedCode = $"❌ Ошибка: {ex.Message}";
                ValidationStatus = "🚨 Сбой";
            });
        }
    }

    private string ExtractCode(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        string tag = "```rust";
        if (markdown.Contains(tag))
        {
            var start = markdown.IndexOf(tag) + tag.Length;
            var end = markdown.IndexOf(" ```", start);
            if (end > start) return markdown.Substring(start, end - start).Trim();
        }
        return markdown.Trim();
    }
}