using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stella.Core.Interfaces;
using Stella.Core.Models;

namespace Stella.Infrastructure.Services;

public class NativeValidationService : ICodeValidator
{
    public string? TargetCargoTomlPath { get; set; }
    public string TargetRelativeFilePath { get; set; } = "src/main.rs";

    public async Task<CodeValidationResult> ValidateAsync(string code, CancellationToken ct = default)
{
    bool isProjectMode = !string.IsNullOrEmpty(TargetCargoTomlPath) && File.Exists(TargetCargoTomlPath);
    
    string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    string temporarySandbox = Path.Combine(homeDir, ".stella_tmp", "validation", Guid.NewGuid().ToString());
    Directory.CreateDirectory(temporarySandbox);

    try
    {
        if (isProjectMode)
        {
            string projectSrcDir = Path.GetDirectoryName(Path.GetFullPath(TargetCargoTomlPath!))!;
            await CopyProjectStructureAsync(projectSrcDir, temporarySandbox, code, ct);
        }
        else
        {
            await PrepareTemporaryCargoProject(temporarySandbox, code, ct);
        }

        string cargoCmd = "cargo";

        var clippyInfo = new ProcessStartInfo
        {
            FileName = cargoCmd,
            Arguments = "clippy --message-format=json --all-targets -- -W warnings -W clippy::pedantic",
            WorkingDirectory = temporarySandbox,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var testInfo = new ProcessStartInfo
        {
            FileName = cargoCmd,
            Arguments = "test --message-format=json",
            WorkingDirectory = temporarySandbox,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var clippyProcess = Process.Start(clippyInfo);
        using var testProcess = Process.Start(testInfo);

        if (clippyProcess == null || testProcess == null)
            return new CodeValidationResult(false, "Process start failed", new(), code);

        Task<string> clippyTask = clippyProcess.StandardOutput.ReadToEndAsync(ct);
        Task<string> testTask = testProcess.StandardOutput.ReadToEndAsync(ct);

        await Task.WhenAll(clippyTask, testTask, clippyProcess.WaitForExitAsync(ct), testProcess.WaitForExitAsync(ct));

        var clippyResult = ParseCargoErrors(clippyTask.Result, code);
        var finalResult = ParseTestResults(testTask.Result, clippyResult, code);

        return finalResult;
    }
    catch (Exception ex)
    {
        return new CodeValidationResult(false, ex.Message, new() { new("error", $"Native validator crashed: {ex.Message}", null, null) }, code);
    }
    finally
    {
        if (Directory.Exists(temporarySandbox))
        {
            try { Directory.Delete(temporarySandbox, true); } catch { }
        }
    }
}
public async Task ApplyChangesAsync(string code, CancellationToken ct = default)
{
    if (string.IsNullOrEmpty(TargetCargoTomlPath)) return;
    
    string projectDir = Path.GetDirectoryName(Path.GetFullPath(TargetCargoTomlPath))!;
    string fullTargetFilePath = Path.Combine(projectDir, TargetRelativeFilePath);
    
    string? fileDir = Path.GetDirectoryName(fullTargetFilePath);
    if (!string.IsNullOrEmpty(fileDir)) Directory.CreateDirectory(fileDir);
    
    await File.WriteAllTextAsync(fullTargetFilePath, code, ct);
}

private async Task CopyProjectStructureAsync(string sourceDir, string destDir, string newCode, CancellationToken ct)
{
    string cargoToml = Path.Combine(sourceDir, "Cargo.toml");
    if (File.Exists(cargoToml))
    {
        File.Copy(cargoToml, Path.Combine(destDir, "Cargo.toml"), true);
    }

    string targetFileInSandbox = Path.Combine(destDir, TargetRelativeFilePath);
    string? sandboxFileDir = Path.GetDirectoryName(targetFileInSandbox);
    if (!string.IsNullOrEmpty(sandboxFileDir)) Directory.CreateDirectory(sandboxFileDir);
    
    await File.WriteAllTextAsync(targetFileInSandbox, newCode, ct);

    string srcSource = Path.Combine(sourceDir, "src");
    string srcDest = Path.Combine(destDir, "src");
    
    if (Directory.Exists(srcSource))
    {
        Directory.CreateDirectory(srcDest);
        foreach (string file in Directory.GetFiles(srcSource, "*.rs", SearchOption.AllDirectories))
        {
            string relPath = Path.GetRelativePath(srcSource, file);
            string destFile = Path.Combine(srcDest, relPath);
            
            if (Path.GetFullPath(destFile) == Path.GetFullPath(targetFileInSandbox)) continue;

            string? dDir = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(dDir)) Directory.CreateDirectory(dDir);
            
            File.Copy(file, destFile, true);
        }
    }
}

    private async Task PrepareTemporaryCargoProject(string path, string code, CancellationToken ct)
    {
        string cargoTomlContent = "[package]\nname = 'stella_temp_native'\nversion = '0.1.0'\nedition = '2021'\n\n[dependencies]\n";
        await File.WriteAllTextAsync(Path.Combine(path, "Cargo.toml"), cargoTomlContent, ct);

        string fullTargetFilePath = Path.Combine(path, TargetRelativeFilePath);
        string? directoryPath = Path.GetDirectoryName(fullTargetFilePath);
        if (!string.IsNullOrEmpty(directoryPath)) Directory.CreateDirectory(directoryPath);

        await File.WriteAllTextAsync(fullTargetFilePath, code, ct);
    }

    private CodeValidationResult ParseCargoErrors(string rawJson, string code)
    {
        var issues = new List<ValidationIssue>();
        var cleanErrorsLog = new StringBuilder();

        if (string.IsNullOrWhiteSpace(rawJson)) return new CodeValidationResult(true, "", issues, code);

        var lines = rawJson.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (root.TryGetProperty("reason", out var reason) && reason.GetString() == "compiler-message")
                {
                    var messageNode = root.GetProperty("message");
                    string level = messageNode.GetProperty("level").GetString() ?? "warning";

                    if (level.ToLower() == "error" || level.ToLower() == "deny")
                    {
                        string message = messageNode.GetProperty("message").GetString() ?? "";
                        int? lineNum = null;

                        if (messageNode.TryGetProperty("spans", out var spans) && spans.GetArrayLength() > 0)
                        {
                            var firstSpan = spans[0];
                            if (firstSpan.TryGetProperty("line_start", out var lineProp))
                            {
                                lineNum = lineProp.GetInt32();
                            }
                        }

                        string errorCode = "";
                        if (messageNode.TryGetProperty("code", out var codeProp) && codeProp.ValueKind != JsonValueKind.Null)
                        {
                            if (codeProp.TryGetProperty("code", out var innerCode))
                            {
                                errorCode = innerCode.GetString() ?? "";
                            }
                        }

                        issues.Add(new ValidationIssue(level, message, lineNum, 0));
                        string clippyTag = string.IsNullOrEmpty(errorCode) ? "" : $" [{errorCode}]";
                        cleanErrorsLog.AppendLine($"[Line {lineNum}]{clippyTag} {message}");
                    }
                }
            }
            catch { }
        }

        return new CodeValidationResult(!issues.Any(), cleanErrorsLog.ToString(), issues, code);
    }

    private CodeValidationResult ParseTestResults(string rawJson, CodeValidationResult prev, string code)
    {
        var issues = new List<ValidationIssue>(prev.Issues);
        var testLog = new StringBuilder(prev.RawOutput);
        bool isSuccess = prev.IsSuccess;

        if (string.IsNullOrWhiteSpace(rawJson)) return prev;

        var lines = rawJson.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (root.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "test")
                {
                    if (root.TryGetProperty("event", out var testEvent) && root.TryGetProperty("name", out var testName))
                    {
                        if (testEvent.GetString() == "failed")
                        {
                            isSuccess = false;
                            string name = testName.GetString() ?? "unknown";
                            string stdout = root.TryGetProperty("stdout", out var outProp) ? outProp.GetString() ?? "" : "";

                            issues.Add(new ValidationIssue("error", $"Test '{name}' failed.", null, null));
                            testLog.AppendLine($"[Test Failed] '{name}'\nLog: {stdout}");
                        }
                    }
                }
            }
            catch { }
        }

        return new CodeValidationResult(isSuccess, testLog.ToString(), issues, code);
    }
}