using System.Collections.Generic;

namespace Stella.Core.Models;

public class ProjectBlueprint
{
    public string ProjectName { get; set; } = "unknown";
    public string RustEdition { get; set; } = "2021";
    public List<string> Dependencies { get; set; } = new();
    public Dictionary<string, ModuleInfo> ModulesGraph { get; set; } = new();
}

public enum ModuleType
{
    BinaryRoot, 
    LibraryRoot, 
    NormalModule, 
    IntegrationTest, 
    Benchmark
}

public class ModuleInfo
{
    public ModuleType Type { get; set; } = ModuleType.NormalModule;
    public List<string> DeclaresModules { get; set; } = new();
    public List<string> UsesExternal { get; set; } = new();
    public List<string> UsesInternal { get; set; } = new();
    
    
    public List<string> PublicStructs { get; set; } = new();
    public List<string> PublicEnums { get; set; } = new();
    public List<string> PublicTraits { get; set; } = new();
    public List<string> PublicFunctions { get; set; } = new();
}

public enum StellaWorkMode
{
    Sandbox,
    Project
}