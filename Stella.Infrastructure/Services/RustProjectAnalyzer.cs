using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stella.Core.Interfaces;
using Stella.Core.Models;

namespace Stella.Infrastructure.Services;

public class RustProjectAnalyzer : IProjectAnalyzer
{
    private readonly string _lensBinaryPath;

    public RustProjectAnalyzer()
    {
        string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _lensBinaryPath = Path.Combine(homeDir, ".stella_bin", OperatingSystem.IsWindows() ? "stella_lens.exe" : "stella_lens");
        
        if (!File.Exists(_lensBinaryPath))
        {
            _lensBinaryPath = OperatingSystem.IsWindows() ? "stella_lens.exe" : "stella_lens";
        }
    }

   
    public async Task<ProjectBlueprint> AnalyzeProjectAsync(string projectPath, CancellationToken ct = default)
    {
        if (!Directory.Exists(projectPath))
            throw new DirectoryNotFoundException($"Target project directory not found: {projectPath}");

        var startInfo = new ProcessStartInfo
        {
            FileName = _lensBinaryPath,
            Arguments = $"\"{projectPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process == null)
            throw new InvalidOperationException("Failed to start Stella Lens analyzer utility.");

        string jsonOutput = await process.StandardOutput.ReadToEndAsync(ct);
        string errorOutput = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(errorOutput))
        {
            throw new Exception($"Stella Lens crashed with code {process.ExitCode}. Error: {errorOutput}");
        }

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            AllowTrailingCommas = true
        };

        try
        {
            var blueprint = JsonSerializer.Deserialize<ProjectBlueprint>(jsonOutput, jsonOptions);
            return blueprint ?? throw new NullReferenceException("Deserialized blueprint is null.");
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to parse token mapping map from Stella Lens. Raw JSON length: {jsonOutput.Length}. Details: {ex.Message}");
        }
    }

    
    public async Task<string> TraceAndExtractDependenciesAsync(string projectPath, ProjectBlueprint blueprint, string targetFileRelativePath, CancellationToken ct = default)
    {
        if (!blueprint.ModulesGraph.ContainsKey(targetFileRelativePath))
            return string.Empty;

        var targetModule = blueprint.ModulesGraph[targetFileRelativePath];
        var sb = new StringBuilder();

        foreach (var import in targetModule.UsesInternal)
        {
            ct.ThrowIfCancellationRequested();
            string targetRsFile = ResolveImportToFilePath(import, blueprint.ProjectName);
            string fullPath = Path.Combine(projectPath, targetRsFile);

            if (!File.Exists(fullPath)) continue;

            var entitiesToFind = ExtractEntityNames(import);
            if (entitiesToFind.Count == 0) continue;

            string fileContent = await File.ReadAllTextAsync(fullPath, ct);
            
            sb.AppendLine($"// --- Automatically extracted from {targetRsFile} for context ---");
            foreach (var entity in entitiesToFind)
            {
                string entityBlock = ExtractTargetEntityBlock(fileContent, entity);
                if (!string.IsNullOrEmpty(entityBlock))
                {
                    sb.AppendLine(entityBlock);
                    sb.AppendLine();
                }
            }
        }

        return sb.ToString();
    }

    private string ResolveImportToFilePath(string import, string projectName)
    {
        string clean = import.Replace("crate::", "")
                             .Replace("super::", "")
                             .Replace("self::", "");
        
        if (!string.IsNullOrEmpty(projectName) && clean.StartsWith($"{projectName}::"))
        {
            clean = clean.Substring(projectName.Length + 2);
        }

        var parts = clean.Split(new[] { "::" }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "src/main.rs";

        return $"src/{parts[0]}.rs";
    }

    private List<string> ExtractEntityNames(string import)
    {
        var result = new List<string>();
        if (import.Contains('{'))
        {
            var match = Regex.Match(import, @"\{([^}]+)\}");
            if (match.Success)
            {
                foreach (var name in match.Groups[1].Value.Split(',')) 
                    result.Add(name.Trim());
            }
        }
        else
        {
            var parts = import.Split(new[] { "::" }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0) result.Add(parts[^1].Trim());
        }
        return result;
    }

    private string ExtractTargetEntityBlock(string fileContent, string entityName)
    {
        string pattern = $@"pub\s+(struct|enum|trait)\s+{entityName}\b";
        var match = Regex.Match(fileContent, pattern);
        if (!match.Success) return string.Empty;

        int startIndex = match.Index;
        int openBraces = 0;
        int endIndex = -1;
        bool foundFirstBrace = false;

        for (int i = startIndex; i < fileContent.Length; i++)
        {
            if (fileContent[i] == '{') { openBraces++; foundFirstBrace = true; }
            else if (fileContent[i] == '}') { openBraces--; }

            if (foundFirstBrace && openBraces == 0) { endIndex = i; break; }
            if (!foundFirstBrace && fileContent[i] == ';') { endIndex = i; break; }
        }

        return endIndex != -1 ? fileContent.Substring(startIndex, endIndex - startIndex + 1) : string.Empty;
    }
}