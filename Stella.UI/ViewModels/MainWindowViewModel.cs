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
    private string? _generatedCode = "Enter your query and click 'Generate'";
    private string? _validationStatus = string.Empty;
    private bool _isBusy;
    private string _projectName = "Project: Not defined";
    private string _rustEdition = "Edition: unknown";
    private string _projectPath = string.Empty;
    private string _detailedErrorLog = string.Empty;
    private bool _hasErrors;
    public bool HasErrors { get => _hasErrors; set => this.RaiseAndSetIfChanged(ref _hasErrors, value); }

    
    
    public string DetailedErrorLog { get => _detailedErrorLog; set => this.RaiseAndSetIfChanged(ref _detailedErrorLog, value); }

    public string ProjectPath { get => _projectPath; set => this.RaiseAndSetIfChanged(ref _projectPath, value); }
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
    if (string.IsNullOrWhiteSpace(ProjectPath) || !Directory.Exists(ProjectPath))
    {
        UpdateStatus("❌ Please select a valid project directory first!");
        return;
    }

    IsBusy = true;
    
    Dispatcher.UIThread.Post(() => {
        DetailedErrorLog = string.Empty;
        HasErrors = false;
    });

    int maxAttempts = 3;
    int currentAttempt = 0;
    string? lastPureCode = null;
    
    string currentProjectPath = ProjectPath; 
    string defaultTargetFile = "src/main.rs"; 

    var memoryLogBuilder = new StringBuilder();

    try
    {
        UpdateStatus("🗺 Indexing project structure...");
        var blueprint = await _projectAnalyzer.AnalyzeProjectAsync(currentProjectPath);

        Dispatcher.UIThread.Post(() => {
            ProjectName = $"Project: {blueprint.ProjectName}";
            RustEdition = $"Edition: {blueprint.RustEdition}";
            Dependencies.Clear();
            foreach (var dep in blueprint.Dependencies) Dependencies.Add($"• {dep}");
            Modules.Clear();
            foreach (var kvp in blueprint.ModulesGraph)
            {
                var apiSummary = new List<string>();
                if (kvp.Value.PublicStructs.Any()) apiSummary.Add($"Structs: {string.Join(", ", kvp.Value.PublicStructs)}");
                if (kvp.Value.PublicEnums.Any()) apiSummary.Add($"Enums: {string.Join(", ", kvp.Value.PublicEnums)}");
                if (kvp.Value.PublicTraits.Any()) apiSummary.Add($"Traits: {string.Join(", ", kvp.Value.PublicTraits)}");
                if (kvp.Value.PublicFunctions.Any()) apiSummary.Add($"Fns: {string.Join(", ", kvp.Value.PublicFunctions)}");

                string typePrefix = kvp.Value.Type switch
                {
                    ModuleType.BinaryRoot => "🚀 [BIN] ",
                    ModuleType.LibraryRoot => "📚 [LIB] ",
                    ModuleType.IntegrationTest => "🧪 [TEST] ",
                    ModuleType.Benchmark => "⏱️ [BENCH] ",
                    _ => "📄 "
                };

                Modules.Add(new ModuleItemViewModel { FilePath = $"{typePrefix}{kvp.Key}", InternalImports = kvp.Value.UsesInternal, PublicApi = apiSummary });
            }
        });

        while (currentAttempt < maxAttempts)
        {
            currentAttempt++;
            double targetTemperature = (currentAttempt == 1) ? 0.0 : 0.2; 

            UpdateStatus($"⏳ Generation attempt {currentAttempt}/{maxAttempts}...");
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
                iterationPromptBuilder.AppendLine(" ```\n");

                iterationPromptBuilder.AppendLine($"Task: {UserPrompt}");
                iterationPromptBuilder.AppendLine("Apply the changes to the Active File Context. Return the FULL updated code for this file.");
            }
            else
            {
                iterationPromptBuilder.AppendLine($"Task: {UserPrompt}\n");
                iterationPromptBuilder.AppendLine("Your previous code modifications failed compilation. Review the history of failed attempts below and avoid repeating these mistakes.");
                iterationPromptBuilder.AppendLine("=== FAILED ATTEMPTS HISTORY ===");
                iterationPromptBuilder.AppendLine(memoryLogBuilder.ToString());
                iterationPromptBuilder.AppendLine("===============================\n");
                
                string hints = GenerateSmartHints(memoryLogBuilder.ToString());
                iterationPromptBuilder.AppendLine($"💡 Critical Hint:\n{hints}");
                iterationPromptBuilder.AppendLine("\nCRITICAL: Do NOT output [ANALYSIS & PLANNING] this time. Return ONLY the fully corrected code execution block inside a single ```rust ... ``` block.");
            }
            
            var response = await _llmService.GenerateCodeAsync(iterationPromptBuilder.ToString(), currentAttempt, targetTemperature);
            var newPureCode = ExtractCode(response);

            if (string.IsNullOrWhiteSpace(newPureCode) || !newPureCode.Contains('{') || !newPureCode.Contains('}')) 
            {
                UpdateStatus("⚠️ Received corrupted code layout. Retrying generation...");
                AppendErrorToMemory(memoryLogBuilder, currentAttempt, "None", "System forced regeneration due to broken markdown code block structure.");
                continue;
            }

            if (newPureCode == lastPureCode)
            {
                UpdateStatus("⚠️ Model stuck in an infinite error loop. Aborting pipeline.");
                break;
            }

            lastPureCode = newPureCode;
            Dispatcher.UIThread.Post(() => GeneratedCode = response); 

            UpdateStatus($"🔍 Running cargo validation (Attempt {currentAttempt})...");
            var result = await _validator.ValidateAsync(newPureCode);
            
            if (result.IsSuccess)
            {
                UpdateStatus("✅ Success! Code compiled cleanly and passed all local tests.");
                break;
            }

            AppendErrorToMemory(memoryLogBuilder, currentAttempt, newPureCode, result.RawOutput);
            
            Dispatcher.UIThread.Post(() => {
                DetailedErrorLog = memoryLogBuilder.ToString();
                HasErrors = true;
            });

            if (currentAttempt == maxAttempts)
            {
                UpdateStatus($"❌ Pipeline failed after {maxAttempts} attempts. Check the error log.");
            }
        }
    }
    catch (Exception ex)
    {
        Dispatcher.UIThread.Post(() => GeneratedCode = $"❌ Critical Pipeline Error: {ex.Message}");
        UpdateStatus("🚨 Stella internal engine panic");
    }
    finally
    {
        IsBusy = false;
    }
}

