using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Stella.Core.Interfaces;
using Stella.Core.Models;

namespace Stella.Infrastructure.Services;

public class DockerValidationService : ICodeValidator
{
    
    private const string ImageName = "rust:1.78"; 

    public async Task<CodeValidationResult> ValidateAsync(string code, CancellationToken ct = default)
    {
        string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string tempDir = Path.Combine(homeDir, ".stella", "temp", $"project_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await PrepareCargoProject(tempDir, code, ct);

           
            string dockerArgs = $"run --rm -v \"{tempDir}\":/usr/src/myapp -w /usr/src/myapp {ImageName} " +
                                "sh -c \"cargo clippy --message-format=json --all-targets -- -W warnings -W clippy::pedantic " +
                                "&& cargo test --message-format=json\"";

            var startInfo = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "docker.exe" : "docker",
                Arguments = dockerArgs, 
                RedirectStandardOutput = true,
                RedirectStandardError = true, 
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return new CodeValidationResult(false, "Docker start failed", new() 
                { 
                    new("error", "Не удалось запустить Docker. Проверь, запущен ли Docker Desktop.", null, null) 
                }, code);
            }
        
            string output = await process.StandardOutput.ReadToEndAsync(ct);
            string errorOutput = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
            {
                return new CodeValidationResult(false, $"Docker exit code {process.ExitCode}", new()
                {
                    new("error", $"Контейнер аварийно завершился. Лог: {errorOutput}", null, null)
                }, code);
            }

            var clippyResult = ParseCargoErrors(output, code);
            if (!clippyResult.IsSuccess)
            {
                return clippyResult;
            }

            return ParseTestResults(output, clippyResult, code);
        }
        catch (Exception ex)
        {
            return new CodeValidationResult(false, ex.Message, new()
            {
                new("error", $"Критический сбой Docker-валидатора: {ex.Message}", null, null)
            }, code);
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
                        
                        issues.Add(new ValidationIssue(level, message, lineNum, 0));
                        cleanErrorsLog.AppendLine($"[Line {lineNum}] {message}");
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
            catch { }
        }

        return new CodeValidationResult(isSuccess, testLog.ToString(), issues, code);
    }
}