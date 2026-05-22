using System.Collections.Generic;

namespace Stella.UI.ViewModels;

public class ModuleItemViewModel
{
    public string FilePath { get; set; } = string.Empty;
    public List<string> InternalImports { get; set; } = new();
    public List<string> PublicApi { get; set; } = new();
}