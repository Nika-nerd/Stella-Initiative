using System.Threading;
using System.Threading.Tasks;
using Stella.Core.Models;

namespace Stella.Core.Interfaces;

public interface ICodeValidator
{
    string? TargetCargoTomlPath { get; set; }
    string TargetRelativeFilePath { get; set; }
    Task<CodeValidationResult> ValidateAsync(string code, CancellationToken ct =  default);
    
    Task ApplyChangesAsync(string code, CancellationToken ct = default);
}


public interface ILLMService
{
    Task<string> GenerateCodeAsync(string prompt, int attempt = 1, double temperature = 0.0);
}