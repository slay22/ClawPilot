# 🦀 ClawPilot

An autonomous AI agent + conversational assistant that lives in Telegram. Send a casual message to chat with it; prefix with `/task` to trigger the full agentic loop — **GitHub Copilot SDK (GPT-4o)** reasoning, tool execution, and human approval gates before any sensitive action. Real-time output streams to a **Blazor dashboard** via SignalR.

```
Telegram ──► Worker ──┬─── Conversation Mode ──► CopilotSession (persistent, per chat)
                      │         │                      │
                      │         └── Web Search ◄───────┘
                      │
                      └─── Task Mode (/task) ──► Agentic Loop (GPT-4o)
                                │                     │
                                │    Local Tools ◄────┘
                                │    GitHub MCP  (api.githubcopilot.com)
                                │    Tavily MCP  (mcp.tavily.com)
                                │
                                └──► Dashboard (Blazor + SignalR) ──► Browser
```

---

## Features

- 💬 **Two modes, one bot** — Casual conversation with persistent history, or `/task` for the full agentic loop
- 🧠 **Personality** — `Soul.md` at the repo root defines character, voice, and values; loaded at startup
- 🔀 **Mid-conversation escalation** — The assistant calls `escalate_to_task` itself when it determines a request needs builds, file edits, or GitHub operations
- 🤖 **Agentic loop** — GPT-4o reasons, calls tools, self-corrects until the task is done
- 🔒 **Tiered permission gate** — AutoApprove / RequireConfirmation (Telegram inline button, 60s timeout) / AlwaysBlock
- 📊 **Structured journal** — Every thought, tool call, and correction persisted to SQLite with full EF Core schema
- 💰 **Budget enforcement** — Configurable max iterations, max tool calls, and wall-clock timeout per session
- 📡 **Real-time dashboard** — Blazor Server + SignalR streams agent reasoning to the browser as it happens
- 🔧 **Reflection-based tools** — Add `[CopilotTool]` to any method and it's automatically registered

---

## Architecture

```
ClawPilot/
├── Soul.md                      # Personality definition — edit freely, reloaded on restart
├── docker-compose.yml
└── src/
    ├── ClawPilot.Dashboard/     # Blazor Server — real-time log viewer
    └── ClawPilot.Worker/        # .NET Worker Service — agentic loop + conversation engine
        ├── Services/
        │   ├── CopilotService       # Task mode: agentic loop, budget, journal orchestration
        │   ├── ConversationService  # Chat mode: persistent sessions, escalation tool, Soul.md
        │   ├── TelegramService      # Bot polling, tiered permission gate, command routing
        │   ├── GitHubMcpService     # Remote MCP → api.githubcopilot.com/mcp/
        │   ├── WebSearchMcpService  # Remote MCP → mcp.tavily.com/mcp/
        │   ├── AgentJournalService  # EF Core SQLite journal
        │   └── LogService           # SignalR client → Dashboard
        ├── Tools/
        │   ├── BuildTool            # dotnet build → typed BuildResult (SARIF)
        │   └── DbTool               # PostgreSQL schema introspection + read-only queries
        ├── Models/                  # AgentSession, ToolIntent, ToolOutcome, ConversationMessage, etc.
        ├── Data/                    # ClawPilotDbContext
        └── Options/                 # AgentBudgetOptions, JournalOptions, ConversationOptions, etc.
```

---

## Conversation vs Task Mode

| | Conversation mode | Task mode |
|---|---|---|
| **Trigger** | Any message | `/task <prompt>` |
| **Engine** | Long-lived `CopilotSession` per chat | New session per run |
| **History** | Persistent (SDK-managed, survives restarts) | Stateless |
| **Tools available** | Web search + `escalate_to_task` | All local tools + GitHub MCP + Tavily MCP |
| **Budget enforcement** | None | MaxIterations / MaxToolCalls / Timeout |
| **Journal** | Not journaled (lightweight) | Full structured journal |
| **Permission gate** | Web search → Confirm | Full tiered policy |
| **Reset** | `/clear` | N/A |

### Escalation flow

When the assistant determines mid-conversation that a request needs real work (build, file edit, DB query, GitHub operation), it calls the `escalate_to_task(taskDescription)` tool automatically:

```
User: "Fix the failing build in my repo"
  → Assistant detects this needs task mode
  → Calls escalate_to_task("Fix failing build in <repo>")
  → Telegram: "🔀 Escalating to task mode..."
  → Full agentic loop fires with approval gates
```

---

## Tools

