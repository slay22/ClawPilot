using ClawPilot.Worker;
using ClawPilot.Worker.Models;
using ClawPilot.Worker.Services;
using ClawPilot.Worker.Tools;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// Configuration
builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection("Telegram"));
builder.Services.Configure<GitHubOptions>(builder.Configuration.GetSection("GitHub"));
builder.Services.Configure<DashboardOptions>(builder.Configuration.GetSection("Dashboard"));
builder.Services.Configure<WebSearchOptions>(builder.Configuration.GetSection("WebSearch"));

// Core Services
builder.Services.AddSingleton<TelegramService>();
builder.Services.AddSingleton<LogService>();
builder.Services.AddSingleton<AgentJournal>();

// Tools
builder.Services.AddSingleton<DbTool>(sp =>
    new DbTool(builder.Configuration.GetConnectionString("Database") ?? "", sp.GetRequiredService<ILogger<DbTool>>()));
builder.Services.AddSingleton<BuildTool>();
builder.Services.AddSingleton<WebSearchTool>();

// Copilot Service - get all tools
builder.Services.AddSingleton<CopilotService>(sp =>
{
    List<object> tools =
    [
        sp.GetRequiredService<DbTool>(),
        sp.GetRequiredService<BuildTool>(),
        sp.GetRequiredService<WebSearchTool>(),
    ];
    return new CopilotService(
        sp.GetRequiredService<TelegramService>(),
        sp.GetRequiredService<LogService>(),
        sp.GetRequiredService<AgentJournal>(),
        sp.GetRequiredService<ILogger<CopilotService>>(),
        tools
    );
});

builder.Services.AddHostedService<Worker>();

IHost host = builder.Build();
host.Run();
