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
            
            double targetTemperature = currentAttempt == 1 ? 0.0 : 0.2 + (currentAttempt * 0.15);
            if (forceAlternativeStrategy) targetTemperature = Math.Min(targetTemperature + 0.2, 0.95);

            UpdateStatus($"⏳ Попытка {currentAttempt}/{maxAttempts} (Temp: {targetTemperature:F2})...");
            await Task.Delay(100);
            
            var iterationPromptBuilder = new StringBuilder();

            if (currentAttempt == 1)
            {
                iterationPromptBuilder.AppendLine($"Initial Task: {UserPrompt}");
            }
            else
            {
                iterationPromptBuilder.AppendLine($"Task: {UserPrompt}\n");
                iterationPromptBuilder.AppendLine("CRITICAL: Your previous implementation is completely wrong and fails Rust validation.");
                
                if (forceAlternativeStrategy)
                {
                    iterationPromptBuilder.AppendLine("![WARNING]: YOU ARE STUCK IN A LOOP. Do NOT write the code the same way. Destroy your previous structural approach and rewrite it using completely different Rust language features!");
                    forceAlternativeStrategy = false; 
                }

                iterationPromptBuilder.AppendLine("=== CURRENT WRONG CODE ===");
                iterationPromptBuilder.AppendLine(lastCode);
                iterationPromptBuilder.AppendLine("==========================\n");

                iterationPromptBuilder.AppendLine("=== COMPILER ERRORS ===");
                iterationPromptBuilder.AppendLine(lastErrorsReport);
                iterationPromptBuilder.AppendLine("\nInstruction: Fix the exact lines with errors. Return the FULL updated file inside ```rust ...```.");
            }
            
            System.Diagnostics.Debug.WriteLine($"\n================[ПОПЫТКА {currentAttempt} | TEMP: {targetTemperature}]==============");

            var response = await _llmService.GenerateCodeAsync(iterationPromptBuilder.ToString(), "default", currentAttempt, targetTemperature);
            var newCode = ExtractCode(response);

            if (string.IsNullOrWhiteSpace(newCode))
            {
                UpdateStatus("⚠️ Модель выдала пустой код.");
                break;
            }

            if (failedCodeAttempts.Contains(newCode) || newCode == lastCode)
            {
                System.Diagnostics.Debug.WriteLine($"[ПЕРЕХВАТ ПЕТЛИ]: Модель выдала дубликат. Форсируем хаос.");
                forceAlternativeStrategy = true;
                
                if (currentAttempt < maxAttempts)
                {
                    currentAttempt--; 
                    lastErrorsReport += "\n- Ошибка: Ты вернула точную копию предыдущего ошибочного кода! Измени логику!";
                    continue;
                }
                UpdateStatus($"⚠️ Тупик. Модель не смогла выйти из цикла.");
                break;
            }

            lastCode = newCode;
            GeneratedCode = response; 

            UpdateStatus($"🔍 Проверка кода компилятором (попытка {currentAttempt})...");
            var result = await _validator.ValidateAsync(newCode);
            
            if (!string.IsNullOrEmpty(result.UpdatedCode) && result.UpdatedCode != lastCode)
            {
                lastCode = result.UpdatedCode;
                string planPrefix = response.Contains("/*") && response.IndexOf("*/") > 0 
                    ? response.Substring(0, response.IndexOf("*/") + 2) + "\n\n" 
                    : "";
                GeneratedCode = planPrefix + "```rust\n" + lastCode + "\n```";
            }

            if (result.IsSuccess && result.Issues.Count == 0)
            {
                UpdateStatus("✅ Код успешно скомпилирован!");
                break;
            }

            failedCodeAttempts.Add(newCode);

            var errorsFeedBuilder = new StringBuilder();
            string[] codeLines = lastCode.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            foreach (var issue in result.Issues.Take(4))
            {
                errorsFeedBuilder.AppendLine($"-> [{issue.Severity.ToUpper()}] Линия {issue.Line}: {issue.Message}");
                if (issue.Line.HasValue && issue.Line.Value > 0 && issue.Line.Value <= codeLines.Length)
                {
                    errorsFeedBuilder.AppendLine($"   Код: `{codeLines[issue.Line.Value - 1].Trim()}`");
                }
            }

            lastErrorsReport = errorsFeedBuilder.ToString();

            if (currentAttempt == maxAttempts)
            {
                UpdateStatus($"⚠️ Ошибок осталось: {result.Issues.Count}");
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