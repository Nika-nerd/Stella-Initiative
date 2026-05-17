namespace Stella.Core.Models;

public record ValidationIssue(string Severity,
    string Message,
    int? Line,
    int? Column);
    
    public record CodeValidationResult(
        bool IsSuccess,
        string RawOutput,
        List<ValidationIssue> Issues, string? UpdatedCode = null);