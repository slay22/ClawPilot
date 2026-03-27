using System.Diagnostics;
using Microsoft.CodeAnalysis.Sarif;

namespace ClawPilot.Worker.Tools;

public record BuildError(string FilePath, int Line, int Column, string Message, string RuleId);
public record BuildWarning(string FilePath, int Line, int Column, string Message, string RuleId);
public record BuildResult(bool Success, BuildError[] Errors, BuildWarning[] Warnings)
{
    public override string ToString()
    {
        if (Success) return "Build successful with no issues.";
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        foreach (BuildError e in Errors) sb.AppendLine($"[error] {e.RuleId}: {e.Message} at {e.FilePath}:{e.Line}:{e.Column}");
        foreach (BuildWarning w in Warnings) sb.AppendLine($"[warning] {w.RuleId}: {w.Message} at {w.FilePath}:{w.Line}:{w.Column}");
        return sb.ToString();
    }
}

public class BuildTool(ILogger<BuildTool> logger)
{
    [CopilotTool("dotnet_build", "Run dotnet build and return structured errors and warnings.")]
    public async Task<BuildResult> BuildAsync()
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
        if (process is null) return new BuildResult(false, [new BuildError("", 0, 0, "Failed to start dotnet build.", "")], []);

        await process.WaitForExitAsync();

        if (!File.Exists(sarifFile))
            return new BuildResult(true, [], []);

        SarifLog sarifLog = SarifLog.Load(sarifFile);
        File.Delete(sarifFile);

        List<BuildError> errors = new List<BuildError>();
        List<BuildWarning> warnings = new List<BuildWarning>();

        foreach (Run run in sarifLog.Runs)
        {
            if (run.Results is null) continue;
            foreach (Result result in run.Results)
            {
                string filePath = string.Empty;
                int line = 0;
                int col = 0;

                if (result.Locations is { Count: > 0 })
                {
                    PhysicalLocation? phys = result.Locations[0].PhysicalLocation;
                    if (phys?.ArtifactLocation?.Uri != null)
                        filePath = phys.ArtifactLocation.Uri.ToString();
                    line = phys?.Region?.StartLine ?? 0;
                    col = phys?.Region?.StartColumn ?? 0;
                }

                string msg = result.Message?.Text ?? string.Empty;
                string ruleId = result.RuleId ?? string.Empty;

                if (result.Level == FailureLevel.Error)
                    errors.Add(new BuildError(filePath, line, col, msg, ruleId));
                else if (result.Level == FailureLevel.Warning)
                    warnings.Add(new BuildWarning(filePath, line, col, msg, ruleId));
            }
        }

        return new BuildResult(errors.Count == 0, [..errors], [..warnings]);
    }
}
