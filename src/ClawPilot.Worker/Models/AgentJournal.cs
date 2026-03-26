using System.Text.Json;

namespace ClawPilot.Worker.Models;

public class JournalEntry
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Thought { get; set; } = string.Empty;
    public string? Action { get; set; }
    public string? Result { get; set; }
    public bool Success { get; set; } = true;
}

public class AgentJournal
{
    private readonly string _filePath = "AgentJournal.json";
    private readonly List<JournalEntry> _entries = new List<JournalEntry>();

    public AgentJournal()
    {
        if (File.Exists(_filePath))
        {
            try
            {
                string json = File.ReadAllText(_filePath);
                _entries = JsonSerializer.Deserialize<List<JournalEntry>>(json) ?? new List<JournalEntry>();
            }
            catch { }
        }
    }

    public void AddEntry(string thought, string? action = null, string? result = null, bool success = true)
    {
        JournalEntry entry = new JournalEntry
        {
            Thought = thought,
            Action = action,
            Result = result,
            Success = success
        };
        _entries.Add(entry);
        Save();
    }

    private void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch { }
    }
}
