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
            if (!File.Exists(cargoPath)) cargoPath = "cargo";

            var startInfo = new ProcessStartInfo
            {
                FileName = cargoPath,
                Arguments = "check --message-format=json", 
                WorkingDirectory = tempDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return new CodeValidationResult(true, "", new List<ValidationIssue>(), code);
            }

            string output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            var validationResult = ParseCargoErrors(output, code);
            return validationResult;
        }
        catch
        {
            return new CodeValidationResult(true, "", new List<ValidationIssue>(), code);
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
            return new CodeValidationResult(true, rawJson, issues, code);
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
                    
                    if (level.ToLower() == "error")
                    {
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
            }
            catch { }
        }

        bool isSuccess = !issues.Any();
        return new CodeValidationResult(isSuccess, rawJson, issues, code);
    }
}