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
builder.Services.Configure<GitHubOptions>(builder.Configuration.GetSection("GitHub"));
builder.Services.Configure<GitHubMcpOptions>(builder.Configuration.GetSection("GitHub:Mcp"));
builder.Services.Configure<DashboardOptions>(builder.Configuration.GetSection("Dashboard"));
builder.Services.Configure<WebSearchMcpOptions>(builder.Configuration.GetSection("WebSearch"));
builder.Services.Configure<AgentBudgetOptions>(builder.Configuration.GetSection("AgentBudget"));
builder.Services.Configure<JournalOptions>(builder.Configuration.GetSection("Journal"));

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

// MCP Services
builder.Services.AddSingleton<GitHubMcpService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GitHubMcpService>());
builder.Services.AddSingleton<WebSearchMcpService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WebSearchMcpService>());

// Copilot Service
builder.Services.AddSingleton<CopilotService>(sp =>
{
    List<object> localTools =
    [
        sp.GetRequiredService<DbTool>(),
        sp.GetRequiredService<BuildTool>(),
    ];
    return new CopilotService(
        sp.GetRequiredService<TelegramService>(),
        sp.GetRequiredService<LogService>(),
        sp.GetRequiredService<IAgentJournal>(),
        sp.GetRequiredService<IOptions<AgentBudgetOptions>>(),
        sp.GetRequiredService<ILogger<CopilotService>>(),
        localTools,
        sp.GetRequiredService<GitHubMcpService>(),
        sp.GetRequiredService<WebSearchMcpService>()
    );
});

builder.Services.AddHostedService<Worker>();

IHost host = builder.Build();

using (IServiceScope scope = host.Services.CreateScope())
{
    IDbContextFactory<ClawPilotDbContext> factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ClawPilotDbContext>>();
    ClawPilotDbContext db = factory.CreateDbContext();
    await db.Database.EnsureCreatedAsync();
    await db.DisposeAsync();
}

host.Run();
