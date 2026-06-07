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
using System.Windows.Input;
using Stella.Infrastructure.Services;

namespace Stella.UI.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private readonly ICodeValidator _validator;
    private readonly ILLMService _llmService;
    private readonly IProjectAnalyzer _projectAnalyzer;
    private readonly ProjectContextManager _contextManager;
    
    private string? _userPrompt = string.Empty;
    private string? _generatedCode = "Enter your query and click 'Generate'";
    private string? _validationStatus = string.Empty;
    private bool _isBusy;
    private string _projectName = "Project: Not defined";
    private string _rustEdition = "Edition: unknown";
    private string _projectPath = string.Empty;
    private string _detailedErrorLog = string.Empty;
    private bool _hasErrors;
    private int _selectedModeIndex = 0; 
    
    private bool _isPendingUserApproval;
    private string _lastPureGeneratedCode = string.Empty;
    private string _lastTargetFile = "src/main.rs";

    public int SelectedModeIndex 
    { 
        get => _selectedModeIndex; 
        set {
            this.RaiseAndSetIfChanged(ref _selectedModeIndex, value);
            ValidationStatus = WorkMode == StellaWorkMode.Sandbox 
                ? "✨ Sandbox Mode active" 
                : "📁 Project Mode active (Select directory)";
        }
    }

    public StellaWorkMode WorkMode => SelectedModeIndex == 0 ? StellaWorkMode.Sandbox : StellaWorkMode.Project;

    public bool HasErrors { get => _hasErrors; set => this.RaiseAndSetIfChanged(ref _hasErrors, value); }
    public string DetailedErrorLog { get => _detailedErrorLog; set => this.RaiseAndSetIfChanged(ref _detailedErrorLog, value); }
    public string ProjectPath { get => _projectPath; set => this.RaiseAndSetIfChanged(ref _projectPath, value); }
    public bool IsBusy { get => _isBusy; set => this.RaiseAndSetIfChanged(ref _isBusy, value); }
    public string? ValidationStatus { get => _validationStatus; set => this.RaiseAndSetIfChanged(ref _validationStatus, value); }
    public string? UserPrompt { get => _userPrompt; set => this.RaiseAndSetIfChanged(ref _userPrompt, value); }
    public string? GeneratedCode { get => _generatedCode; set => this.RaiseAndSetIfChanged(ref _generatedCode, value); }
    public string ProjectName { get => _projectName; set => this.RaiseAndSetIfChanged(ref _projectName, value); }
    public string RustEdition { get => _rustEdition; set => this.RaiseAndSetIfChanged(ref _rustEdition, value); }

    public bool IsPendingUserApproval 
    { 
        get => _isPendingUserApproval; 
        set => this.RaiseAndSetIfChanged(ref _isPendingUserApproval, value); 
    }

    public ICommand ApplyChangesCommand { get; }
    public ICommand RejectChangesCommand { get; }

    public ObservableCollection<string> Dependencies { get; } = new();
    public ObservableCollection<ModuleItemViewModel> Modules { get; } = new();

    public MainWindowViewModel(ILLMService llmService, ICodeValidator validator, IProjectAnalyzer projectAnalyzer, ProjectContextManager contextManager)
    {
        _llmService = llmService;
        _validator = validator;
        _projectAnalyzer = projectAnalyzer;
        _contextManager = contextManager;

        ApplyChangesCommand = ReactiveCommand.CreateFromTask(OnApplyChangesAsync);
        RejectChangesCommand = ReactiveCommand.Create(OnRejectChanges);
    }

    public async Task StartGenerationProcess()
    {
        if (string.IsNullOrWhiteSpace(UserPrompt) || IsBusy) return;
        
        if (WorkMode == StellaWorkMode.Project && (string.IsNullOrWhiteSpace(ProjectPath) || !Directory.Exists(ProjectPath)))
        {
            UpdateStatus("❌ Please select a valid project directory first for Project Mode!");
            return;
        }

        IsBusy = true;
        IsPendingUserApproval = false;
        
        Dispatcher.UIThread.Post(() => {
            DetailedErrorLog = string.Empty;
            HasErrors = false;
        });

        int maxAttempts = 3;
        int currentAttempt = 0;
        string? lastPureCode = null;
        
        string currentProjectPath = ProjectPath; 
        _lastTargetFile = "src/main.rs";

        var uiLogBuilder = new StringBuilder();
        var modelShortLogBuilder = new StringBuilder();

        try
        {
            ProjectBlueprint? blueprint = null;

            if (WorkMode == StellaWorkMode.Project)
            {
                UpdateStatus("🗺 Indexing project structure...");
                blueprint = await _projectAnalyzer.AnalyzeProjectAsync(currentProjectPath);
                UpdateBlueprintUi(blueprint);

                foreach (var fileKey in blueprint.ModulesGraph.Keys)
                {
                    if (UserPrompt.Contains(Path.GetFileName(fileKey), StringComparison.OrdinalIgnoreCase))
                    {
                        _lastTargetFile = fileKey;
                        break;
                    }
                }

                ConfigureValidator(Path.Combine(currentProjectPath, "Cargo.toml"), _lastTargetFile);
            }
            else
            {
                Dispatcher.UIThread.Post(() => {
                    ProjectName = "Project: Isolated Sandbox";
                    RustEdition = "Edition: 2021";
                    Dependencies.Clear();
                    Modules.Clear();
                });
                
                ConfigureValidator(null, "src/main.rs");
            }

            while (currentAttempt < maxAttempts)
            {
                currentAttempt++;
                double targetTemperature = (currentAttempt == 1) ? 0.0 : 0.1; 

                if (currentAttempt > 1)
                {
                    int delayMs = (currentAttempt == 2) ? 3000 : 6000;
                    UpdateStatus($"⏳ Cooldown active (Rate Limit Protection)... Waiting {delayMs/1000}s");
                    await Task.Delay(delayMs);
                }

                UpdateStatus($"⏳ Generation attempt {currentAttempt}/{maxAttempts}...");
                
                var iterationPromptBuilder = new StringBuilder();
                
                iterationPromptBuilder.AppendLine("=== CRITICAL INSTRUCTIONS ===");
                iterationPromptBuilder.AppendLine("1. Return ONLY the complete, production-ready Rust code wrapped in a single ```rust ... ``` block.");
                iterationPromptBuilder.AppendLine("2. DO NOT include any explanations, introduction, or markdown text outside the code block.");
                iterationPromptBuilder.AppendLine("3. DO NOT ADD ANY COMMENTS (lines starting with // or /* */) inside the generated Rust code. Keep the code absolutely clean.");
                iterationPromptBuilder.AppendLine("4. Provide the FULL file content every time. Never truncate or leave placeholders.");
                iterationPromptBuilder.AppendLine("=============================\n");

                if (currentAttempt == 1)
                {
                    if (WorkMode == StellaWorkMode.Project && blueprint != null)
                    {
                        string targetFileFullPath = Path.Combine(currentProjectPath, _lastTargetFile);
                        string targetFileCode = File.Exists(targetFileFullPath) ? await File.ReadAllTextAsync(targetFileFullPath) : "";
                        string relatedDefinitions = await _projectAnalyzer.TraceAndExtractDependenciesAsync(currentProjectPath, blueprint, _lastTargetFile);

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

                        iterationPromptBuilder.AppendLine($"Active File Context ({_lastTargetFile}):");
                        iterationPromptBuilder.AppendLine("```rust");
                        iterationPromptBuilder.AppendLine(targetFileCode);
                        iterationPromptBuilder.AppendLine(" ```\n");
                    }
                    else
                    {
                        iterationPromptBuilder.AppendLine("=== SANDBOX ISOLATED GENERATION ===");
                        iterationPromptBuilder.AppendLine("Generate a standalone, self-contained Rust block based on the user request.");
                        iterationPromptBuilder.AppendLine("Make sure all necessary imports and a complete `mod tests` suite are present in the single file.");
                        iterationPromptBuilder.AppendLine("===================================\n");
                    }

                    iterationPromptBuilder.AppendLine($"Task: {UserPrompt}");
                }
                else
                {
                    iterationPromptBuilder.AppendLine($"Task: {UserPrompt}\n");
                    iterationPromptBuilder.AppendLine("Your previous code modifications failed compilation. Review the error logs below and fix them.");
                    iterationPromptBuilder.AppendLine("=== FAILED ATTEMPTS DIAGNOSTICS ===");
                    iterationPromptBuilder.AppendLine(modelShortLogBuilder.ToString());
                    iterationPromptBuilder.AppendLine("===================================\n");
                    
                    string hints = GenerateSmartHints(uiLogBuilder.ToString());
                    iterationPromptBuilder.AppendLine($"💡 Critical Hint:\n{hints}");
                    iterationPromptBuilder.AppendLine("\nREMINDER: Return ONLY the fully corrected code execution block inside a single ```rust ... ``` block without any // comments.");
                }
                
                var response = await _llmService.GenerateCodeAsync(iterationPromptBuilder.ToString(), currentAttempt, targetTemperature);
                var newPureCode = ExtractCode(response);

                if (string.IsNullOrWhiteSpace(newPureCode) || !newPureCode.Contains('{') || !newPureCode.Contains('}')) 
                {
                    UpdateStatus("⚠️ Received corrupted code layout. Retrying generation...");
                    AppendErrorToMemory(uiLogBuilder, modelShortLogBuilder, currentAttempt, "None", "System forced regeneration due to broken markdown code block structure.");
                    continue;
                }

                if (newPureCode == lastPureCode)
                {
                    UpdateStatus("⚠️ Model stuck in an infinite error loop. Aborting pipeline.");
                    break;
                }

                lastPureCode = newPureCode;
                _lastPureGeneratedCode = newPureCode; 
                Dispatcher.UIThread.Post(() => GeneratedCode = response); 

                UpdateStatus($"🔍 Running cargo validation (Attempt {currentAttempt})...");
                var result = await _validator.ValidateAsync(newPureCode);
                
                if (result.IsSuccess)
                {
                    if (WorkMode == StellaWorkMode.Project)
                    {
                        UpdateStatus("🛡️ Code passed tests! Pending your execution approval...");
                        Dispatcher.UIThread.Post(() => IsPendingUserApproval = true); 
                    }
                    else
                    {
                        UpdateStatus("✅ Success! Sandbox code compiled cleanly.");
                        Dispatcher.UIThread.Post(() => IsBusy = false); 
                    }
                    break;
                }

                AppendErrorToMemory(uiLogBuilder, modelShortLogBuilder, currentAttempt, newPureCode, result.RawOutput);
                
                Dispatcher.UIThread.Post(() => {
                    DetailedErrorLog = uiLogBuilder.ToString();
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
            Dispatcher.UIThread.Post(() => IsBusy = false);
        }
        finally
        {
            if (!IsPendingUserApproval)
            {
                Dispatcher.UIThread.Post(() => IsBusy = false);
            }
        }
    }

    private async Task OnApplyChangesAsync()
    {
        Dispatcher.UIThread.Post(() => IsPendingUserApproval = false);
        UpdateStatus("💾 Writing confirmed changes to disk...");

        try
        {
            if (WorkMode == StellaWorkMode.Project && !string.IsNullOrWhiteSpace(ProjectPath))
            {
                await _validator.ApplyChangesAsync(_lastPureGeneratedCode);
                
                UpdateStatus("⚙️ Recalculating AST Map via stella_lens...");
                await _contextManager.RebuildAstMapAsync(ProjectPath);

                var freshBlueprint = await _projectAnalyzer.AnalyzeProjectAsync(ProjectPath);
                
                Dispatcher.UIThread.Post(() => {
                    UpdateBlueprintUi(freshBlueprint);
                    UpdateStatus($"✅ Successfully merged and synced: {_lastTargetFile}");
                });
            }
            else
            {
                UpdateStatus("✅ Code verified in memory. Sandbox changes applied!");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Sync Exception Prevented]: {ex.Message}");
            UpdateStatus($"❌ Sync Error: {ex.Message}");
        }
        finally
        {
            Dispatcher.UIThread.Post(() => IsBusy = false);
        }
    }

    private void OnRejectChanges()
    {
        Dispatcher.UIThread.Post(() => {
            IsPendingUserApproval = false;
            IsBusy = false;
            UpdateStatus("🛑 Modifications rejected by user.");
        });
    }

    private void UpdateBlueprintUi(ProjectBlueprint blueprint)
    {
        if (blueprint == null) return;

        Dispatcher.UIThread.Post(() => {
            ProjectName = $"Project: {blueprint.ProjectName}";
            RustEdition = $"Edition: {blueprint.RustEdition}";
            
            Dependencies.Clear();
            if (blueprint.Dependencies != null)
            {
                foreach (var dep in blueprint.Dependencies) Dependencies.Add($"• {dep}");
            }
            
            Modules.Clear();
            if (blueprint.ModulesGraph != null)
            {
                foreach (var kvp in blueprint.ModulesGraph)
                {
                    if (kvp.Value == null) continue;
                    
                    var apiSummary = new List<string>();
                    if (kvp.Value.PublicStructs?.Any() == true) apiSummary.Add($"Structs: {string.Join(", ", kvp.Value.PublicStructs)}");
                    if (kvp.Value.PublicEnums?.Any() == true) apiSummary.Add($"Enums: {string.Join(", ", kvp.Value.PublicEnums)}");
                    if (kvp.Value.PublicTraits?.Any() == true) apiSummary.Add($"Traits: {string.Join(", ", kvp.Value.PublicTraits)}");
                    if (kvp.Value.PublicFunctions?.Any() == true) apiSummary.Add($"Fns: {string.Join(", ", kvp.Value.PublicFunctions)}");

                    string typePrefix = kvp.Value.Type switch
                    {
                        ModuleType.BinaryRoot => "🚀 [BIN] ",
                        ModuleType.LibraryRoot => "📚 [LIB] ",
                        ModuleType.IntegrationTest => "🧪 [TEST] ",
                        ModuleType.Benchmark => "⏱️ [BENCH] ",
                        _ => "📄 "
                    };

                    Modules.Add(new ModuleItemViewModel { FilePath = $"{typePrefix}{kvp.Key}", InternalImports = kvp.Value.UsesInternal ?? new(), PublicApi = apiSummary });
                }
            }
        });
    }

    private void ConfigureValidator(string? cargoPath, string relativeFilePath)
    {
        _validator.TargetCargoTomlPath = cargoPath;
        _validator.TargetRelativeFilePath = relativeFilePath;
    }

    private void AppendErrorToMemory(StringBuilder uiBuilder, StringBuilder modelBuilder, int attempt, string failedCode, string errorLog)
    {
        uiBuilder.AppendLine($"======================================================================");
        uiBuilder.AppendLine($"❌ FAILED ATTEMPT #{attempt} — Compilation / Test Pipeline Error");
        uiBuilder.AppendLine($"======================================================================");
        uiBuilder.AppendLine("[Generated Broken Source Code Context]:");
        uiBuilder.AppendLine(failedCode);
        uiBuilder.AppendLine("\n[Raw Diagnostic Output from rustc / cargo / clippy]:");
        uiBuilder.AppendLine(string.IsNullOrWhiteSpace(errorLog) ? "No diagnostic output available." : errorLog);
        uiBuilder.AppendLine("\n");

        modelBuilder.AppendLine($"--- FAILED ATTEMPT #{attempt} DIAGNOSTICS ---");
        modelBuilder.AppendLine(string.IsNullOrWhiteSpace(errorLog) ? "Process crashed or returned no output." : errorLog);
        modelBuilder.AppendLine("---------------------------------------------\n");
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