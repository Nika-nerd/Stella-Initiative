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
        int maxAttempts = 5;
        int currentAttempt = 0;
        string? lastCode = null;

       
        string lastErrorsReport = string.Empty;
        string lastSmartHints = string.Empty;

        try
        {
            while (currentAttempt < maxAttempts)
            {
                currentAttempt++;
                UpdateStatus($"⏳ Попытка {currentAttempt} из {maxAttempts}...");
                
                await Task.Delay(100);
                
                var iterationPromptBuilder =  new StringBuilder();

                if (currentAttempt > 1)
                {
                    GeneratedCode =
                        $"/* [АГЕНТ СТЕЛЛА]: Попытка {currentAttempt}. Пересборка контекста и исправление Clippy...*/\n\n" +
                        GeneratedCode;
                }

                
                

                if (currentAttempt == 1)
                {
                    iterationPromptBuilder.AppendLine($"Initial Task: {UserPrompt}");
                }
                else
                {
                    
                    iterationPromptBuilder.AppendLine($"Task: {UserPrompt}\n");
                    iterationPromptBuilder.AppendLine("CRITICAL: Your previous code failed validation. You must refactor it based on the compiler/clippy report below.");
                    iterationPromptBuilder.AppendLine("=== CURRENT CODE TO FIX ===");
                    iterationPromptBuilder.AppendLine($"```rust\n{lastCode}\n ```\n");
                    iterationPromptBuilder.AppendLine("=== COMPILER & CLIPPY REPORT ===");
                    iterationPromptBuilder.AppendLine(lastErrorsReport);
                    
                    if (!string.IsNullOrEmpty(lastSmartHints))
                    {
                        iterationPromptBuilder.AppendLine($"\n[KNOWLEDGE BASE ADVICE]:\n{lastSmartHints}");
                    }
                    iterationPromptBuilder.AppendLine("\nInstruction: Locate the exact 'Context Line Code' failing inside your file. Fix it. Do NOT rewrite the entire architecture from scratch if it's correct. Modify ONLY what causes the errors.");
                }
                
                System.Diagnostics.Debug.WriteLine($"\n================[ПОПЫТКА {currentAttempt}]==============");
                System.Diagnostics.Debug.WriteLine($"[ПРОМПТ]: \n{iterationPromptBuilder}");

                
                var response = await _llmService.GenerateCodeAsync(iterationPromptBuilder.ToString(), "default", currentAttempt);
                System.Diagnostics.Debug.WriteLine($"[ОТВЕТ LLM]: \n{response}");
                var newCode = ExtractCode(response);

                if (string.IsNullOrWhiteSpace(newCode))
                {
                    UpdateStatus("⚠️ Модель выдала пустой код. Прерывание.");
                    break;
                }

                if (newCode == lastCode)
                {
                    System.Diagnostics.Debug.WriteLine($"[ВЫХОД]: Цикл прерван! Модель выдала ТОЧНО ТАКОЙ ЖЕ код, как на прошлом шаге (Зацикливание).");
                        
                    UpdateStatus($"⚠️ Тупик на попытке {currentAttempt}. Модель зациклилась.");
                    break;
                }

                lastCode = newCode;
                GeneratedCode = response; 

                UpdateStatus($"🔍 Валидация и Авто-исправление кода (попытка {currentAttempt})...");
                var result = await _validator.ValidateAsync(newCode);
                
                System.Diagnostics.Debug.WriteLine($"[РЕЗУЛЬТАТ ВАЛИДАЦИИ]: Успех сборки = {result.IsSuccess}, Всего замечний Clippy/Тестов = {result.Issues.Count}");
                foreach (var issue in result.Issues)
                {
                    System.Diagnostics.Debug.WriteLine($"-> [{issue.Severity}] Line {issue.Line}: {issue.Message}");
                }

                
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
                    System.Diagnostics.Debug.WriteLine("[ВЫХОД]: Цикл прерван! Код идеален");
                    UpdateStatus("✅ Код успешно скомпилирован, автоматически исправлен и протестирован!");
                    break;
                }

                var errorsFeedBuilder = new StringBuilder();
                string[] codeLines = lastCode.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

                foreach (var issue in result.Issues)
                {
                    errorsFeedBuilder.AppendLine("[BUG DETECTED]");
                    errorsFeedBuilder.AppendLine($"Severity: {issue.Severity.ToUpper()}");
                    errorsFeedBuilder.AppendLine($"Message: {issue.Message}");
                    
                    if (issue.Line.HasValue && issue.Line.Value > 0)
                    {
                        int zeroBasedLine = issue.Line.Value - 1;
                        errorsFeedBuilder.AppendLine($"Line Number: {issue.Line.Value}");
                        
                        if (zeroBasedLine < codeLines.Length)
                        {
                            string problematicLine = codeLines[zeroBasedLine].Trim();
                            errorsFeedBuilder.AppendLine($"Context Line Code: `{problematicLine}`");
                        }
                    }
                    errorsFeedBuilder.AppendLine("--------------------------------");
                }

                lastErrorsReport = errorsFeedBuilder.ToString();
                lastSmartHints = GenerateSmartHints(lastErrorsReport);

                if (currentAttempt == maxAttempts)
                {
                    UpdateStatus($"⚠️ Лимит попыток исчерпан. Ошибок осталось: {result.Issues.Count}");
                }
            }
        }
        catch (Exception ex)
        {
            GeneratedCode = $"❌ Критическая ошибка: {ex.Message}";
            UpdateStatus("🚨 Сбой работы агента");
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
        int startIndex = markdown.IndexOf(startTag);
        if (startIndex == -1)
        {
            startTag = "``";
            startIndex = markdown.IndexOf(startTag);
        }

        if (startIndex != -1)
        {
            int codeStart = startIndex + startTag.Length;
            int endIndex = markdown.IndexOf(" ```", codeStart);

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