private void AppendErrorToMemory(StringBuilder builder, int attempt, string failedCode, string errorLog)
{
    builder.AppendLine($"======================================================================");
    builder.AppendLine($"❌ FAILED ATTEMPT #{attempt} — Compilation / Test Pipeline Error");
    builder.AppendLine($"======================================================================");
    builder.AppendLine("[Generated Broken Source Code Context]:");
    
    var lines = failedCode.Split('\n');
    if (lines.Length > 20)
    {
        builder.AppendLine(string.Join("\n", lines.Take(10)));
        builder.AppendLine("\t// ... [source lines truncated for log readability] ...");
        builder.AppendLine(string.Join("\n", lines.Skip(lines.Length - 10)));
    }
    else
    {
        builder.AppendLine(failedCode);
    }

    builder.AppendLine("\n[Raw Diagnostic Output from rustc / cargo / clippy]:");
    builder.AppendLine(string.IsNullOrWhiteSpace(errorLog) ? "No diagnostic output available from the child process wrapper." : errorLog);
    builder.AppendLine("\n");
}

private async Task AppendErrorToMemoryAsync(string filePath, int attempt, string failedCode, string errorLog)
{
    var sb = new StringBuilder();
    sb.AppendLine($"--- FAILED ATTEMPT #{attempt} ---");
    sb.AppendLine("[The Code You Generated]:");
    sb.AppendLine(failedCode); 
    sb.AppendLine("[Compiler Error Output from rustc]:");
    sb.AppendLine(errorLog);
    sb.AppendLine("▲ Carefully look at the Line and Column numbers in the error above. Fix this exact location. ▲");
    sb.AppendLine(new string('-', 30));
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
        { "E0308", "CRITICAL: Type mismatch. Check expected vs found types. Use `.as_str()`, `.to_string()`, or match types explicitly." },
        { "E0502", "CRITICAL: Borrow checker error! You cannot borrow a variable as mutable if it is already borrowed as immutable. Scope the borrows using inner blocks `{}` or drop the immutable borrow early using `drop()`." },
        { "E0382", "CRITICAL: Value moved! The variable was moved in a previous iteration or function call. Clone the data using `.clone()` before moving, or pass it by reference `&` instead of by value." },
        { "E0277", "CRITICAL: Trait bound not satisfied. The type does not implement the required trait. Check if you need to derive it (e.g., `#[derive(Debug, Clone)]`) or implement it manually." },
        { "E0597", "CRITICAL: Value does not live long enough. It is dropped while still borrowed. Ensure the reference outlives the data, or return an owned type (like `String` or `Vec`) instead of a reference." }
    };
}