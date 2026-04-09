using System.CommandLine;
using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;

// ─── Global options ───────────────────────────────────────────────────────────

Option<int> portOption = new("--port", () => 9001, "Worker CLI API port (default 9001)");
portOption.AddAlias("-p");

RootCommand root = new("ClawPilot CLI — control your agent from the terminal");
root.AddGlobalOption(portOption);

// ─── task ─────────────────────────────────────────────────────────────────────

Command taskCmd = new("task", "Run a one-off agent task and stream its output");
Argument<string> taskPromptArg = new("prompt", "What you want the agent to do");
taskCmd.AddArgument(taskPromptArg);

taskCmd.SetHandler(async (string prompt, int port) =>
{
    using CancellationTokenSource cts = new();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    await RunStreamingCommandAsync(
        port,
        httpClient => PostJsonAsync(httpClient, "/api/task", new { prompt }, cts.Token),
        cts.Token);
}, taskPromptArg, portOption);

root.AddCommand(taskCmd);

// ─── chat ─────────────────────────────────────────────────────────────────────

Command chatCmd = new("chat", "Send a conversational message and stream the reply");
Argument<string> chatMsgArg = new("message", "Your message to the agent");
chatCmd.AddArgument(chatMsgArg);

chatCmd.SetHandler(async (string message, int port) =>
{
    using CancellationTokenSource cts = new();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    await RunStreamingCommandAsync(
        port,
        httpClient => PostJsonAsync(httpClient, "/api/chat", new { message }, cts.Token),
        cts.Token);
}, chatMsgArg, portOption);

root.AddCommand(chatCmd);

// ─── cron ─────────────────────────────────────────────────────────────────────

Command cronCmd = new("cron", "Manage scheduled tasks");

// cron list
Command cronListCmd = new("list", "List all scheduled tasks");
cronListCmd.SetHandler(async (int port) =>
{
    using HttpClient httpClient = CreateClient(port);
    HttpResponseMessage resp = await httpClient.GetAsync("/api/cron");
    string json = await resp.Content.ReadAsStringAsync();
    PrintCronList(json);
}, portOption);
cronCmd.AddCommand(cronListCmd);

// cron add
Command cronAddCmd = new("add", "Add a new scheduled task");
Option<string> scheduleOpt = new("--schedule", "Schedule (e.g. 'every 1h', '0 * * * *')") { IsRequired = true };
Option<string> modeOpt = new("--mode", () => "recurring", "Run mode: recurring | until-done | once");
Option<bool> needsApprovalOpt = new("--needs-approval", () => false, "Require operator approval before each run");
Argument<string> cronPromptArg = new("prompt", "Agent prompt to execute on each run");

cronAddCmd.AddOption(scheduleOpt);
cronAddCmd.AddOption(modeOpt);
cronAddCmd.AddOption(needsApprovalOpt);
cronAddCmd.AddArgument(cronPromptArg);

cronAddCmd.SetHandler(async (string schedule, string mode, bool needsApproval, string prompt, int port) =>
{
    using HttpClient httpClient = CreateClient(port);
    HttpResponseMessage resp = await PostJsonAsync(httpClient, "/api/cron",
        new { schedule, prompt, mode, needsApproval }, CancellationToken.None);
    string json = await resp.Content.ReadAsStringAsync();

    if ((int)resp.StatusCode == 201)
    {
        JsonElement task = JsonDocument.Parse(json).RootElement;
        Console.WriteLine($"✅ Task created: {task.GetProperty("id").GetString()}");
        Console.WriteLine($"   Label: {task.GetProperty("label").GetString()}");
        Console.WriteLine($"   Next run: {task.GetProperty("nextRunAt").GetString()}");
    }
    else
    {
        Console.Error.WriteLine($"❌ Failed: {json}");
        Environment.Exit(1);
    }
}, scheduleOpt, modeOpt, needsApprovalOpt, cronPromptArg, portOption);
cronCmd.AddCommand(cronAddCmd);

// cron remove
Command cronRemoveCmd = new("remove", "Remove a scheduled task by ID");
Argument<string> removeIdArg = new("id", "Task ID (GUID)");
cronRemoveCmd.AddArgument(removeIdArg);

cronRemoveCmd.SetHandler(async (string id, int port) =>
{
    using HttpClient httpClient = CreateClient(port);
    HttpResponseMessage resp = await httpClient.DeleteAsync($"/api/cron/{id}");
    string json = await resp.Content.ReadAsStringAsync();
    Console.WriteLine(resp.IsSuccessStatusCode ? $"✅ Removed {id}" : $"❌ {json}");
    if (!resp.IsSuccessStatusCode) Environment.Exit(1);
}, removeIdArg, portOption);
cronCmd.AddCommand(cronRemoveCmd);

// cron pause
Command cronPauseCmd = new("pause", "Pause a scheduled task");
Argument<string> pauseIdArg = new("id", "Task ID (GUID)");
cronPauseCmd.AddArgument(pauseIdArg);

cronPauseCmd.SetHandler(async (string id, int port) =>
{
    using HttpClient httpClient = CreateClient(port);
    HttpResponseMessage resp = await httpClient.PatchAsync($"/api/cron/{id}/pause", null);
    string json = await resp.Content.ReadAsStringAsync();
    Console.WriteLine(resp.IsSuccessStatusCode ? $"⏸ Paused {id}" : $"❌ {json}");
    if (!resp.IsSuccessStatusCode) Environment.Exit(1);
}, pauseIdArg, portOption);
cronCmd.AddCommand(cronPauseCmd);