| Tool | Source | Mode | Permission | Description |
|---|---|---|---|---|
| `escalate_to_task` | Local | Conversation | AutoApprove | Hands off to full task mode |
| `dotnet_build` | Local | Task | AutoApprove | Runs `dotnet build`, returns typed errors/warnings via SARIF |
| `get_schema` | Local | Task | AutoApprove | Lists tables + columns from PostgreSQL (supports table filter) |
| `execute_query` | Local | Task | AutoApprove | Runs SELECT-only SQL (max 200 rows, 5s timeout) |
| `get_file_contents` | GitHub MCP | Task | AutoApprove | Reads a file from any GitHub repo |
| `list_pull_requests` | GitHub MCP | Task | AutoApprove | Lists PRs on a repo |
| `get_workflow_run` | GitHub MCP | Task | AutoApprove | Gets a GitHub Actions workflow run |
| `get_workflow_run_logs` | GitHub MCP | Task | AutoApprove | Fetches GitHub Actions logs |
| `create_issue` | GitHub MCP | Task | **Confirm** | Creates a GitHub issue |
| `push_files` | GitHub MCP | Task | **Confirm** | Pushes file changes to a repo |
| `create_pull_request` | GitHub MCP | Task | **Confirm** | Opens a pull request |
| `tavily-search` | Tavily MCP | Both | **Confirm** | Real-time web search |
| `tavily-extract` | Tavily MCP | Both | **Confirm** | Extracts content from a URL |

