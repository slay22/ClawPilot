Here's the fully revised plan with the GitHub MCP Server integrated:

---

## Claw-Pilot v1 — Revised Build Plan (v3)

### Phase 0 — Foundation

**Goal:** Minimal runnable Worker Service with Copilot SDK authenticated and structured logging in place.

1. `dotnet new worker -n ClawPilot`
2. Add packages: `GitHub.Copilot.SDK`, `Microsoft.Extensions.Hosting`, `Serilog.Extensions.Hosting`
3. Implement `CopilotAgentService : BackgroundService` that initializes the SDK, runs one dummy loop iteration, logs it via Serilog, and exits cleanly.
4. Configure structured JSON logging to file from the start.
5. Verify `gh auth login` works inside the container context.

**Exit criteria:** `dotnet run` starts, authenticates, logs one structured entry, shuts down cleanly.

---

### Phase 1 — Session Lifecycle & Budget Enforcement

**Goal:** Every agent run is a tracked session with a hard budget. Nothing runs unbounded.

**1a — Session entity**

```csharp
class AgentSession {
    public Guid Id { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string TriggerSource { get; set; }  // "Telegram:chatId:messageId"
    public string InitialPrompt { get; set; }
    public string Status { get; set; }         // Completed | BudgetExhausted | Denied | Faulted
    public int IterationsUsed { get; set; }
    public int ToolCallsUsed { get; set; }
    public string? FinalSummary { get; set; }
    public string? FaultReason { get; set; }
}
```

**1b — Budget config**

```json
"AgentBudget": {
  "MaxIterations": 15,
  "MaxToolCallsPerSession": 40,
  "Timeout": "00:10:00"
}
```

**1c — AgentRunner responsibilities**

- `OpenSessionAsync(trigger, prompt)` → creates and persists `AgentSession`
- Increments `IterationsUsed` and `ToolCallsUsed` on every tick
- `CancellationTokenSource` chained from host lifetime token + budget timeout
- `CloseSessionAsync(sessionId, status, summary?, faultReason?)` → writes `EndedAt` and final status

**Exit criteria:** Two runs produce two `AgentSession` rows. Setting `MaxIterations: 2` produces status `BudgetExhausted`.

---

### Phase 2 — SQLite Journal & Typed Thinking Traces

**Goal:** Structured, queryable memory that captures the full reasoning process.

**2a — EF Core setup**

1. Add `Microsoft.EntityFrameworkCore.Sqlite`
2. All entities foreign-key to `AgentSession.Id`
3. Apply migrations on startup via `context.Database.MigrateAsync()`

**2b — Typed trace entities**

```csharp
class ThoughtStep {
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public int SequenceNumber { get; set; }
    public DateTime Timestamp { get; set; }
    public string ReasoningText { get; set; }
}

class ToolIntent {
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public int SequenceNumber { get; set; }
    public DateTime Timestamp { get; set; }
    public string ToolName { get; set; }
    public string ToolSource { get; set; }     // "Local" | "GitHubMCP" | "WebSearchMCP"
    public string ArgumentsJson { get; set; }
    public string? ReasoningExcerpt { get; set; }
}

class ToolOutcome {
    public Guid Id { get; set; }
    public Guid ToolIntentId { get; set; }
    public Guid SessionId { get; set; }
    public DateTime Timestamp { get; set; }
    public string PermissionTier { get; set; }
    public bool Approved { get; set; }
    public bool Succeeded { get; set; }
    public long LatencyMs { get; set; }
    public string? ResultJson { get; set; }
    public string? ErrorMessage { get; set; }
}

class CorrectionStep {
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public int IterationNumber { get; set; }
    public DateTime Timestamp { get; set; }
    public string FailedApproach { get; set; }
    public string ErrorSignal { get; set; }
    public string RevisedApproach { get; set; }
}

class SessionSummary {
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public DateTime Timestamp { get; set; }
    public string FinalAnswer { get; set; }
    public int CorrectionsCount { get; set; }
    public int ThoughtStepsCount { get; set; }
    public string BudgetConsumedJson { get; set; }  // { iterations, toolCalls, elapsedMs }
}
```

Note the added `ToolSource` field on `ToolIntent` — this lets you later query "how many GitHub MCP calls did this session make vs local tools" which will be useful for cost and latency analysis.

**2c — AgentJournal service**

```csharp
interface IAgentJournal {
    Task AppendThoughtAsync(Guid sessionId, int seq, string reasoning);
    Task AppendToolIntentAsync(Guid sessionId, int seq, string tool, string source, string argsJson, string? excerpt);
    Task AppendToolOutcomeAsync(Guid toolIntentId, Guid sessionId, ToolOutcome outcome);
    Task AppendCorrectionAsync(Guid sessionId, int iteration, CorrectionStep correction);
    Task WriteSessionSummaryAsync(Guid sessionId, SessionSummary summary);
}
```

