namespace ClawPilot.Worker.Options;

public class EmailOptions
{
    // IMAP (read)
    public string ImapHost { get; set; } = string.Empty;
    public int ImapPort { get; set; } = 993;
    public bool ImapUseSsl { get; set; } = true;

    // SMTP (send)
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseSsl { get; set; } = false;

    // Shared credentials
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    // Sender identity
    public string FromName { get; set; } = "ClawPilot Agent";
    public string FromAddress { get; set; } = string.Empty;
}
