using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Stella.Core.Interfaces;
using Stella.Core.Models;

namespace Stella.Infrastructure.Services;

public class DockerValidationService : ICodeValidator
{
    private const string ImageName = "rust:1.78-slim"; 

    public async Task<CodeValidationResult> ValidateAsync(string code, CancellationToken ct = default)
    {
       
        string tempDir = Path.Combine("/tmp", $"stella_check_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        string filePath = Path.Combine(tempDir, "main.rs");

        try
        {
           
            await File.WriteAllTextAsync(filePath, code, ct);

            
            var startInfo = new ProcessStartInfo
            {
                FileName = "/usr/local/bin/docker",
                Arguments = $"run --rm -v \"{tempDir}\":/usr/src/myapp -w /usr/src/myapp {ImageName} rustc --error-format=json main.rs",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            string output = await process.StandardError.ReadToEndAsync(ct); 
            await process.WaitForExitAsync(ct);

            return ParseRustErrors(output);
        }
        finally
        {
            
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    private CodeValidationResult ParseRustErrors(string rawJson)
    {
        var issues = new List<ValidationIssue>();
        var lines = rawJson.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            try 
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                
                if (root.TryGetProperty("message", out var msgElement))
                {
                    string message = msgElement.GetString() ?? "";
                    string level = root.GetProperty("level").GetString() ?? "note";
                    
                    
                    int? lineNum = null;
                    if (root.TryGetProperty("spans", out var spans) && spans.GetArrayLength() > 0)
                    {
                        lineNum = spans[0].GetProperty("line_start").GetInt32();
                    }

                    issues.Add(new ValidationIssue(level, message, lineNum, 0));
                }
            }
            catch {}
        }

        return new CodeValidationResult(issues.Count == 0, rawJson, issues);
    }
}