**2d — Verbosity config**

```json
"Journal": {
  "Verbosity": "Full",
  "RetainThoughtStepsDays": 7
}
```

| Level | What's stored |
|---|---|
| `Full` | Everything — use in dev |
| `Standard` | ToolIntent + ToolOutcome + CorrectionStep + SessionSummary |
| `Minimal` | SessionSummary only |

**Exit criteria:** A completed session produces rows across all typed tables. `CorrectionStep` rows are queryable by session.

---

### Phase 3 — Self-Healer Tool (Local)

**Goal:** One working `[CopilotTool]` that runs `dotnet build`, parses SARIF, and returns structured errors. This is kept as a local tool because it operates on the local filesystem — GitHub MCP handles the remote side.

1. `[CopilotTool] Task<BuildResult> RunBuildAsync(string projectPath)`:
   - Runs `dotnet build --no-restore /p:ErrorFormat=sarif`
   - Parses SARIF into typed `BuildError[]` and `BuildWarning[]`
2. `BuildError` record:
   ```csharp
   record BuildError(string FilePath, int Line, int Column, string Message, string RuleId);
   ```
3. `ToolSource = "Local"`, `AutoApprove` tier.
4. Journal records `ToolIntent` before and `ToolOutcome` after every call.

**Richer debugging loop enabled by GitHub MCP (Phase 4):** Once GitHub MCP is wired, the agent can correlate a local SARIF error with the matching failed GitHub Actions workflow run — fetching remote logs to cross-reference with the local build output. This emergent capability requires no extra local code.

**Exit criteria:** A project with a known compile error produces a correctly parsed `BuildError`. `ToolIntent` and `ToolOutcome` rows exist in the journal with `ToolSource: "Local"`.

---

### Phase 4 — GitHub MCP Server

**Goal:** Agent gets full GitHub awareness — repos, files, PRs, issues, Actions — via the official GitHub MCP Server, with no sidecar to manage.

**4a — Transport**

Use the **remote hosted** GitHub MCP Server over HTTP. No local process, no Docker sidecar. Auth via a fine-grained GitHub PAT stored in environment variables.

```json
"GitHub": {
  "McpUrl": "https://api.githubcopilot.com/mcp/",
  "PatSecretName": "GitHub__Pat"
}
```

The PAT needs only the minimum scopes required: `repo` (read), `actions` (read), `issues` (write if creating issues is desired).

**4b — Tool selection via header**

Load only what the agent needs using `X-MCP-Tools` to keep context window footprint small:

```
X-MCP-Tools: get_file_contents, list_pull_requests, get_workflow_run, get_workflow_run_logs, create_issue, push_files, create_pull_request
```

**4c — Permission tier table**

| Tool | Tier | Reason |
|---|---|---|
| `get_file_contents` | `AutoApprove` | Read-only |
| `list_pull_requests` | `AutoApprove` | Read-only |
| `search_code` | `AutoApprove` | Read-only |
| `get_workflow_run` | `AutoApprove` | Read-only |
| `get_workflow_run_logs` | `AutoApprove` | Read-only |
| `create_issue` | `RequireConfirmation` | Creates remote artifact |
| `push_files` | `RequireConfirmation` | Modifies repo |
| `create_pull_request` | `RequireConfirmation` | Modifies repo |

**4d — Lockdown mode**

Enable GitHub MCP's Lockdown mode from day one to protect against prompt injection from untrusted repo content. This is a header flag — no code change required.

**4e — Journaling**

All GitHub MCP calls are journaled with `ToolSource: "GitHubMCP"`. This keeps them queryable and distinguishable from local tool calls in the session trace.

**Exit criteria:** Agent fetches a file from a repo (`get_file_contents`), journals it as `ToolSource: "GitHubMCP"` with `AutoApprove`. Triggering `create_issue` sends a Telegram confirmation button.

---

### Phase 5 — Telegram & Tiered Permission Policy

**Goal:** Human-in-the-loop approvals with trust tiers across all tool sources.

**5a — Permission policy**

```csharp
enum PermissionTier { AutoApprove, RequireConfirmation, AlwaysBlock }

record ToolPermission(string ToolName, string ToolSource, PermissionTier Tier, string? Reason = null);
```

The policy table now spans all three tool sources:

