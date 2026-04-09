using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using ClawPilot.Worker.Models;
using ClawPilot.Worker.Options;
using Microsoft.Extensions.Options;

namespace ClawPilot.Worker.Services;

/// <summary>
/// Lightweight HTTP API (REST + SSE) that exposes ClawPilot's capabilities to the local CLI tool.
/// Listens on 127.0.0.1:&lt;port&gt; (default 9001, never exposed publicly).
/// </summary>
public class CliApiService(
    IOptions<CliOptions> options,
    TelegramService telegramService,
    LogService logService,
    SchedulerService schedulerService,
    ILogger<CliApiService> logger) : BackgroundService
{
    private readonly CliOptions _options = options.Value;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("CLI API is disabled (Cli:Enabled = false).");
            return;
        }

        using HttpListener listener = new();
        listener.Prefixes.Add($"http://127.0.0.1:{_options.Port}/");

        try
        {
            listener.Start();
            logger.LogInformation("CLI API listening on http://127.0.0.1:{Port}/", _options.Port);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start CLI API listener on port {Port}. " +
                "Ensure no other process is using the port.", _options.Port);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error accepting CLI request");
                continue;
            }

            _ = Task.Run(() => HandleRequestAsync(context, stoppingToken), stoppingToken);
        }

        listener.Stop();
    }

    // ─── Request router ───────────────────────────────────────────────────────

    private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        string method = ctx.Request.HttpMethod.ToUpperInvariant();
        string path = ctx.Request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;

        ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");

        try
        {
            switch (method, path)
            {
                case ("GET", "/api/health"):
                    await WriteJsonAsync(ctx.Response, 200, """{"status":"ok"}""");
                    break;

                case ("GET", "/api/stream"):
                    await HandleSseAsync(ctx, ct);
                    break;

                case ("POST", "/api/task"):
                    await HandleTaskAsync(ctx, ct);
                    break;

                case ("POST", "/api/chat"):
                    await HandleChatAsync(ctx, ct);
                    break;

                case ("GET", "/api/cron"):
                    await HandleCronListAsync(ctx, ct);
                    break;

                case ("POST", "/api/cron"):
                    await HandleCronAddAsync(ctx, ct);
                    break;

                default:
                    if (method == "DELETE" && path.StartsWith("/api/cron/"))
                        await HandleCronDeleteAsync(ctx, path, ct);
                    else if (method == "PATCH" && path.StartsWith("/api/cron/"))
                        await HandleCronPatchAsync(ctx, path, ct);
                    else
                        await WriteJsonAsync(ctx.Response, 404, """{"error":"not found"}""");
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling CLI request {Method} {Path}", method, path);
            try { await WriteJsonAsync(ctx.Response, 500, $"{{\"error\":\"{JsonEscape(ex.Message)}\"}}"); }
            catch { /* ignore write errors after a fault */ }
        }
    }

    // ─── SSE stream ───────────────────────────────────────────────────────────

    private async Task HandleSseAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        string clientId = Guid.NewGuid().ToString("N");

        ctx.Response.ContentType = "text/event-stream; charset=utf-8";
        ctx.Response.Headers.Set("Cache-Control", "no-cache");
        ctx.Response.Headers.Set("Connection", "keep-alive");
        ctx.Response.SendChunked = true;

        ChannelReader<string> reader = logService.Subscribe(clientId);

        try
        {
            // Send a handshake comment so the client knows the stream is ready.
            await WriteRawSseAsync(ctx.Response, ": connected\n\n", ct);

            await foreach (string msg in reader.ReadAllAsync(ct))
            {
                string sseFrame = FormatSseFrame(msg);
                await WriteRawSseAsync(ctx.Response, sseFrame, ct);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "SSE client {ClientId} disconnected", clientId);
        }
        finally
        {
            logService.Unsubscribe(clientId);
            try { ctx.Response.Close(); } catch { }
        }
    }

    private static string FormatSseFrame(string msg)
    {
        if (msg == LogService.SessionEndMarker)
            return "event: session.end\ndata: done\n\n";

        if (msg == LogService.SessionErrorMarker)
            return "event: session.error\ndata: error\n\n";

        // Escape embedded newlines so each continuation is a separate data: line.
        string escaped = msg.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\ndata: ");
        return $"data: {escaped}\n\n";
    }

    private static async Task WriteRawSseAsync(HttpListenerResponse response, string text, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await response.OutputStream.WriteAsync(bytes, ct);
        await response.OutputStream.FlushAsync(ct);
    }

    // ─── Task / Chat ──────────────────────────────────────────────────────────

    private async Task HandleTaskAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        JsonElement body = await ReadJsonBodyAsync(ctx);
        if (!body.TryGetProperty("prompt", out JsonElement promptEl) ||
            string.IsNullOrWhiteSpace(promptEl.GetString()))
        {
            await WriteJsonAsync(ctx.Response, 400, """{"error":"prompt is required"}""");
            return;
        }

        string prompt = promptEl.GetString()!;
        await telegramService.WriteCommandAsync(new TelegramCommand("cli", $"/task {prompt}"), ct);
        await WriteJsonAsync(ctx.Response, 202, """{"status":"accepted"}""");
    }

    private async Task HandleChatAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        JsonElement body = await ReadJsonBodyAsync(ctx);
        if (!body.TryGetProperty("message", out JsonElement msgEl) ||
            string.IsNullOrWhiteSpace(msgEl.GetString()))
        {
            await WriteJsonAsync(ctx.Response, 400, """{"error":"message is required"}""");
            return;
        }

        string message = msgEl.GetString()!;
        await telegramService.WriteCommandAsync(new TelegramCommand("cli", message), ct);
        await WriteJsonAsync(ctx.Response, 202, """{"status":"accepted"}""");
    }

    // ─── Cron management ─────────────────────────────────────────────────────

    private async Task HandleCronListAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        List<ScheduledTask> tasks = await schedulerService.ListTasksAsync(ct);
        await WriteJsonAsync(ctx.Response, 200, JsonSerializer.Serialize(tasks, JsonOpts));
    }

    private async Task HandleCronAddAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        JsonElement body = await ReadJsonBodyAsync(ctx);

        if (!body.TryGetProperty("schedule", out JsonElement scheduleEl) ||
            !body.TryGetProperty("prompt", out JsonElement promptEl))
        {
            await WriteJsonAsync(ctx.Response, 400, """{"error":"schedule and prompt are required"}""");
            return;
        }

        string schedule = scheduleEl.GetString() ?? string.Empty;
        string prompt = promptEl.GetString() ?? string.Empty;

        RunMode mode = RunMode.Recurring;
        if (body.TryGetProperty("mode", out JsonElement modeEl))
        {
            string? modeStr = modeEl.GetString();
            mode = modeStr switch
            {
                "until-done" or "UntilDone" => RunMode.UntilDone,
                "once" or "RunOnce" => RunMode.RunOnce,
                _ => RunMode.Recurring,
            };
        }

        bool needsApproval = body.TryGetProperty("needsApproval", out JsonElement naEl) && naEl.GetBoolean();

        try
        {
            ScheduledTask task = await schedulerService.AddTaskAsync(prompt, schedule, mode, needsApproval, ct);
            await WriteJsonAsync(ctx.Response, 201, JsonSerializer.Serialize(task, JsonOpts));
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(ctx.Response, 400, $"{{\"error\":\"{JsonEscape(ex.Message)}\"}}");
        }
    }

    private async Task HandleCronDeleteAsync(HttpListenerContext ctx, string path, CancellationToken ct)
    {
        // path: /api/cron/{id}
        string idStr = path["/api/cron/".Length..];
        if (!Guid.TryParse(idStr, out Guid id))
        {
            await WriteJsonAsync(ctx.Response, 400, """{"error":"invalid id"}""");
            return;
        }

        bool removed = await schedulerService.RemoveTaskAsync(id, ct);
        await WriteJsonAsync(ctx.Response, removed ? 200 : 404,
            removed ? """{"status":"removed"}""" : """{"error":"not found"}""");
    }

    private async Task HandleCronPatchAsync(HttpListenerContext ctx, string path, CancellationToken ct)
    {
        // path: /api/cron/{id}/pause  or  /api/cron/{id}/resume
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 4 || !Guid.TryParse(segments[2], out Guid id))
        {
            await WriteJsonAsync(ctx.Response, 400, """{"error":"invalid path — expected /api/cron/{id}/pause|resume"}""");
            return;
        }

        string action = segments[3].ToLowerInvariant();
        if (action is not ("pause" or "resume"))
        {
            await WriteJsonAsync(ctx.Response, 400, """{"error":"action must be pause or resume"}""");
            return;
        }

        bool enabled = action == "resume";
        bool updated = await schedulerService.SetEnabledAsync(id, enabled, ct);
        await WriteJsonAsync(ctx.Response, updated ? 200 : 404,
            updated ? $"{{\"status\":\"{action}d\"}}" : """{"error":"not found"}""");
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<JsonElement> ReadJsonBodyAsync(HttpListenerContext ctx)
    {
        using StreamReader sr = new(ctx.Request.InputStream, Encoding.UTF8, leaveOpen: true);
        string body = await sr.ReadToEndAsync();
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body).RootElement;
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, int statusCode, string json)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private static string JsonEscape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
}
