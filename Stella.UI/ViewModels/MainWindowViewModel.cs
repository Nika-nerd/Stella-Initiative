using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

    
    public bool IsBusy 
    { 
        get => _isBusy; 
        set => Dispatcher.UIThread.Post(() => this.RaiseAndSetIfChanged(ref _isBusy, value)); 
    }
    
    public string? ValidationStatus 
    { 
        get => _validationStatus; 
        set => Dispatcher.UIThread.Post(() => this.RaiseAndSetIfChanged(ref _validationStatus, value)); 
    }
    
    public string? UserPrompt 
    { 
        get => _userPrompt; 
        set => Dispatcher.UIThread.Post(() => this.RaiseAndSetIfChanged(ref _userPrompt, value)); 
    }
    
    public string? GeneratedCode 
    { 
        get => _generatedCode; 
        set => Dispatcher.UIThread.Post(() => this.RaiseAndSetIfChanged(ref _generatedCode, value)); 
    }

    public MainWindowViewModel(ILLMService llmService, ICodeValidator validator)
    {
        _llmService = llmService;
        _validator = validator;
    }

    public async Task StartGenerationProcess()
{
    if (string.IsNullOrWhiteSpace(UserPrompt) || IsBusy) return;

    IsBusy = true;
    int maxAttempts = 3;
    int currentAttempt = 0;
    string? lastCode = null;

    string lastErrorsReport = string.Empty;
    var failedCodeAttempts = new List<string>();
    
    bool forceAlternativeStrategy = false; 

    try
    {
        while (currentAttempt < maxAttempts)
{
    currentAttempt++;
    
    double targetTemperature = 0.0; 

    UpdateStatus($"⏳ Попытка {currentAttempt}/{maxAttempts}...");
    if (currentAttempt > 1)
    {
        UpdateStatus($"Ожидание лимитов API (2 сек)...");
        await Task.Delay(2000);
    }
    
    
    var iterationPromptBuilder = new StringBuilder();

    if (currentAttempt == 1)
    {
        iterationPromptBuilder.AppendLine($"Task: {UserPrompt}");
    }
    else
    {
        iterationPromptBuilder.AppendLine($"Task: {UserPrompt}\n");
        iterationPromptBuilder.AppendLine("Your previous code has compilation errors. Fix them.");
        iterationPromptBuilder.AppendLine("=== CODE TO FIX ===");
        iterationPromptBuilder.AppendLine(lastCode);
        iterationPromptBuilder.AppendLine("===================\n");
        iterationPromptBuilder.AppendLine("=== COMPILER ERRORS ===");
        iterationPromptBuilder.AppendLine(lastErrorsReport); 
        iterationPromptBuilder.AppendLine("\nReturn the complete fixed code inside ```rust ...```. No explanations.");
    }
    
    var response = await _llmService.GenerateCodeAsync(iterationPromptBuilder.ToString(), "default", currentAttempt, targetTemperature);
    var newCode = ExtractCode(response);

    if (string.IsNullOrWhiteSpace(newCode) || !newCode.Contains('}')) 
    {
        UpdateStatus("⚠️ Модель вернула поврежденный или недописанный код. Пробуем еще раз...");
        targetTemperature = 0.3;
        continue;
    }

    if (newCode == lastCode)
    {
        UpdateStatus("⚠️ Модель выдала точную копию кода. Прерывание.");
        break;
    }

    lastCode = newCode;
    GeneratedCode = response;

    UpdateStatus($"🔍 Проверка компилятором (попытка {currentAttempt})...");
    var result = await _validator.ValidateAsync(newCode);
    
    if (result.IsSuccess && result.Issues.Count == 0)
    {
        UpdateStatus("✅ Код успешно скомпилирован!");
        break;
    }

    
    var errorsFeedBuilder = new StringBuilder();
    
    var mainIssue = result.Issues.FirstOrDefault(i => i.Severity.ToLower() == "error");
    if (mainIssue != null)
    {
        errorsFeedBuilder.AppendLine($"Error on line {mainIssue.Line}: {mainIssue.Message}");
    }
    else if (result.Issues.Count > 0)
    {
        errorsFeedBuilder.AppendLine($"Issue: {result.Issues[0].Message}");
    }

    lastErrorsReport = errorsFeedBuilder.ToString();

    if (currentAttempt == maxAttempts)
    {
        UpdateStatus($"❌ Не удалось исправить. Ошибок: {result.Issues.Count}");
    }
}
    }
    catch (Exception ex)
    {
        GeneratedCode = $"❌ Ошибка пайплайна: {ex.Message}";
        UpdateStatus("🚨 Сбой Стеллы");
    }
    finally
    {
        IsBusy = false;
    }
}

    private string GenerateSmartHints(string logs)
    {
        var activeHints = new List<string>();
        foreach (var entry in _rustKnowledgeBase)
        {
            if (logs.Contains(entry.Key))
            {
                activeHints.Add(entry.Value);
            }
        }

        if (activeHints.Count == 0)
            return "Analyze the compiler/clippy output carefully and fix the specific lines.";

        return "Focus on fixing these patterns:\n" + string.Join("\n", activeHints);
    }

    private void UpdateStatus(string status)
    {
        Dispatcher.UIThread.Post(() => ValidationStatus = status);
    }

    public string ExtractCode(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
    
        string startTag = "```rust";
        int startIndex = markdown.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
        if (startIndex == -1)
        {
            startTag = "```";
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

    private readonly Dictionary<string, string> _rustKnowledgeBase = new()
    {
        { "ptr_arg", "Function arguments should be slices. Change `&Vec<T>` to `&[T]` or `&String` to `&str`." },
        { "len_zero", "Use `.is_empty()` instead of checking `.len() == 0`." },
        { "unused_variables", "Remove unused variables or prefix them with `_`." },
        { "approx_constant", "Use constants from `std::f64::consts` instead of manual numbers." },
        { "redundant_clone", "Remove unnecessary `.clone()`. Data can be borrowed here." },
        { "needless_return", "Remove `return` keyword at the end of the function; use implicit return." }
    };
}