| Tool | Source | Tier |
|---|---|---|
| `RunBuildAsync` | Local | `AutoApprove` |
| `QueryDbReadOnly` | Local | `AutoApprove` |
| `QueryDbSchema` | Local | `AutoApprove` |
| `WriteFile` (in-project) | Local | `RequireConfirmation` |
| `WriteFile` (outside root) | Local | `AlwaysBlock` |
| `ExecuteArbitraryShell` | Local | `AlwaysBlock` |
| `get_file_contents` | GitHubMCP | `AutoApprove` |
| `get_workflow_run_logs` | GitHubMCP | `AutoApprove` |
| `create_issue` | GitHubMCP | `RequireConfirmation` |
| `push_files` | GitHubMCP | `RequireConfirmation` |
| `create_pull_request` | GitHubMCP | `RequireConfirmation` |
| `WebSearchAsync` | WebSearchMCP | `RequireConfirmation` |

**5b — Telegram wiring**

1. Add `Telegram.Bot`, implement `TelegramGateway` as singleton.
2. `OnPermissionRequest` behavior:
   - `AutoApprove` → log, return `true`, no message
   - `RequireConfirmation` → Inline Keyboard (✅ Approve / ❌ Deny), 60s timeout, timeout = implicit deny
   - `AlwaysBlock` → notification-only message, return `false`
3. Pending approvals in `ConcurrentDictionary<string, TaskCompletionSource<bool>>` keyed by callback GUID.
4. Every outcome written to `ToolOutcome` with `Approved: true/false`.
5. Session lifecycle messages to Telegram:
   - Opened: *"🤖 Session started: {prompt truncated}"*
   - Closed: *"✅ Done in {n} iterations | 🔁 {corrections} corrections | ⏱ {elapsed}"*
   - Budget hit: *"⚠️ BudgetExhausted after {n} iterations"*
   - Faulted: *"❌ Faulted: {reason}"*

**Exit criteria:** `push_files` triggers Telegram button. Approve writes `Approved: true` to `ToolOutcome` with `ToolSource: "GitHubMCP"`. Deny closes session with status `Denied`.

---

### Phase 6 — DB Read-Only Tool

**Goal:** Agent can introspect schema and run safe queries.

1. `[CopilotTool] Task<SchemaResult> GetSchemaAsync(string? tableFilter)`:
   - Queries `information_schema` (or Oracle `ALL_COLUMNS` for APAX)
   - Returns table names, column names, types — no row data
   - `ToolSource: "Local"`, `AutoApprove`
2. `[CopilotTool] Task<QueryResult> RunReadOnlyQueryAsync(string sql)`:
   - Read-only DB user enforced at DB level
   - Max 200 rows, 5s query timeout
   - Rejects non-SELECT at application layer
   - `ToolSource: "Local"`, `AutoApprove`
3. Connection string per environment in `appsettings.{env}.json`.

**Exit criteria:** Agent describes a table schema and retrieves sample rows. Both journaled as `ToolSource: "Local"` with correct intent/outcome pairs.

---

### Phase 7 — Web Search MCP (stdio, local)

**Goal:** Agent can search the web via a locally managed MCP sidecar. Kept separate from GitHub MCP — different concern, different transport.

1. Run MCP server as a child process via `System.Diagnostics.Process`, lifecycle tied to `IHostedService`.
2. Implement `McpStdioTransport` for JSON-RPC over stdin/stdout.
3. `[CopilotTool] Task<SearchResult> WebSearchAsync(string query)`:
   - `ToolSource: "WebSearchMCP"`, `RequireConfirmation`
   - Returns top N results with title, URL, snippet
4. `ToolIntent` captures query; `ToolOutcome` captures result count and latency.

**Exit criteria:** Agent sends a search query, Telegram shows confirmation button, approval triggers MCP call, results appear in next reasoning step, journaled with `ToolSource: "WebSearchMCP"`.

---

### Phase 8 — Docker Compose

**Goal:** Single local compose file. No unnecessary sidecars — GitHub MCP is remote, so only the Web Search MCP process runs locally (managed in-process, not as a separate container).

```yaml
services:
  clawpilot:
    build: .
    volumes:
      - ./data:/app/data      # SQLite journal + sessions
      - ./logs:/app/logs
    environment:
      - ASPNETCORE_ENVIRONMENT=Development
      - Telegram__BotToken=${TELEGRAM_BOT_TOKEN}
      - GitHub__Pat=${GITHUB_PAT}
    restart: unless-stopped
```

A `docker-compose.prod.yml` swaps SQLite for Postgres and adds any prod-specific env vars — no code changes required.

---

### Deferred to v2

- **Blazor/SignalR dashboard** — Telegram session messages cover v1 observability.
- **Postgres** — SQLite with EF Core means the swap is a connection string change.
- **Parallel tool calls** — prove sequential reliability first.
- **Full SQL injection hardening** — read-only DB role is the real guard for v1.
- **Fine-tuning pipeline** — `CorrectionStep` rows are accumulating a training dataset; using it is a v2 concern.
- **Additional GitHub MCP tools** — `search_code`, `list_commits`, Dependabot alerts available when needed.