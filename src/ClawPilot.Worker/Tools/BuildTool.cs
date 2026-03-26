using System.Diagnostics;
using System.Text;
using Microsoft.CodeAnalysis.Sarif;

namespace ClawPilot.Worker.Tools;

public class BuildTool(ILogger<BuildTool> logger)
{
    [CopilotTool("dotnet_build", "Run dotnet build and return the errors and warnings in SARIF format.")]
    public async Task<string> BuildAsync()
    {
        logger.LogInformation("Running dotnet build...");

        string sarifFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.sarif");

        ProcessStartInfo startInfo = new ProcessStartInfo()
        {
            FileName = "dotnet",
            Arguments = $"build /errorlog:{sarifFile}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using Process? process = Process.Start(startInfo);
        if (process is null) return "Error: Failed to start dotnet build.";

        await process.WaitForExitAsync();

        if (!File.Exists(sarifFile))
        {
            return "Build finished, but no SARIF log was generated.";
        }

        SarifLog sarifLog = SarifLog.Load(sarifFile);

        StringBuilder report = new StringBuilder();
        foreach (Run run in sarifLog.Runs)
        {
            if (run.Results is null) continue;
            foreach (Result result in run.Results)
            {
                report.AppendLine($"[{result.Level}] {result.RuleId}: {result.Message.Text}");

                if (result.Locations is null) continue;
                foreach (Location loc in result.Locations)
                {
                    PhysicalLocation phys = loc.PhysicalLocation;
                    if (phys?.ArtifactLocation?.Uri != null)
                    {
                        report.AppendLine($"  At: {phys.ArtifactLocation.Uri} (Line {phys.Region?.StartLine})");
                    }
                }
            }
        }

        File.Delete(sarifFile);

        return report.Length > 0 ? report.ToString() : "Build successful with no issues.";
    }
}
