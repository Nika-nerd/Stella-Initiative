namespace Stella.Core.Models;

public record CompilationResult(bool IsSuccess, string Message, string? CleanCode);