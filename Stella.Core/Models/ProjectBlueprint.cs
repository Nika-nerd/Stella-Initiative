using System.Collections.Generic;

namespace Stella.Core.Models;

public class ProjectBlueprint
{
    public string ProjectName { get; set; } = "unknown";
    public string RustEdition { get; set; } = "2021";
    public List<string> Dependencies { get; set; } = new();
    public Dictionary<string, ModuleInfo> ModulesGraph { get; set; } = new();
}

public class ModuleInfo
{
    public List<string> DeclaresModules { get; set; } = new();
    
    public List<string> UsesExternal { get; set; } = new();
    
    public List<string> UsesInternal { get; set; } = new();
    
    public List<string> PublicDefinitions { get; set; } = new();
}