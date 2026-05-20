using System.Diagnostics;
using System.Text.Json;
using Stella.Core.Interfaces;
using Stella.Core.Models;

namespace Stella.Infrastructure.Services;

public class DockerValidationService : ICodeValidator
{
    private const string ImageName = "rust:1.78-slim"; 

    public async Task<CodeValidationResult> ValidateAsync(string code, CancellationToken ct = default)
    {
        string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string tempDir = Path.Combine(homeDir, ".stella", "temp", $"project_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await PrepareCargoProject(tempDir, code, ct);

            string dockerArgs = $"run --rm -v \"{tempDir}\":/usr/src/myapp -w /usr/src/myapp {ImageName} " +
                                "sh -c \"rustup component add clippy rustfmt > /dev/null 2>&1 " +
                                "&& rustfmt src/main.rs " + 
                                "&& cargo clippy --message-format=json --all-targets -- -W warnings -W clippy::pedantic\"";

            var startInfo = new ProcessStartInfo
            {
                FileName = "/usr/local/bin/docker",
                Arguments = dockerArgs, 
                RedirectStandardOutput = true,
                RedirectStandardError = true, 
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (!File.Exists(startInfo.FileName))
            {
                startInfo.FileName = "docker";
            }

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return new CodeValidationResult(false, "", new List<ValidationIssue> 
                { 
                    new("error", "Не удалось запустить Docker. Проверь, запущен ли Docker Desktop.", null, null) 
                });
            }
        
            string output = await process.StandardOutput.ReadToEndAsync(ct);
            string errorOutput = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
            {
                return new CodeValidationResult(false, errorOutput, new List<ValidationIssue>
                {
                    new("error", $"Docker упал с кодом {process.ExitCode}. Лог: {errorOutput}", null, null)
                });
            }

            return ParseCargoErrors(output, code);
        }
        catch (Exception ex)
        {
            return new CodeValidationResult(false, ex.Message, new List<ValidationIssue>
            {
                new("error", $"Критический сбой Docker-валидатора: {ex.Message}", null, null)
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
tokio = { version = '1.0', features = ['full'] }
async-trait = '0.1'
";
        await File.WriteAllTextAsync(Path.Combine(path, "Cargo.toml"), cargoToml, ct);
        await File.WriteAllTextAsync(Path.Combine(path, "src/main.rs"), code, ct);
    }

    private CodeValidationResult ParseCargoErrors(string rawJson, string code)
    {
        var issues = new List<ValidationIssue>();
        
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            issues.Add(new ValidationIssue("error", "Контейнер Docker не вернул логов компиляции.", null, null));
            return new CodeValidationResult(false, rawJson, issues, code);
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

       
        bool hasErrors = issues.Any(i => i.Severity.ToLower() == "error" || i.Severity.ToLower() == "deny");
        bool isSuccess = !hasErrors; 

        return new CodeValidationResult(isSuccess, rawJson, issues, code);
    }
}