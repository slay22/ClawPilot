using System.Text;
using ClawPilot.Worker.Options;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ClawPilot.Worker.Tools;

public class EmailTool(IOptions<EmailOptions> options, ILogger<EmailTool> logger)
{
    private readonly EmailOptions _options = options.Value;

    [CopilotTool("read_emails",
        "Reads the most recent emails from the inbox. Optionally filter by sender address or subject keyword. " +
        "Returns a JSON array with fields: id, from, subject, date, bodyPreview.")]
    public async Task<string> ReadEmailsAsync(
        int count = 10,
        string? fromFilter = null,
        string? subjectFilter = null)
    {
        if (string.IsNullOrWhiteSpace(_options.ImapHost))
            return "Email is not configured. Set the Email:ImapHost option.";

        try
        {
            using ImapClient client = new();
            await client.ConnectAsync(_options.ImapHost, _options.ImapPort, _options.ImapUseSsl);
            await client.AuthenticateAsync(_options.Username, _options.Password);

            IMailFolder inbox = client.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly);

            int total = inbox.Count;
            int fetchFrom = Math.Max(0, total - count * 5); // over-fetch to allow filtering

            IList<IMessageSummary> summaries = await inbox.FetchAsync(
                fetchFrom, total - 1,
                MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.BodyStructure);

            List<IMessageSummary> filtered = [..summaries.Reverse()];

            if (!string.IsNullOrWhiteSpace(fromFilter))
                filtered = [..filtered.Where(s =>
                    s.Envelope.From.Mailboxes.Any(m =>
                        m.Address.Contains(fromFilter, StringComparison.OrdinalIgnoreCase) ||
                        (m.Name?.Contains(fromFilter, StringComparison.OrdinalIgnoreCase) ?? false)))];

            if (!string.IsNullOrWhiteSpace(subjectFilter))
                filtered = [..filtered.Where(s =>
                    s.Envelope.Subject?.Contains(subjectFilter, StringComparison.OrdinalIgnoreCase) ?? false)];

            filtered = [..filtered.Take(count)];

            StringBuilder sb = new();
            sb.AppendLine("[");
            for (int i = 0; i < filtered.Count; i++)
            {
                IMessageSummary s = filtered[i];
                string from = s.Envelope.From.ToString();
                string subject = s.Envelope.Subject ?? "(no subject)";
                string date = s.Envelope.Date?.ToString("yyyy-MM-dd HH:mm") ?? "";
                string uid = s.UniqueId.ToString();

                // Fetch text body preview
                string preview = await FetchBodyPreviewAsync(inbox, s);

                sb.Append($"  {{\"id\":\"{uid}\",\"from\":{EscapeJson(from)},\"subject\":{EscapeJson(subject)},\"date\":{EscapeJson(date)},\"bodyPreview\":{EscapeJson(preview)}}}");
                if (i < filtered.Count - 1) sb.AppendLine(",");
                else sb.AppendLine();
            }
            sb.AppendLine("]");

            await client.DisconnectAsync(true);
            return sb.ToString();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "EmailTool.ReadEmailsAsync failed");
            return $"Error reading emails: {ex.Message}";
        }
    }

    [CopilotTool("search_emails",
        "Searches the inbox for emails matching the given keyword (searches subject and body). " +
        "Returns a JSON array with fields: id, from, subject, date, bodyPreview.")]
    public async Task<string> SearchEmailsAsync(string keyword, int maxResults = 10)
    {
        if (string.IsNullOrWhiteSpace(_options.ImapHost))
            return "Email is not configured. Set the Email:ImapHost option.";

        if (string.IsNullOrWhiteSpace(keyword))
            return "Keyword must not be empty.";

        try
        {
            using ImapClient client = new();
            await client.ConnectAsync(_options.ImapHost, _options.ImapPort, _options.ImapUseSsl);
            await client.AuthenticateAsync(_options.Username, _options.Password);

            IMailFolder inbox = client.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly);

            SearchQuery query = SearchQuery.SubjectContains(keyword).Or(SearchQuery.BodyContains(keyword));
            IList<UniqueId> uids = await inbox.SearchAsync(query);
            IList<UniqueId> page = [..uids.Reverse().Take(maxResults)];

            IList<IMessageSummary> summaries = await inbox.FetchAsync(
                [..page],
                MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.BodyStructure);

            StringBuilder sb = new();
            sb.AppendLine("[");
            for (int i = 0; i < summaries.Count; i++)
            {
                IMessageSummary s = summaries[i];
                string from = s.Envelope.From.ToString();
                string subject = s.Envelope.Subject ?? "(no subject)";
                string date = s.Envelope.Date?.ToString("yyyy-MM-dd HH:mm") ?? "";
                string uid = s.UniqueId.ToString();
                string preview = await FetchBodyPreviewAsync(inbox, s);

                sb.Append($"  {{\"id\":\"{uid}\",\"from\":{EscapeJson(from)},\"subject\":{EscapeJson(subject)},\"date\":{EscapeJson(date)},\"bodyPreview\":{EscapeJson(preview)}}}");
                if (i < summaries.Count - 1) sb.AppendLine(",");
                else sb.AppendLine();
            }
            sb.AppendLine("]");

            await client.DisconnectAsync(true);
            return sb.ToString();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "EmailTool.SearchEmailsAsync failed");
            return $"Error searching emails: {ex.Message}";
        }
    }

    [CopilotTool("send_email",
        "Sends an email via SMTP. Provide recipient address, subject, and plain-text body. " +
        "Optionally provide replyToMessageId to thread the reply correctly.")]
    public async Task<string> SendEmailAsync(
        string to,
        string subject,
        string body,
        string? replyToMessageId = null)
    {
        if (string.IsNullOrWhiteSpace(_options.SmtpHost))
            return "Email is not configured. Set the Email:SmtpHost option.";

        if (string.IsNullOrWhiteSpace(to))
            return "Recipient address (to) must not be empty.";

        try
        {
            MimeMessage message = new();
            message.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));
            message.To.Add(MailboxAddress.Parse(to));
            message.Subject = subject;

            if (!string.IsNullOrWhiteSpace(replyToMessageId))
                message.InReplyTo = replyToMessageId;

            message.Body = new TextPart("plain") { Text = body };

            using SmtpClient smtp = new SmtpClient();
            await smtp.ConnectAsync(_options.SmtpHost, _options.SmtpPort,
                _options.SmtpUseSsl
                    ? MailKit.Security.SecureSocketOptions.SslOnConnect
                    : MailKit.Security.SecureSocketOptions.StartTlsWhenAvailable);

            await smtp.AuthenticateAsync(_options.Username, _options.Password);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);

            logger.LogInformation("Email sent to {To} with subject '{Subject}'", to, subject);
            return $"Email sent successfully to {to}.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "EmailTool.SendEmailAsync failed");
            return $"Error sending email: {ex.Message}";
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<string> FetchBodyPreviewAsync(IMailFolder inbox, IMessageSummary summary)
    {
        try
        {
            MimeEntity? body = await inbox.GetBodyPartAsync(summary.UniqueId, summary.TextBody);
            if (body is TextPart textPart)
            {
                string text = textPart.Text ?? string.Empty;
                return text.Length > 300 ? text[..300] + "…" : text;
            }
        }
        catch
        {
            // best-effort
        }
        return string.Empty;
    }

    private static string EscapeJson(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);
}
