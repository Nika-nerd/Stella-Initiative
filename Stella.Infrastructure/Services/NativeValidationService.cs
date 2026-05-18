using System.Diagnostics;
using System.Text.Json;
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

            string cargoPath = Path.Combine(homeDir, ".cargo", "bin", "cargo");
            if (!File.Exists(cargoPath))
            {
                cargoPath = "cargo";
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = cargoPath,
                Arguments = "clippy --message-format=json --all-targets -- -W warnings -W clippy::pedantic",
                WorkingDirectory = tempDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return new CodeValidationResult(false, "", new List<ValidationIssue> 
                { 
                    new("error", "Не удалось запустить локальный процесс cargo. Проверь установку Rustup.", null, null) 
                });
            }

            string output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            var validationResult = ParseCargoErrors(output, code);
            return new CodeValidationResult(validationResult.IsSuccess, output, validationResult.Issues, code);
        }
        catch (Exception ex)
        {
            return new CodeValidationResult(false, ex.Message, new List<ValidationIssue>
            {
                new("error", $"Ошибка нативного валидатора: {ex.Message}", null, null)
            });
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

        string cargoToml = @"
[package]
name = 'stella_temp'
version = '0.1.0'
edition = '2021'

[dependencies]
serde = { version = '1.0', features = ['derive'] }
serde_json = '1.0'
";

        await File.WriteAllTextAsync(Path.Combine(path, "Cargo.toml"), cargoToml, ct);
        await File.WriteAllTextAsync(Path.Combine(path, "src/main.rs"), code, ct);
    }

    private CodeValidationResult ParseCargoErrors(string rawJson,  string code)
    {
        var issues = new List<ValidationIssue>();
        
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            issues.Add(new ValidationIssue("error", "Локальный компилятор Rust не вернул данных. Проверь синтаксис.", null, null));
            return new CodeValidationResult(false, rawJson, issues,  code);
        }

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
                    string message = messageNode.GetProperty("message").GetString() ?? "";
                    
                    int? lineNum = null;
                    var spans = messageNode.GetProperty("spans");
                    if (spans.GetArrayLength() > 0)
                    {
                        lineNum = spans[0].GetProperty("line_start").GetInt32();
                    }

                    issues.Add(new ValidationIssue(level, message, lineNum, 0));
                }
            }
            catch { }
        }

        
        bool isSuccess = !issues.Any(i => i.Severity.ToLower() == "error" || i.Severity.ToLower() == "deny");


        if (issues.Count == 0)
        {
            isSuccess = true;
        }

        return new CodeValidationResult(isSuccess, rawJson, issues, code);
    }
}