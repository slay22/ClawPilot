using ClawPilot.Worker;
using ClawPilot.Worker.Data;
using ClawPilot.Worker.Options;
using ClawPilot.Worker.Services;
using ClawPilot.Worker.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Formatting.Compact;
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// Load local secret overrides (gitignored — never committed)
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Services.AddSerilog((sp, cfg) =>
{
    cfg.ReadFrom.Configuration(builder.Configuration)
       .WriteTo.Console(new CompactJsonFormatter())
       .WriteTo.File(new CompactJsonFormatter(), "logs/clawpilot-.log",
           rollingInterval: RollingInterval.Day,
           retainedFileCountLimit: 7);
});

// Configuration
builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection("Telegram"));
builder.Services.Configure<PrRunnerOptions>(builder.Configuration.GetSection("PrRunner"));
builder.Services.Configure<GitHubOptions>(builder.Configuration.GetSection("GitHub"));
builder.Services.Configure<GitHubMcpOptions>(builder.Configuration.GetSection("GitHub:Mcp"));
builder.Services.Configure<DashboardOptions>(builder.Configuration.GetSection("Dashboard"));
builder.Services.Configure<WebSearchMcpOptions>(builder.Configuration.GetSection("WebSearch"));
builder.Services.Configure<AgentBudgetOptions>(builder.Configuration.GetSection("AgentBudget"));
builder.Services.Configure<JournalOptions>(builder.Configuration.GetSection("Journal"));
builder.Services.Configure<ConversationOptions>(builder.Configuration.GetSection("Conversation"));
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.Configure<CliOptions>(builder.Configuration.GetSection("Cli"));

// EF Core SQLite journal
builder.Services.AddDbContextFactory<ClawPilotDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Journal") ?? "Data Source=data/clawpilot.db"));

// Core Services
builder.Services.AddSingleton<TelegramService>();
builder.Services.AddSingleton<LogService>();
builder.Services.AddSingleton<IAgentJournal, AgentJournalService>();

// Tools
builder.Services.AddSingleton<DbTool>(sp =>
    new DbTool(builder.Configuration.GetConnectionString("Database") ?? "", sp.GetRequiredService<ILogger<DbTool>>()));
builder.Services.AddSingleton<BuildTool>();
builder.Services.AddSingleton<EmailTool>();

// MCP Services
builder.Services.AddSingleton<GitHubMcpService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GitHubMcpService>());
builder.Services.AddSingleton<WebSearchMcpService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WebSearchMcpService>());

// Scheduler Service (registered as singleton so Worker and CopilotService can both reference it)
builder.Services.AddSingleton<SchedulerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SchedulerService>());

// Copilot Service
builder.Services.AddSingleton<CopilotService>(sp =>
{
    List<object> localTools =
    [
        sp.GetRequiredService<DbTool>(),
        sp.GetRequiredService<BuildTool>(),
        sp.GetRequiredService<EmailTool>(),
        sp.GetRequiredService<SchedulerService>(),
    ];
    return new CopilotService(
        sp.GetRequiredService<TelegramService>(),
        sp.GetRequiredService<LogService>(),
        sp.GetRequiredService<IAgentJournal>(),
        sp.GetRequiredService<IOptions<AgentBudgetOptions>>(),
        sp.GetRequiredService<IOptions<GitHubOptions>>(),
        sp.GetRequiredService<ILogger<CopilotService>>(),
        localTools,
        sp.GetRequiredService<GitHubMcpService>(),
        sp.GetRequiredService<WebSearchMcpService>()
    );
});

// Wire CopilotService into SchedulerService (breaks circular dep)
builder.Services.AddSingleton(sp =>
{
    SchedulerService scheduler = sp.GetRequiredService<SchedulerService>();
    scheduler.CopilotService = sp.GetRequiredService<CopilotService>();
    return scheduler;
});

// Conversation Service (CopilotService injected after construction to break circular dep)
builder.Services.AddSingleton<ConversationService>(sp =>
{
    ConversationService svc = new(
        sp.GetRequiredService<TelegramService>(),
        sp.GetRequiredService<IDbContextFactory<ClawPilotDbContext>>(),
        sp.GetRequiredService<IOptions<ConversationOptions>>(),
        sp.GetRequiredService<IOptions<GitHubOptions>>(),
        sp.GetRequiredService<WebSearchMcpService>(),
        sp.GetRequiredService<ILogger<ConversationService>>()
    )
    {
        CopilotService = sp.GetRequiredService<CopilotService>()
    };
    return svc;
});
builder.Services.AddHostedService(sp => sp.GetRequiredService<ConversationService>());

// PR Webhook + Runner Services
builder.Services.AddSingleton<PrWebhookService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PrWebhookService>());
builder.Services.AddHostedService<PrRunnerService>();

// CLI API Service
builder.Services.AddSingleton<CliApiService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CliApiService>());

builder.Services.AddHostedService<Worker>();

IHost host = builder.Build();

using (IServiceScope scope = host.Services.CreateScope())
{
    IDbContextFactory<ClawPilotDbContext> factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ClawPilotDbContext>>();
    ClawPilotDbContext db = factory.CreateDbContext();
    await using (db)
    {
        // Creates schema on first run. For existing databases, the IF NOT EXISTS
        // guards below handle any tables added after the initial creation.
        await db.Database.EnsureCreatedAsync();

        // Idempotent guards for schema additions — safe to run on every startup.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "ConversationMessages" (
                "Id"               TEXT NOT NULL CONSTRAINT "PK_ConversationMessages" PRIMARY KEY,
                "TelegramChatId"   TEXT NOT NULL DEFAULT '',
                "CopilotSessionId" TEXT NOT NULL DEFAULT '',
                "CreatedAt"        TEXT NOT NULL DEFAULT '0001-01-01T00:00:00',
                "UpdatedAt"        TEXT NOT NULL DEFAULT '0001-01-01T00:00:00'
            )
            """);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_ConversationMessages_TelegramChatId"
            ON "ConversationMessages" ("TelegramChatId")
            """);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "ScheduledTasks" (
                "Id"            TEXT NOT NULL CONSTRAINT "PK_ScheduledTasks" PRIMARY KEY,
                "Label"         TEXT NOT NULL DEFAULT '',
                "Prompt"        TEXT NOT NULL DEFAULT '',
                "CronExpression" TEXT NOT NULL DEFAULT '',
                "RunMode"       INTEGER NOT NULL DEFAULT 0,
                "NeedsApproval" INTEGER NOT NULL DEFAULT 0,
                "IsEnabled"     INTEGER NOT NULL DEFAULT 1,
                "RunCount"      INTEGER NOT NULL DEFAULT 0,
                "LastRunAt"     TEXT,
                "NextRunAt"     TEXT NOT NULL DEFAULT '0001-01-01T00:00:00+00:00',
                "CreatedAt"     TEXT NOT NULL DEFAULT '0001-01-01T00:00:00+00:00'
            )
            """);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_ScheduledTasks_IsEnabled"
            ON "ScheduledTasks" ("IsEnabled")
            """);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_ScheduledTasks_NextRunAt"
            ON "ScheduledTasks" ("NextRunAt")
            """);
    }
}

host.Run();
