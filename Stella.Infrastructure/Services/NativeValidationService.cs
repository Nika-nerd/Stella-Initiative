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
    public async Task<CodeValidationResult> ValidateAsync(string code, CancellationToken ct = default)
    {
        string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string tempDir = Path.Combine(Path.GetTempPath(), "stella_projects", Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            await PrepareCargoProject(tempDir, code, ct);

            string cargoPath = Path.Combine(homeDir, ".cargo", "bin", OperatingSystem.IsWindows() ? "cargo.exe" : "cargo");

            if (!File.Exists(cargoPath)) 
            {
                cargoPath = OperatingSystem.IsWindows() ? "cargo.exe" : "cargo";
            }

            var clippyInfo = new ProcessStartInfo
            {
                FileName = cargoPath,
                Arguments = "clippy --message-format=json --all-targets -- -W warnings -W clippy::pedantic",
                WorkingDirectory = tempDir,
                RedirectStandardOutput = true,
                RedirectStandardError = false, 
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var clippyProcess = Process.Start(clippyInfo);
            if (clippyProcess == null)
                return new CodeValidationResult(false, "Cargo start failed", new() { new("error", "Cargo not found.", null, null) }, code);

            string clippyOutput = await clippyProcess.StandardOutput.ReadToEndAsync(ct);
            await clippyProcess.WaitForExitAsync(ct);

            var result = ParseCargoErrors(clippyOutput, code);
            if (!result.IsSuccess) return result; 

            var testInfo = new ProcessStartInfo
            {
                FileName = cargoPath,
                Arguments = "test --message-format=json",
                WorkingDirectory = tempDir,
                RedirectStandardOutput = true,
                RedirectStandardError = false, 
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var testProcess = Process.Start(testInfo);
            if (testProcess != null)
            {
                string testOutput = await testProcess.StandardOutput.ReadToEndAsync(ct);
                await testProcess.WaitForExitAsync(ct);
                result = ParseTestResults(testOutput, result, code);
            }

            return result;
        }
        catch (Exception ex)
        {
            return new CodeValidationResult(false, ex.Message, new() { new("error", ex.Message, null, null) }, code);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    private async Task PrepareCargoProject(string path, string code, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.Combine(path, "src"));
        string cargoToml = "[package]\nname = 'stella_temp'\nversion = '0.1.0'\nedition = '2021'\n\n[dependencies]\nserde = { version = '1.0', features = ['derive'] }\nserde_json = '1.0'\ntokio = { version = '1.0', features = ['full'] }\nasync-trait = '0.1'\n";
        await File.WriteAllTextAsync(Path.Combine(path, "Cargo.toml"), cargoToml, ct);
        await File.WriteAllTextAsync(Path.Combine(path, "src/main.rs"), code, ct);
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
                        
                        var spans = messageNode.GetProperty("spans");
                        if (spans.GetArrayLength() > 0) lineNum = spans[0].GetProperty("line_start").GetInt32();
                        
                        string errorCode = "";
                        if (messageNode.TryGetProperty("code", out var codeProp) && !codeProp.ValueKind.Equals(JsonValueKind.Null))
                        {
                            errorCode = codeProp.GetProperty("code").GetString() ?? "";
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