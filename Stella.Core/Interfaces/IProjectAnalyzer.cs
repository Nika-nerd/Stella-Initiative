using System.Threading;
using System.Threading.Tasks;
using Stella.Core.Models;

namespace Stella.Core.Interfaces;

public interface IProjectAnalyzer
{
    
    Task<ProjectBlueprint> AnalyzeProjectAsync(string projectPath, CancellationToken ct = default);
    
    Task<string> TraceAndExtractDependenciesAsync(string projectPath, ProjectBlueprint blueprint, string targetFileRelativePath, CancellationToken ct = default);
}