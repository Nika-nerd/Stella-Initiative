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

public class DockerValidationService : ICodeValidator
{
    private const string ImageName = "rust:1.78"; 

    public string? TargetCargoTomlPath { get; set; }
    public string TargetRelativeFilePath { get; set; } = "src/main.rs";

    public async Task<CodeValidationResult> ValidateAsync(string code, CancellationToken ct = default)
    {
        string baseTempDir = OperatingSystem.IsMacOS() 
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".stella_tmp", Guid.NewGuid().ToString())
            : Path.Combine(Path.GetTempPath(), "stella_docker", Guid.NewGuid().ToString());
        
        string tempDir = Path.GetFullPath(baseTempDir);
        Directory.CreateDirectory(tempDir);

        try
        {
            await PrepareCargoProject(tempDir, code, ct);

            string dockerCmd = OperatingSystem.IsWindows() ? "docker.exe" : "docker";
            string clippyArgs = $"run --rm -v \"{tempDir}\":/usr/src/myapp -w /usr/src/myapp {ImageName} " +
                                "cargo clippy --message-format=json --all-targets -- -W warnings -W clippy::pedantic";

            var clippyInfo = new ProcessStartInfo
            {
                FileName = dockerCmd,
                Arguments = clippyArgs, 
                RedirectStandardOutput = true,
                RedirectStandardError = false, 
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var clippyProcess = Process.Start(clippyInfo);
            if (clippyProcess == null)
                return new CodeValidationResult(false, "Docker start failed", new() { new("error", "Не удалось запустить Docker.", null, null) }, code);

            string clippyOutput = await clippyProcess.StandardOutput.ReadToEndAsync(ct);
            await clippyProcess.WaitForExitAsync(ct);

            var result = ParseCargoErrors(clippyOutput, code);
            if (!result.IsSuccess) return result;

            string testArgs = $"run --rm -v \"{tempDir}\":/usr/src/myapp -w /usr/src/myapp {ImageName} " +
                              "cargo test --message-format=json";

            var testInfo = new ProcessStartInfo
            {
                FileName = dockerCmd,
                Arguments = testArgs, 
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
            return new CodeValidationResult(false, ex.Message, new() { new("error", $"Docker validator crashed: {ex.Message}", null, null) }, code);
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
        string cargoTomlContent = "[package]\nname = 'stella_temp'\nversion = '0.1.0'\nedition = '2021'\n\n[dependencies]\n";
        if (!string.IsNullOrEmpty(TargetCargoTomlPath) && File.Exists(TargetCargoTomlPath))
        {
            cargoTomlContent = await File.ReadAllTextAsync(TargetCargoTomlPath, ct);
            if (cargoTomlContent.Contains("name ="))
            {
                cargoTomlContent = System.Text.RegularExpressions.Regex.Replace(cargoTomlContent, @"name\s*=\s*""[^""]+""", "name = \"stella_temp\"");
            }
        }

        await File.WriteAllTextAsync(Path.Combine(path, "Cargo.toml"), cargoTomlContent, ct);

        string fullTargetFilePath = Path.Combine(path, TargetRelativeFilePath);
        string? directoryPath = Path.GetDirectoryName(fullTargetFilePath);
        if (!string.IsNullOrEmpty(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

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