---

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker + Docker Compose](https://docs.docker.com/get-docker/)
- A Telegram bot token ([@BotFather](https://t.me/botfather))

### 1. Clone & configure secrets

```bash
git clone https://github.com/slay22/ClawPilot.git
cd ClawPilot
cp .env.example .env
```

Edit `.env` and fill in your values:

```env
TELEGRAM_BOT_TOKEN=        # from @BotFather
TELEGRAM_CHAT_ID=          # your numeric Telegram user ID (message @userinfobot to get it)
GITHUB_COPILOT_TOKEN=      # GitHub → Settings → Copilot
GITHUB_PAT=                # Fine-grained PAT (repo read, actions read, issues write)
TAVILY_API_KEY=            # Free at app.tavily.com (1,000 req/month)
```

> ⚠️ `TELEGRAM_CHAT_ID` must be your **numeric** user ID, not a username. Message [@userinfobot](https://t.me/userinfobot) on Telegram — it replies with your ID instantly.

### 2. (Optional) Customize the personality

Edit `Soul.md` at the repo root before building. The assistant loads it on every startup.

### 3. Run with Docker Compose

```bash
docker compose up --build
```

This starts:
- **postgres** — PostgreSQL 17 for the agent's DB tool
- **dashboard** — Blazor Server on `http://localhost:8080`
- **worker** — The agentic loop + conversation engine (waits for Telegram messages)

### 4. Talk to it

Open Telegram, message your bot:

```
What's new in .NET 10?
```
→ Casual conversation, may use web search.

```
/task Fix the compile error in src/ClawPilot.Worker
```
→ Full agentic loop — builds, reads errors, edits files, asks for confirmation before writing.

```
/clear
```
→ Resets conversation history for your chat.

---

## Personality — `Soul.md`

The assistant's character is defined entirely in `Soul.md` at the repo root. It's plain Markdown — edit it freely. Changes take effect on the next restart (or next new conversation session).

The file is loaded at session creation and prepended to the system prompt. If it's missing, the assistant falls back to the default `ConversationOptions.SystemPrompt` with a warning log.

In Docker, `Soul.md` is `COPY`'d into the image at `/app/Soul.md`. To iterate on personality without rebuilding, mount it as a volume:

```yaml
# docker-compose.yml
worker:
  volumes:
    - ./Soul.md:/app/Soul.md
```

---

## Local Development (without Docker)

```bash
# Start only postgres
docker compose up postgres -d

# Set local secrets (gitignored)
cp src/ClawPilot.Worker/appsettings.json src/ClawPilot.Worker/appsettings.Local.json
# edit appsettings.Local.json with real values

# Run the dashboard
dotnet run --project src/ClawPilot.Dashboard

# Run the worker
dotnet run --project src/ClawPilot.Worker
```

Alternatively use [.NET User Secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets):

```bash
cd src/ClawPilot.Worker
dotnet user-secrets set "Telegram:BotToken" "your-token"
dotnet user-secrets set "GitHub:CopilotToken" "your-token"
dotnet user-secrets set "GitHub:Mcp:Pat" "your-pat"
dotnet user-secrets set "WebSearch:TavilyApiKey" "your-key"
```

---

## Configuration Reference

**`appsettings.json`** (committed — all secrets empty, override in `appsettings.Local.json` or env vars)

```json
{
  "AgentBudget": {
    "MaxIterations": 15,
    "MaxToolCallsPerSession": 40,
    "Timeout": "00:10:00"
  },
  "Journal": {
    "Verbosity": "Full",
    "RetainThoughtStepsDays": 7
  },
  "Conversation": {
    "SystemPrompt": ""
  },
  "GitHub": {
    "Mcp": {
      "McpUrl": "https://api.githubcopilot.com/mcp/"
    }
  }
}
```

| `Journal.Verbosity` | What's stored |
|---|---|
| `Full` | ThoughtSteps + ToolIntent + ToolOutcome + CorrectionStep + SessionSummary |
| `Standard` | ToolIntent + ToolOutcome + CorrectionStep + SessionSummary |
| `Minimal` | SessionSummary only |

---

## Architecture Decisions

### Two separate `CopilotClient` instances
`CopilotService` (task mode) and `ConversationService` (chat mode) each own a `CopilotClient`. This keeps their lifecycles independent — a crashing task run cannot corrupt an ongoing conversation session.

### SDK-native conversation history
Conversation history is managed entirely by the Copilot SDK (`CopilotSession` with `InfiniteSessions` enabled by default). ClawPilot only stores the `chatId → sessionId` mapping in SQLite so sessions can be **resumed across restarts** via `ResumeSessionAsync`. No manual message serialisation.

### `escalate_to_task` as a real tool
Escalation from conversation to task mode is implemented as an `AIFunction` registered in the conversation session's tool list. The model decides when to call it — no heuristics, no keyword matching. This means the LLM's own judgment governs the transition, and the decision is visible in logs.

### `Soul.md` loaded at session creation, not startup
The personality file is read fresh for each new conversation session (not once at startup). This means you can edit `Soul.md` and the next new chat picks it up without a full restart. Existing in-progress sessions are unaffected.

### `TelegramCommand` record carries `ChatId`
The internal channel between the Telegram update handler and the Worker loop carries a `TelegramCommand(ChatId, Text)` record instead of a raw string. This enables per-chat routing (conversation history is keyed by `ChatId`), `/clear` scoped to the right chat, and a clean path to multi-user support later.

### Permission policy enforced at the tool level, not the transport level
GitHub MCP tool filtering via request headers was removed after it caused 400 errors. Tool availability is now governed entirely by `DefaultPermissionPolicy` — `AutoApprove`, `RequireConfirmation`, or `AlwaysBlock` — applied at execution time. This keeps the permission model in one place regardless of tool source.

---

## Adding a New Tool

1. Add a public method to any class (or create a new one in `Tools/`)
2. Decorate it with `[CopilotTool("tool_name", "description")]`
3. Register the instance in `Program.cs` under `localTools`
4. Add it to `DefaultPermissionPolicy` in `Models/PermissionPolicy.cs`

```csharp
public class MyTool(ILogger<MyTool> logger)
{
    [CopilotTool("my_tool", "Does something useful for the agent.")]
    public async Task<string> DoSomethingAsync(string input)
    {
        // ...
        return result;
    }
}
```

---

## Tech Stack

| Component | Technology |
|---|---|
| Agent SDK | [GitHub Copilot SDK](https://github.com/github/copilot-sdk) (GPT-4o) |
| MCP Client | [ModelContextProtocol](https://github.com/modelcontextprotocol/csharp-sdk) |
| Telegram | [Telegram.Bot](https://github.com/TelegramBots/Telegram.Bot) |
| Journal | EF Core 9 + SQLite |
| Dashboard | Blazor Server + ASP.NET Core SignalR |
| Logging | Serilog (compact JSON, rolling file) |
| Database tool | Dapper + Npgsql (PostgreSQL) |
| Build analysis | Microsoft.CodeAnalysis.Sarif |
| Web search | [Tavily MCP](https://docs.tavily.com/documentation/mcp) (free tier) |
| Runtime | .NET 10 |

---

## Roadmap (v2)

- [ ] Auto-detect task vs. conversation (LLM classifier routing, no `/task` prefix needed)
- [ ] Postgres swap for journal (EF Core connection string change only)
- [ ] Parallel tool calls
- [ ] Fine-tuning pipeline from `CorrectionStep` rows
- [ ] Additional GitHub MCP tools (`search_code`, `list_commits`, Dependabot alerts)
- [ ] Richer Blazor dashboard (session history, tool call timeline)
- [ ] Consolidate to single `CopilotClient` (shared CLI process, lower resource overhead)

---

## SDK Compliance Backlog

Issues identified against the [official SDK docs](https://github.com/github/copilot-sdk/blob/main/dotnet/README.md).
Tracked as **Phase 10** in plan.md.

| # | Severity | Issue | Status |
|---|---|---|---|
| 1 | 🔴 Critical | `OnPermissionRequest` is **Required** but missing from `CopilotService` | Pending |
| 2 | 🟡 Medium | `GitHubToken` not passed to `CopilotClient` — relies on `gh auth login` in container | Pending |
| 3 | 🟡 Medium | `session.On()` return value discarded in `CopilotService` — handler leak per run | Pending |
| 4 | 🟡 Medium | `PermissionRequestResult.Kind = "allow"` is unvalidated — use `PermissionRequestResultKind.Approved` once SDK is upgraded | Pending |
| 5 | 🔵 Low | SDK v0.1.25-preview.0 is outdated — missing `PermissionHandler.ApproveAll`, `gpt-5` support | Pending |
| 6 | 🔵 Low | No `OnUserInputRequest` in `ConversationService` — agent cannot use built-in `ask_user` tool | Pending |

> **Note:** Items 1 and 3 are the most impactful for stability. Fix these before the next major feature.

---

## License

MIT
