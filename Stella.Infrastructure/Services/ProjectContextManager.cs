using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Stella.Infrastructure.Services;

public class ProjectContextManager
{
    private readonly string _stellaLensPath;
    public string CurrentProjectBlueprintJson { get; private set; } = "{}";

    public ProjectContextManager()
    {
        _stellaLensPath = Path.Combine(AppContext.BaseDirectory, "stella_lens");
        if (OperatingSystem.IsWindows()) _stellaLensPath += ".exe";
    }

    public async Task RebuildAstMapAsync(string projectRootPath)
    {
        if (!File.Exists(_stellaLensPath))
        {
            string alternativePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "stella_lens", "target", "debug", "stella_lens"));
            if (File.Exists(alternativePath))
            {
                File.Copy(alternativePath, _stellaLensPath, true);
            }
            else
            {
                CurrentProjectBlueprintJson = "{\"error\": \"stella_lens binary not found. Please run cargo build inside stella_lens directory.\"}";
                return;
            }
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _stellaLensPath,
            Arguments = $"\"{projectRootPath}\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process != null)
        {
            string output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            
            if (!string.IsNullOrWhiteSpace(output))
            {
                CurrentProjectBlueprintJson = output;
            }
        }
    }
}