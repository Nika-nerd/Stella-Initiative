using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stella.Core.Interfaces;
using Stella.Core.Models;

namespace Stella.Infrastructure.Services;

public class RustProjectAnalyzer : IProjectAnalyzer
{
    private static readonly Regex ModRegex = new(@"^\s*(?:pub\s+)?mod\s+([a-zA-Z0-9_]+);", RegexOptions.Compiled);
    private static readonly Regex UseRegex = new(@"^\s*(?:pub\s+)?use\s+(.+);", RegexOptions.Compiled);
    
    private static readonly Regex StructRegex = new(@"^\s*pub\s+struct\s+([a-zA-Z0-9_]+)", RegexOptions.Compiled);
    private static readonly Regex EnumRegex = new(@"^\s*pub\s+enum\s+([a-zA-Z0-9_]+)", RegexOptions.Compiled);
    private static readonly Regex TraitRegex = new(@"^\s*pub\s+trait\s+([a-zA-Z0-9_]+)", RegexOptions.Compiled);
    private static readonly Regex FnRegex = new(@"^\s*pub\s+(?:async\s+)?fn\s+([a-zA-Z0-9_]+)", RegexOptions.Compiled);

    public async Task<ProjectBlueprint> AnalyzeProjectAsync(string projectPath, CancellationToken ct = default)
    {
        var blueprint = new ProjectBlueprint();

        if (!Directory.Exists(projectPath))
            return blueprint;

        string cargoTomlPath = Path.Combine(projectPath, "Cargo.toml");
        if (File.Exists(cargoTomlPath))
        {
            await ParseCargoTomlAsync(cargoTomlPath, blueprint, ct);
        }

        var rustFiles = Directory.GetFiles(projectPath, "*.rs", SearchOption.AllDirectories);
        foreach (var file in rustFiles)
        {
            ct.ThrowIfCancellationRequested();
    
            string relativePath = Path.GetRelativePath(projectPath, file);
    
            relativePath = relativePath.Replace(Path.DirectorySeparatorChar, '/');
    
            if (relativePath.StartsWith("target/")) continue;

            var moduleInfo = await AnalyzeRustFileAsync(relativePath, file, ct);
            blueprint.ModulesGraph[relativePath] = moduleInfo;
        }

        return blueprint;
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
            string targetRsFile = ResolveImportToFilePath(import);
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

    private string ResolveImportToFilePath(string import)
    {
        string clean = import.Replace("crate::", "").Replace("super::", "");
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

    private async Task ParseCargoTomlAsync(string filePath, ProjectBlueprint blueprint, CancellationToken ct)
    {
        var lines = await File.ReadAllLinesAsync(filePath, ct);
        bool inDependencies = false;

        foreach (var line in lines)
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;

            if (trimmed.StartsWith("[package]")) { inDependencies = false; continue; }
            if (trimmed.StartsWith("[dependencies]")) { inDependencies = true; continue; }
            if (trimmed.StartsWith("[")) { inDependencies = false; continue; }

            if (!inDependencies)
            {
                if (trimmed.StartsWith("name"))
                {
                    var match = Regex.Match(trimmed, @"name\s*=\s*""([^""]+)""|name\s*=\s*'([^']+)'");
                    if (match.Success) blueprint.ProjectName = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                }
                else if (trimmed.StartsWith("edition"))
                {
                    var match = Regex.Match(trimmed, @"edition\s*=\s*""([^""]+)""|edition\s*=\s*'([^']+)'");
                    if (match.Success) blueprint.RustEdition = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                }
            }
            else
            {
                var eqIndex = trimmed.IndexOf('=');
                if (eqIndex > 0)
                {
                    blueprint.Dependencies.Add(trimmed.Substring(0, eqIndex).Trim());
                }
            }
        }
    }

    private async Task<ModuleInfo> AnalyzeRustFileAsync(string relativePath, string absolutePath, CancellationToken ct)
    {
        var modInfo = new ModuleInfo();
        
        if (relativePath.EndsWith("src/main.rs")) modInfo.Type = ModuleType.BinaryRoot;
        else if (relativePath.EndsWith("src/lib.rs")) modInfo.Type = ModuleType.LibraryRoot;
        else if (relativePath.StartsWith("tests/")) modInfo.Type = ModuleType.IntegrationTest;
        else if (relativePath.StartsWith("benches/")) modInfo.Type = ModuleType.Benchmark;
        else modInfo.Type = ModuleType.NormalModule;

        var lines = await File.ReadAllLinesAsync(absolutePath, ct);

        foreach (var line in lines)
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("//")) continue;

            var modMatch = ModRegex.Match(line);
            if (modMatch.Success) { modInfo.DeclaresModules.Add(modMatch.Groups[1].Value); continue; }

            var useMatch = UseRegex.Match(line);
            if (useMatch.Success)
            {
                string usePath = useMatch.Groups[1].Value;
                if (usePath.StartsWith("crate::") || usePath.StartsWith("super::") || usePath.StartsWith("self::"))
                    modInfo.UsesInternal.Add(usePath);
                else
                    modInfo.UsesExternal.Add(usePath);
                continue;
            }

            var structMatch = StructRegex.Match(line);
            if (structMatch.Success) { modInfo.PublicStructs.Add(structMatch.Groups[1].Value); continue; }

            var enumMatch = EnumRegex.Match(line);
            if (enumMatch.Success) { modInfo.PublicEnums.Add(enumMatch.Groups[1].Value); continue; }

            var traitMatch = TraitRegex.Match(line);
            if (traitMatch.Success) { modInfo.PublicTraits.Add( traitMatch.Groups[1].Value); continue; }

            var fnMatch = FnRegex.Match(line);
            if (fnMatch.Success) { modInfo.PublicFunctions.Add(fnMatch.Groups[1].Value); }
        }
        
        return modInfo;
    }
}