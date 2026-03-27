# 🦀 ClawPilot

An autonomous AI agent that accepts natural-language tasks via **Telegram**, runs them through the **GitHub Copilot SDK (GPT-4o)** in an agentic loop with tools, streams real-time output to a **Blazor dashboard** via SignalR, and asks a human operator for permission before any sensitive action.

```
Telegram ──► Worker (Agentic Loop) ──► GitHub Copilot SDK (GPT-4o)
                │         │                        │
                │    Local Tools ◄─────────────────┘
                │    GitHub MCP  (api.githubcopilot.com)
                │    Tavily MCP  (mcp.tavily.com)
                │
                └──► Dashboard (Blazor + SignalR) ──► Browser
```

---

## Features

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
├── docker-compose.yml
└── src/
    ├── ClawPilot.Dashboard/     # Blazor Server — real-time log viewer
    └── ClawPilot.Worker/        # .NET Worker Service — agentic loop engine
        ├── Services/
        │   ├── CopilotService       # Agentic loop, budget, journal orchestration
        │   ├── TelegramService      # Bot polling, tiered permission gate
        │   ├── GitHubMcpService     # Remote MCP → api.githubcopilot.com/mcp/
        │   ├── WebSearchMcpService  # Remote MCP → mcp.tavily.com/mcp/
        │   ├── AgentJournalService  # EF Core SQLite journal
        │   └── LogService           # SignalR client → Dashboard
        ├── Tools/
        │   ├── BuildTool            # dotnet build → typed BuildResult (SARIF)
        │   └── DbTool               # PostgreSQL schema introspection + read-only queries
        ├── Models/                  # AgentSession, ToolIntent, ToolOutcome, etc.
        ├── Data/                    # ClawPilotDbContext
        └── Options/                 # AgentBudgetOptions, JournalOptions, etc.
```

---

## Tools

| Tool | Source | Permission | Description |
|---|---|---|---|
| `dotnet_build` | Local | AutoApprove | Runs `dotnet build`, returns typed errors/warnings via SARIF |
| `get_schema` | Local | AutoApprove | Lists tables + columns from PostgreSQL (supports table filter) |
| `execute_query` | Local | AutoApprove | Runs SELECT-only SQL (max 200 rows, 5s timeout) |
| `get_file_contents` | GitHub MCP | AutoApprove | Reads a file from any GitHub repo |
| `list_pull_requests` | GitHub MCP | AutoApprove | Lists PRs on a repo |
| `get_workflow_run` | GitHub MCP | AutoApprove | Gets a GitHub Actions workflow run |
| `get_workflow_run_logs` | GitHub MCP | AutoApprove | Fetches GitHub Actions logs |
| `create_issue` | GitHub MCP | **Confirm** | Creates a GitHub issue |
| `push_files` | GitHub MCP | **Confirm** | Pushes file changes to a repo |
| `create_pull_request` | GitHub MCP | **Confirm** | Opens a pull request |
| `tavily-search` | Tavily MCP | **Confirm** | Real-time web search |
| `tavily-extract` | Tavily MCP | **Confirm** | Extracts content from a URL |

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
TELEGRAM_CHAT_ID=          # your Telegram chat/user ID
GITHUB_COPILOT_TOKEN=      # GitHub → Settings → Copilot
GITHUB_PAT=                # Fine-grained PAT (repo read, actions read, issues write)
TAVILY_API_KEY=            # Free at app.tavily.com (1,000 req/month)
```

### 2. Run with Docker Compose

```bash
docker compose up --build
```

This starts:
- **postgres** — PostgreSQL 17 for the agent's DB tool
- **dashboard** — Blazor Server on `http://localhost:8080`
- **worker** — The agentic loop (waits for Telegram messages)

### 3. Send a task

Open Telegram, message your bot:

```
Describe the schema of the database and list tables with more than 5 columns
```

The agent will reason, call tools, ask for confirmation on write operations, and report back.

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
  "GitHub": {
    "Mcp": {
      "McpUrl": "https://api.githubcopilot.com/mcp/",
      "Tools": ["get_file_contents", "list_pull_requests", "..."]
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

- [ ] Postgres swap for journal (EF Core connection string change only)
- [ ] Parallel tool calls
- [ ] Fine-tuning pipeline from `CorrectionStep` rows
- [ ] Additional GitHub MCP tools (`search_code`, `list_commits`, Dependabot alerts)
- [ ] Richer Blazor dashboard (session history, tool call timeline)

---

## License

MIT
