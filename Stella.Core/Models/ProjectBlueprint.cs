using System.Collections.Generic;
using System.Text.Json.Serialization;

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
    [JsonPropertyName("module_type")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ModuleType Type { get; set; } = ModuleType.NormalModule;
    
    [JsonPropertyName("declares_modules")]
    public List<string> DeclaresModules { get; set; } = new();
    
    [JsonPropertyName("uses_external")]
    public List<string> UsesExternal { get; set; } = new();
    
    [JsonPropertyName("uses_internal")]
    public List<string> UsesInternal { get; set; } = new();
    
    
    [JsonPropertyName("public_structs")]
    public List<string> PublicStructs { get; set; } = new();
    [JsonPropertyName("public_enums")]
    public List<string> PublicEnums { get; set; } = new();
    [JsonPropertyName("public_traits")]
    public List<string> PublicTraits { get; set; } = new();
    [JsonPropertyName("public_functions")]
    public List<string> PublicFunctions { get; set; } = new();
}

public enum StellaWorkMode
{
    Sandbox,
    Project
}