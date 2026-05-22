using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ReactiveUI;
using Stella.Core.Interfaces;
using Avalonia.Threading;
using Stella.Core.Models;
using System.Collections.ObjectModel;

namespace Stella.UI.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private readonly ICodeValidator _validator;
    private readonly ILLMService _llmService;
    private readonly IProjectAnalyzer _projectAnalyzer;
    
    private string? _userPrompt = string.Empty;
    private string? _generatedCode = "Введите запрос и нажмите 'Сгенерировать'";
    private string? _validationStatus = string.Empty;
    private bool _isBusy;
    private string _projectName = "Проект: Не определен";
    private string _rustEdition = "Edition: unknown";

    public bool IsBusy { get => _isBusy; set => this.RaiseAndSetIfChanged(ref _isBusy, value); }
    public string? ValidationStatus { get => _validationStatus; set => this.RaiseAndSetIfChanged(ref _validationStatus, value); }
    public string? UserPrompt { get => _userPrompt; set => this.RaiseAndSetIfChanged(ref _userPrompt, value); }
    public string? GeneratedCode { get => _generatedCode; set => this.RaiseAndSetIfChanged(ref _generatedCode, value); }
    public string ProjectName { get => _projectName; set => this.RaiseAndSetIfChanged(ref _projectName, value); }
    public string RustEdition { get => _rustEdition; set => this.RaiseAndSetIfChanged(ref _rustEdition, value); }

    public ObservableCollection<string> Dependencies { get; } = new();
    public ObservableCollection<ModuleItemViewModel> Modules { get; } = new();

    public MainWindowViewModel(ILLMService llmService, ICodeValidator validator, IProjectAnalyzer projectAnalyzer)
    {
        _llmService = llmService;
        _validator = validator;
        _projectAnalyzer = projectAnalyzer;
    }

    private ProjectBlueprint? _currentProjectMap;

    public ProjectBlueprint? CurrentProjectMap
    {
        get => _currentProjectMap;
        set => this.RaiseAndSetIfChanged(ref _currentProjectMap, value);
    }
    
    public async Task StartGenerationProcess()
    {
        if (string.IsNullOrWhiteSpace(UserPrompt) || IsBusy) return;

        IsBusy = true;
        int maxAttempts = 3;
        int currentAttempt = 0;
        string? lastPureCode = null;
        
        string currentProjectPath = "/Users/aliserik/RustroverProjects/guessing_game"; 
        string defaultTargetFile = "src/main.rs"; 

        string sessionHistoryFile = Path.Combine(Path.GetTempPath(), $"stella_error_memory_{Guid.NewGuid()}.tmp");

        try
        {
            UpdateStatus("🗺 Сборка карты проекта...");
            var blueprint = await _projectAnalyzer.AnalyzeProjectAsync(currentProjectPath);

            Dispatcher.UIThread.Post(() => {
                ProjectName = $"Проект: {blueprint.ProjectName}";
                RustEdition = $"Edition: {blueprint.RustEdition}";
    
                Dependencies.Clear();
                foreach (var dep in blueprint.Dependencies) Dependencies.Add($"• {dep}");

                Modules.Clear();
                foreach (var kvp in blueprint.ModulesGraph)
                {
                    Modules.Add(new ModuleItemViewModel
                    {
                        FilePath = kvp.Key,
                        InternalImports = kvp.Value.UsesInternal,
                        PublicApi = kvp.Value.PublicDefinitions
                    });
                }
            });

            while (currentAttempt < maxAttempts)
            {
                currentAttempt++;
                double targetTemperature = (currentAttempt == 1) ? 0.0 : 0.2; 

                UpdateStatus($"⏳ Попытка {currentAttempt}/{maxAttempts}...");
                if (currentAttempt > 1) await Task.Delay(1000); 
                
                var iterationPromptBuilder = new StringBuilder();
                
                if (currentAttempt == 1)
                {
                    string targetFile = defaultTargetFile;
                    
                    foreach (var fileKey in blueprint.ModulesGraph.Keys)
                    {
                        if (UserPrompt.Contains(Path.GetFileName(fileKey), StringComparison.OrdinalIgnoreCase))
                        {
                            targetFile = fileKey;
                            break;
                        }
                    }

                    string targetFileFullPath = Path.Combine(currentProjectPath, targetFile);
                    string targetFileCode = File.Exists(targetFileFullPath) ? await File.ReadAllTextAsync(targetFileFullPath) : "";
                    string relatedDefinitions = await _projectAnalyzer.TraceAndExtractDependenciesAsync(currentProjectPath, blueprint, targetFile);

                    iterationPromptBuilder.AppendLine("=== PROJECT BLUEPRINT ===");
                    iterationPromptBuilder.AppendLine($"Project: {blueprint.ProjectName}, Edition: {blueprint.RustEdition}");
                    iterationPromptBuilder.AppendLine($"Dependencies: {string.Join(", ", blueprint.Dependencies)}");
                    iterationPromptBuilder.AppendLine("=========================\n");

                    if (!string.IsNullOrEmpty(relatedDefinitions))
                    {
                        iterationPromptBuilder.AppendLine("=== RELATED TYPES & STRUCTS ===");
                        iterationPromptBuilder.AppendLine(relatedDefinitions);
                        iterationPromptBuilder.AppendLine("===============================\n");
                    }

                    iterationPromptBuilder.AppendLine($"Active File Context ({targetFile}):");
                    iterationPromptBuilder.AppendLine("```rust");
                    iterationPromptBuilder.AppendLine(targetFileCode);
                    iterationPromptBuilder.AppendLine("```\n");

                    iterationPromptBuilder.AppendLine($"Task: {UserPrompt}");
                    iterationPromptBuilder.AppendLine("Apply the changes to the Active File Context. Return the FULL updated code for this file.");
                }
                else
                {
                    string errorLedger = await File.ReadAllTextAsync(sessionHistoryFile);

                    iterationPromptBuilder.AppendLine($"Task: {UserPrompt}\n");
                    iterationPromptBuilder.AppendLine("Your previous code modifications failed compilation. Review the history of failed attempts below and avoid repeating these mistakes.");
                    iterationPromptBuilder.AppendLine("=== FAILED ATTEMPTS HISTORY ===");
                    iterationPromptBuilder.AppendLine(errorLedger);
                    iterationPromptBuilder.AppendLine("===============================\n");
                    
                    string hints = GenerateSmartHints(errorLedger);
                    iterationPromptBuilder.AppendLine($"💡 Critical Hint:\n{hints}");
                    iterationPromptBuilder.AppendLine("\nCRITICAL: Do NOT output [ANALYSIS & PLANNING] this time. Return ONLY the fully corrected code execution block inside a single ```rust ... ``` block.");
                }
                
                var response = await _llmService.GenerateCodeAsync(iterationPromptBuilder.ToString(), currentAttempt, targetTemperature);
                var newPureCode = ExtractCode(response);

                if (string.IsNullOrWhiteSpace(newPureCode) || !newPureCode.Contains('{') || !newPureCode.Contains('}')) 
                {
                    UpdateStatus("⚠️ Получен поврежденный код. Перегенерация...");
                    await AppendErrorToMemoryAsync(sessionHistoryFile, currentAttempt, "None", "System forced regeneration due to corrupted layout.");
                    continue;
                }

                if (newPureCode == lastPureCode)
                {
                    UpdateStatus("⚠️ Модель зациклилась на одной версии кода. Остановка пайплайна.");
                    break;
                }

                lastPureCode = newPureCode;
                GeneratedCode = response; 

                UpdateStatus($"🔍 Верификация кода (Итерация {currentAttempt})...");
                var result = await _validator.ValidateAsync(newPureCode);
                
                if (result.IsSuccess)
                {
                    UpdateStatus("✅ Успех! Код скомпилирован и успешно прошел тесты.");
                    break;
                }

                await AppendErrorToMemoryAsync(sessionHistoryFile, currentAttempt, newPureCode, result.RawOutput);

                if (currentAttempt == maxAttempts)
                {
                    UpdateStatus($"❌ Не удалось исправить за {maxAttempts} попыток. Проверьте лог ошибок.");
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
            if (File.Exists(sessionHistoryFile))
            {
                try { File.Delete(sessionHistoryFile); } catch { }
            }
            IsBusy = false;
        }
    }

    private async Task AppendErrorToMemoryAsync(string filePath, int attempt, string failedCode, string errorLog)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"--- Attempt #{attempt} ---");
        sb.AppendLine("[Failed Code Snippet]:");
        
        var codeLines = failedCode.Split('\n');
        if (codeLines.Length > 25)
        {
            sb.AppendLine(string.Join("\n", codeLines.Take(12)));
            sb.AppendLine("[... large code block truncated for brevity ...]");
            sb.AppendLine(string.Join("\n", codeLines.Skip(codeLines.Length - 12)));
        }
        else
        {
            sb.AppendLine(failedCode);
        }

        sb.AppendLine("[Compiler/Test Error Output]:");
        var cleanLog = string.Join("\n", errorLog.Split('\n').Take(10)); 
        sb.AppendLine(cleanLog);
        sb.AppendLine();

        await File.AppendAllTextAsync(filePath, sb.ToString());
    }

    private string GenerateSmartHints(string logs)
    {
        var activeHints = _rustKnowledgeBase
            .Where(entry => logs.Contains(entry.Key))
            .Select(entry => entry.Value)
            .ToList();

        return activeHints.Count == 0 
            ? "Inspect lifetimes, look closely at strict type mismatches, and verify function signatures." 
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
            startTag = "```";
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