
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
        
        string tempDir = Path.Combine(Path.GetTempPath(), $"stella_project_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            
            await PrepareCargoProject(tempDir, code, ct);

            
            string dockerArgs = $"run --rm -v \"{tempDir}\":/usr/src/myapp -w /usr/src/myapp {ImageName} " +
                                "sh -c \"rustup component add clippy rustfmt > /dev/null 2>&1 " +
                                "&& rustfmt src/main.rs " + 
                                "&& cargo clippy --message-format=json\"";


            if (File.Exists(Path.Combine(tempDir, "src/main.rs")))
            {
                
                string formattedCode = await File.ReadAllTextAsync(Path.Combine(tempDir, "src/main.rs"), ct);
            
            }
            
            
            var startInfo = new ProcessStartInfo
            {
                FileName = "docker", 
                Arguments = dockerArgs,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            
            string output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            return ParseCargoErrors(output);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch {  }
            }
        }
    }

    private async Task PrepareCargoProject(string path, string code, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.Combine(path, "src"));
    
        
        var match = System.Text.RegularExpressions.Regex.Match(code, @"//\s*crates:\s*(.*)");
        string deps = "";
    
        if (match.Success)
        {
            var crateList = match.Groups[1].Value.Split(',');
            foreach (var crate in crateList)
            {
                
                deps += $"{crate.Trim()} = \"*\"\n";
            }
        }

        string cargoToml = "[package]\n" +
                           "name = \"stella_temp\"\n" +
                           "version = \"0.1.0\"\n" +
                           "edition = \"2021\"\n" +
                           "[dependencies]\n" + deps;

        await File.WriteAllTextAsync(Path.Combine(path, "Cargo.toml"), cargoToml, ct);
        await File.WriteAllTextAsync(Path.Combine(path, "src/main.rs"), code, ct);
    }

    private CodeValidationResult ParseCargoErrors(string rawJson)
    {
        var issues = new List<ValidationIssue>();
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
            catch {  }
        }

        
        bool isSuccess = !issues.Any(i => i.Severity.ToLower() == "error" || i.Severity.ToLower() == "warning");
        return new CodeValidationResult(isSuccess, rawJson, issues);
    }
}