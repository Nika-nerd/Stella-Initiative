using System;
using System.Collections.Generic;
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

    IsBusy = true;
    int maxAttempts = 5; 
    int currentAttempt = 0;
    
    string currentPrompt = UserPrompt;
    string? lastCode = null;

    try
    {
        while (currentAttempt < maxAttempts)
        {
            currentAttempt++;
            UpdateStatus($"⏳ Попытка {currentAttempt} из {maxAttempts}...");

            
            var response = await _llmService.GenerateCodeAsync(currentPrompt, "default");
            var newCode = ExtractCode(response);

           
            if (newCode == lastCode) 
            {
                ValidationStatus = $"⚠️ Тупик на попытке {currentAttempt}. Модель повторяется.";
                break;
            }
            
            lastCode = newCode;
            
            
            GeneratedCode = response; 

            
            UpdateStatus($"🔍 Проверка Clippy (попытка {currentAttempt})...");
            var result = await _validator.ValidateAsync(newCode);

            
            if (result.IsSuccess && !result.Issues.Any(i => i.Severity == "warning"))
            {
                ValidationStatus = "✅ Идеальный код (проверка пройдена)";
                break;
            }

          
            var errorsFeed = string.Join("\n", result.Issues.Select(i => $"[{i.Severity}] {i.Message}"));
            
           
            string smartHints = GenerateSmartHints(errorsFeed);

            
            currentPrompt = $"Your previous Rust implementation has issues. Follow the guidance below to fix it.\n\n" +
                            $"CONTEXT:\n{newCode}\n\n" +
                            $"CLIPPY LOGS:\n{errorsFeed}\n\n" +
                            $"{smartHints}\n" +
                            $"INSTRUCTION: Revise the code. Return ONLY the full corrected code in a block.";

           
            if (currentAttempt == maxAttempts)
            {
                ValidationStatus = $"⚠️ Лимит попыток исчерпан. Ошибок осталось: {result.Issues.Count}";
            }
        }
    }
    catch (Exception ex)
    {
        GeneratedCode = $"❌ Критическая ошибка: {ex.Message}";
        ValidationStatus = "🚨 Сбой";
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
        return "INSTRUCTION: Analyze the compiler logs and fix the code according to idiomatic Rust standards.";

    return "GUIDANCE FOR YOUR NEXT ATTEMPT (STRICT RULES):\n" + string.Join("\n", activeHints) + "\n";
}

private void UpdateStatus(string status)
{
    Dispatcher.UIThread.Post(() => ValidationStatus = status);
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
    
    private readonly Dictionary<string, string> _rustKnowledgeBase = new()
    {
        { "ptr_arg", "CRITICAL: Function arguments should be slices. Change `&Vec<T>` to `&[T]` or `&String` to `&str`." },
        { "len_zero", "TIP: Use `.is_empty()` instead of checking `.len() == 0`." },
        { "unused_variables", "CLEANUP: Remove unused variables or prefix them with `_`." },
        { "approx_constant", "MATH: Use constants from `std::f64::consts` instead of manual numbers." },
        { "redundant_clone", "PERF: Remove unnecessary `.clone()`. Data can be borrowed here." },
        { "needless_return", "STYLE: Remove `return` keyword at the end of the function; use implicit return." }
    };
    
    
}