using Stella.Core.Models;

namespace Stella.Core.Interfaces;

public interface ICodeValidator
{
    Task<CodeValidationResult> ValidateAsync(string code, CancellationToken ct =  default);
}


public interface ILLMService
{
    Task<string> GenerateCodeAsync(string prompt, int attempt = 1, double temperature = 0.0);
}