// cron resume
Command cronResumeCmd = new("resume", "Resume a paused scheduled task");
Argument<string> resumeIdArg = new("id", "Task ID (GUID)");
cronResumeCmd.AddArgument(resumeIdArg);

cronResumeCmd.SetHandler(async (string id, int port) =>
{
    using HttpClient httpClient = CreateClient(port);
    HttpResponseMessage resp = await httpClient.PatchAsync($"/api/cron/{id}/resume", null);
    string json = await resp.Content.ReadAsStringAsync();
    Console.WriteLine(resp.IsSuccessStatusCode ? $"▶ Resumed {id}" : $"❌ {json}");
    if (!resp.IsSuccessStatusCode) Environment.Exit(1);
}, resumeIdArg, portOption);
cronCmd.AddCommand(cronResumeCmd);

root.AddCommand(cronCmd);

// ─── Run ──────────────────────────────────────────────────────────────────────

return await root.InvokeAsync(args);

// ─── Helpers ──────────────────────────────────────────────────────────────────

static HttpClient CreateClient(int port)
{
    HttpClient client = new();
    client.BaseAddress = new Uri($"http://127.0.0.1:{port}");
    client.Timeout = TimeSpan.FromMinutes(30);
    return client;
}

static async Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string path, object body, CancellationToken ct)
{
    string json = JsonSerializer.Serialize(body, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    StringContent content = new(json, Encoding.UTF8, "application/json");
    return await client.PostAsync(path, content, ct);
}

/// <summary>
/// Opens an SSE stream, then posts the command, and streams log lines to stdout
/// until a session.end / session.error event is received or Ctrl+C is pressed.
/// </summary>
static async Task RunStreamingCommandAsync(
    int port,
    Func<HttpClient, Task<HttpResponseMessage>> sendCommand,
    CancellationToken ct)
{
    using HttpClient httpClient = CreateClient(port);

    // Health check — give a friendly error if the Worker isn't running.
    try
    {
        HttpResponseMessage health = await httpClient.GetAsync("/api/health", ct);
        if (!health.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"❌ Worker is not healthy (HTTP {(int)health.StatusCode}). Is ClawPilot running?");
            Environment.Exit(1);
        }
    }
    catch (HttpRequestException)
    {
        Console.Error.WriteLine($"❌ Cannot reach Worker at http://127.0.0.1:{port}/. Is ClawPilot running?");
        Environment.Exit(1);
    }

    using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    bool sessionEnded = false;
    bool sessionSuccess = false;

    // Open SSE stream first so we don't miss the first events.
    Task sseTask = Task.Run(async () =>
    {
        try
        {
            using HttpClient sseClient = CreateClient(port);
            sseClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            using HttpResponseMessage sseResp = await sseClient.GetAsync(
                "/api/stream", HttpCompletionOption.ResponseHeadersRead, cts.Token);

            using Stream stream = await sseResp.Content.ReadAsStreamAsync(cts.Token);

            await foreach (SseItem<string> item in SseParser.Create(stream).EnumerateAsync(cts.Token))
            {
                switch (item.EventType)
                {
                    case "session.end":
                        sessionEnded = true;
                        sessionSuccess = true;
                        await cts.CancelAsync();
                        return;

                    case "session.error":
                        sessionEnded = true;
                        sessionSuccess = false;
                        await cts.CancelAsync();
                        return;

                    default:
                        Console.WriteLine(item.Data);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { /* expected on session end or Ctrl+C */ }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Stream error: {ex.Message}");
        }
    }, cts.Token);

    // Small delay to let the SSE connection establish before we post the command.
    await Task.Delay(150, ct);

    HttpResponseMessage cmdResp = await sendCommand(httpClient);
    if (!cmdResp.IsSuccessStatusCode)
    {
        string err = await cmdResp.Content.ReadAsStringAsync(ct);
        Console.Error.WriteLine($"❌ Failed to send command: {err}");
        await cts.CancelAsync();
        Environment.Exit(1);
    }

    try { await sseTask; } catch (OperationCanceledException) { }

    if (sessionEnded && !sessionSuccess)
    {
        Console.Error.WriteLine("❌ Agent session ended with an error.");
        Environment.Exit(1);
    }
}

static void PrintCronList(string json)
{
    JsonElement tasks = JsonDocument.Parse(json).RootElement;
    if (tasks.GetArrayLength() == 0)
    {
        Console.WriteLine("No scheduled tasks.");
        return;
    }

    foreach (JsonElement task in tasks.EnumerateArray())
    {
        string id = task.GetProperty("id").GetString() ?? "?";
        string label = task.GetProperty("label").GetString() ?? "?";
        string mode = task.GetProperty("runMode").GetString() ?? "?";
        string nextRun = task.GetProperty("nextRunAt").GetString() ?? "?";
        bool enabled = task.GetProperty("isEnabled").GetBoolean();
        string status = enabled ? "✅" : "⏸";
        Console.WriteLine($"{status} [{id[..8]}…] {label}");
        Console.WriteLine($"     mode={mode}  next={nextRun}");
    }
}
