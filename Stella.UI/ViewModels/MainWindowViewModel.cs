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
        int maxAttempts = 3;
        int currentAttempt = 0;
        string? lastCode = null;
        string lastErrorsReport = string.Empty;

        try
        {
            while (currentAttempt < maxAttempts)
            {
                currentAttempt++;
                double targetTemperature = (currentAttempt == 1) ? 0.0 : 0.2; 

                UpdateStatus($"⏳ Попытка {currentAttempt}/{maxAttempts}...");
                if (currentAttempt > 1) await Task.Delay(1500); 
                
                var iterationPromptBuilder = new StringBuilder();
                if (currentAttempt == 1)
                {
                    iterationPromptBuilder.AppendLine($"Write Rust code for task: {UserPrompt}");
                }
                else
                {
                    iterationPromptBuilder.AppendLine($"Task: {UserPrompt}\n");
                    iterationPromptBuilder.AppendLine("Your previous code has errors. Fix them immediately.");
                    iterationPromptBuilder.AppendLine("=== CODE TO FIX ===");
                    iterationPromptBuilder.AppendLine(lastCode);
                    iterationPromptBuilder.AppendLine("===================\n");
                    iterationPromptBuilder.AppendLine("=== CRITICAL COMPILER/TEST ERRORS ===");
                    iterationPromptBuilder.AppendLine(lastErrorsReport);
                    
                    string hints = GenerateSmartHints(lastErrorsReport);
                    iterationPromptBuilder.AppendLine($"\n💡 Hints:\n{hints}");
                    iterationPromptBuilder.AppendLine("\nReturn the full code execution block. No comments.");
                }
                
                var response = await _llmService.GenerateCodeAsync(iterationPromptBuilder.ToString(), currentAttempt, targetTemperature);
                var newCode = ExtractCode(response);

                if (string.IsNullOrWhiteSpace(newCode) || !newCode.Contains('}')) 
                {
                    UpdateStatus("⚠️ Получен поврежденный код. Перегенерация...");
                    currentAttempt--; 
                    continue;
                }

                if (newCode == lastCode)
                {
                    UpdateStatus("⚠️ Модель продублировала ошибочный код. Остановка.");
                    break;
                }

                lastCode = newCode;
                GeneratedCode = response; 

                UpdateStatus($"🔍 Верификация через Clippy и Cargo Test (Итерация {currentAttempt})...");
                var result = await _validator.ValidateAsync(newCode);
                
                if (result.IsSuccess)
                {
                    UpdateStatus("✅ Успех! Код скомпилирован и прошел тесты.");
                    break;
                }

                lastErrorsReport = result.RawOutput;

                if (currentAttempt == maxAttempts)
                {
                    UpdateStatus($"❌ Исправить не удалось. Проверь логи.");
                }
            }
        }
        catch (Exception ex)
        {
            GeneratedCode = $"❌ Ошибка пайплайна: {ex.Message}";
            UpdateStatus("🚨 Критический сбой Stella");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string GenerateSmartHints(string logs)
    {
        var activeHints = _rustKnowledgeBase
            .Where(entry => logs.Contains(entry.Key))
            .Select(entry => entry.Value)
            .ToList();

        return activeHints.Count == 0 
            ? "Analyze the errors block and fix types, lifetimes, or method contracts explicitly." 
            : string.Join("\n", activeHints);
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
            startTag = " ```";
            startIndex = markdown.IndexOf(startTag);
        }

        if (startIndex != -1)
        {
            int codeStart = startIndex + startTag.Length;
            int endIndex = markdown.IndexOf("```", codeStart); 
            if (endIndex > codeStart) return markdown.Substring(codeStart, endIndex - codeStart).Trim();
        }

        return markdown.Trim();
    }

    private readonly Dictionary<string, string> _rustKnowledgeBase = new()
    {
        { "ptr_arg", "Optimization: Use slices instead of owning containers in arguments. E.g., `&str` instead of `&String`, or `&[T]` instead of `&Vec<T>`." },
        { "len_zero", "Style: Use `.is_empty()` method instead of comparing `.len() == 0`." },
        { "unused_variables", "Clean code: Remove unused variable or prefix it with an underscore: `_variable`." },
        { "redundant_clone", "Performance: Avoid allocation. Remove unnecessary `.clone()` calls where data can be borrowed." },
        { "needless_return", "Idiomatic code: Remove `return` keyword from the end of the block; use implicit expression return." }
    };
}