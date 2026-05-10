using Stella.Core.Models;

namespace Stella.Core.Interfaces;

public interface ICodeValidator
{
    Task<CompilationResult> ValidateAsync(string code);
}


public interface ILLMService
{
    Task<string> GenerateCodeAsync(string prompt, string cartridgeId);
}