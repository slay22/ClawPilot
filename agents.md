# ClawPilot — Architecture Reference for Agents

> This document describes the full architecture of ClawPilot so that AI agents (and developers) working on this codebase can understand the system without reading every file.

---

## Overview

**ClawPilot** is a .NET 10 autonomous agent system that accepts natural-language tasks via Telegram, runs them through the GitHub Copilot SDK (GPT-4o) in an agentic loop with tools, streams real-time output to a Blazor dashboard via SignalR, and asks a human operator for permission before sensitive actions.

```
Telegram ──► Worker (Agentic Loop) ──► GitHub Copilot SDK (GPT-4o)
                │         │                      │
                │    Tools (DbTool, BuildTool)◄──┘
                │
                └──► Dashboard (Blazor + SignalR) ──► Browser
```

---

## Project Structure

```
ClawPilot/
├── docker-compose.yml              # Orchestrates postgres, dashboard, worker
└── src/
    ├── ClawPilot.Dashboard/        # Blazor Server app — real-time log viewer
    └── ClawPilot.Worker/           # .NET Worker Service — agentic loop engine
```

---

## ClawPilot.Worker

**Type:** .NET 10 Worker Service (`Microsoft.NET.Sdk.Worker`)  
**Entry point:** `Program.cs` → registers DI, starts `Worker` hosted service

### Execution Flow

1. `Worker.cs` (BackgroundService) starts on host boot
2. Starts `LogService` (connects to Dashboard via SignalR)
3. Starts `TelegramService` (begins polling Telegram for messages)
4. Sends "🤖 Claw-Pilot Agent Online" to Telegram
5. Enters main loop: reads commands from `TelegramService.CommandReader` channel
6. For each command → calls `CopilotService.RunAgentAsync(prompt)`
7. Reports "✅ Task Complete" or "❌ Error" back to Telegram

### Services

#### `TelegramService`
- **Config:** `Telegram:BotToken`, `Telegram:ChatId` (env vars)
- **Receives:** Telegram messages and callback queries
- **Channel:** Incoming text messages are written to an unbounded `Channel<string>` (`CommandReader`). Only messages from `ChatId` are accepted.
- **Permission flow:** `RequestPermissionAsync(description)` sends an inline keyboard (Approve / Deny) to Telegram and awaits a `TaskCompletionSource<bool>`. The callback query handler resolves it.
- **Sends:** Markdown-formatted messages via `SendMessageAsync`

#### `CopilotService`
- **Config:** `GitHub:CopilotToken` (env var)
- **SDK:** `GitHub.Copilot.SDK` (v0.1.25-alpha)
- **Model:** `gpt-4o` with streaming enabled
- **Tool registration:** On startup, iterates all registered `toolProviders`, reflects over methods with `[CopilotTool]` attribute, and registers them as `AIFunction` via `AIFunctionFactory.Create`
- **Per-agent-run:**
  - Creates a `CopilotClient` session with tools and an `OnPermissionRequest` callback
  - Streams `AssistantMessageDeltaEvent` → forwards to `LogService` (SignalR)
  - Streams `ToolCallDeltaEvent` → logs tool name to SignalR and `AgentJournal`
  - Calls `session.SendAndWaitAsync(prompt)` and awaits completion

