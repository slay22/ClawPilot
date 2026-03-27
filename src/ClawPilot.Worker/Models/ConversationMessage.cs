namespace ClawPilot.Worker.Models;

// Stores the mapping from Telegram chat ID → Copilot SDK session ID for cross-restart resume.
// The SDK persists conversation history itself; we only need the session ID reference.
public class ConversationMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TelegramChatId { get; set; } = string.Empty;
    public string CopilotSessionId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