#### `LogService`
- Connects to `http://localhost:5247/logHub` (Dashboard's SignalR hub) as a **client**
- Uses `WithAutomaticReconnect()`
- `SendLogAsync(message)` invokes `SendLog` on the hub if connected

### Tools (Exposed to Copilot Agent)

Tools are discovered by reflection using `[CopilotTool(name, description)]`.

#### `DbTool` — `src/ClawPilot.Worker/Tools/DbTool.cs`
| Tool name | Description |
|---|---|
| `get_schema` | Lists all public tables and their columns from PostgreSQL |
| `execute_query` | Runs a `SELECT`-only SQL query; returns JSON results |

- Uses **Dapper** + **Npgsql** against the PostgreSQL instance
- Safety guard: rejects any SQL that doesn't start with `SELECT`
- Connection string from `ConnectionStrings:Database` config key

#### `BuildTool` — `src/ClawPilot.Worker/Tools/BuildTool.cs`
| Tool name | Description |
|---|---|
| `dotnet_build` | Runs `dotnet build /errorlog:<temp>.sarif`, parses SARIF, returns errors/warnings |

- Uses `Microsoft.CodeAnalysis.Sarif` (Sarif.Sdk v4.6.0) to parse build output
- Returns structured `[level] ruleId: message` lines with file locations
- Cleans up temp SARIF file after parsing

### `CopilotToolAttribute`
```csharp
[AttributeUsage(AttributeTargets.Method)]
public class CopilotToolAttribute(string name, string description) : Attribute
```
Decorate any `public` method on a registered tool provider to expose it to the agent.

### `AgentJournal` — `src/ClawPilot.Worker/Models/AgentJournal.cs`
- Persists a list of `JournalEntry` records to `AgentJournal.json` (local file, next to executable)
- Each entry has: `Timestamp`, `Thought`, `Action`, `Result`, `Success`
- Written synchronously on every `AddEntry` call

#### `WebSearchTool` — `src/ClawPilot.Worker/Tools/WebSearchTool.cs`
| Tool name | Description |
|---|---|
| `web_search` | Searches the web via Brave Search API; returns top titles, URLs, and descriptions |

- Uses `HttpClient` with Brave Search REST API (`https://api.search.brave.com/res/v1/web/search`)
- Configurable via `WebSearch:BraveApiKey` and `WebSearch:MaxResults` (default 5)
- Gracefully returns an error message if no API key is configured
- Get a free key at https://api.search.brave.com/ (2,000 queries/month free tier)


| Package | Version | Purpose |
|---|---|---|
| `GitHub.Copilot.SDK` | 0.1.25-alpha | Copilot agentic session |
| `Telegram.Bot` | 22.9.5.3 | Telegram bot client |
| `Microsoft.AspNetCore.SignalR.Client` | 10.0.5 | SignalR client → Dashboard |
| `Npgsql` | 10.0.2 | PostgreSQL driver |
| `Dapper` | 2.1.72 | SQL micro-ORM |
| `Sarif.Sdk` | 4.6.0 | Parse `dotnet build` SARIF output |
| `Microsoft.Extensions.Hosting` | 10.0.0 | Worker host |

---

## ClawPilot.Dashboard

**Type:** .NET 10 Blazor Server App (`Microsoft.NET.Sdk.Web`)  
**Entry point:** `Program.cs`

### Responsibilities
- Hosts a **SignalR hub** at `/logHub` that the Worker connects to
- Broadcasts incoming log messages to all connected browser clients in real time
- Provides a web UI (Blazor Server, Interactive Server render mode)

### SignalR Hub — `Hubs/LogHub.cs`
```
Client (Worker) calls: SendLog(message)
Hub broadcasts:        ReceiveLog(message) → all connected clients
```

### Blazor Components
```
Components/
├── App.razor               # Root component
├── Routes.razor            # Router
├── _Imports.razor          # Global using directives
├── Layout/
│   ├── MainLayout.razor    # App shell with nav
│   ├── NavMenu.razor       # Sidebar navigation
│   └── ReconnectModal.razor# Auto-reconnect UI
└── Pages/
    ├── Home.razor          # Main page (log viewer)
    ├── Counter.razor       # Default Blazor scaffold page
    ├── Weather.razor       # Default Blazor scaffold page
    ├── Error.razor         # Error boundary
    └── NotFound.razor      # 404 page
```

### NuGet Dependencies (Dashboard)
No explicit NuGet references — uses the built-in SDK defaults for Blazor Server + SignalR.

---

## Infrastructure

### Docker Compose Services

| Service | Image / Build | Port | Notes |
|---|---|---|---|
| `postgres` | `postgres:17` | `5432` | Health-checked with `pg_isready` |
| `dashboard` | `Dockerfile` in Dashboard project | `8080` | `ASPNETCORE_ENVIRONMENT=Development` |
| `worker` | `Dockerfile` in Worker project | — | Depends on `postgres` (healthy) + `dashboard` (started) |

### Environment Variables (Worker)
| Variable | Config key | Purpose |
|---|---|---|
| `TELEGRAM_BOT_TOKEN` | `Telegram:BotToken` | Bot authentication |
| `TELEGRAM_CHAT_ID` | `Telegram:ChatId` | Authorized chat (commands + notifications) |
| `GITHUB_COPILOT_TOKEN` | `GitHub:CopilotToken` | Copilot API authentication |
| `WebSearch__BraveApiKey` | `WebSearch:BraveApiKey` | Brave Search API key for web search |

---

## Inter-Service Communication

```
┌────────────┐   Telegram Bot API (polling)   ┌─────────────────┐
│  Telegram  │ ◄──────────────────────────── │                 │
│  (user)    │ ──── text commands ──────────► │  Worker         │
└────────────┘   inline button callbacks      │  (agentic loop) │
                                              │                 │
                                              │  ┌───────────┐  │
                                              │  │ Copilot   │  │
                                              │  │ SDK       │  │
                                              │  │ (GPT-4o)  │  │
                                              │  └───────────┘  │
                                              │        │tools   │
                                              │  ┌─────┴──────┐ │
                                              │  │ DbTool     │ │──► PostgreSQL
                                              │  │ BuildTool  │ │
                                              │  └────────────┘ │
                                              │        │        │
                                              │  SignalR client │
                                              └────────┼────────┘
                                                       │ SendLog(msg)
                                              ┌────────▼────────┐
                                              │  Dashboard      │
                                              │  /logHub        │
                                              │  (Blazor Server)│
                                              └────────┬────────┘
                                                       │ ReceiveLog
                                              ┌────────▼────────┐
                                              │  Browser (UI)   │
                                              └─────────────────┘
```

---

## Adding New Tools

1. Create a class (or add methods to an existing tool class)
2. Decorate methods with `[CopilotTool("tool_name", "description")]`
3. Register the instance in `Program.cs` under `tools` list passed to `CopilotService`

The `CopilotService` constructor will auto-discover and register all `[CopilotTool]`-decorated methods via reflection.

---

## Key Design Patterns

- **Channel-based command queue:** Telegram messages → `Channel<string>` → consumed serially by the Worker loop. Prevents concurrent agent runs.
- **Permission gate:** Any Copilot SDK permission request is forwarded to Telegram as an inline keyboard. The agent is blocked until the operator approves or denies.
- **SignalR streaming:** Agent response deltas are pushed to the Dashboard in real time without polling.
- **Reflection-based tool registration:** No manual wiring needed for new tools — just add `[CopilotTool]` and register the provider object.
- **SARIF-based build feedback:** Build errors are structured (not raw stdout), giving the agent precise file/line information.
