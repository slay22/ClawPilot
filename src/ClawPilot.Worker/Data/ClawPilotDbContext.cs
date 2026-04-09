using ClawPilot.Worker.Models;
using Microsoft.EntityFrameworkCore;

namespace ClawPilot.Worker.Data;

public class ClawPilotDbContext(DbContextOptions<ClawPilotDbContext> options) : DbContext(options)
{
    public DbSet<AgentSession> AgentSessions => Set<AgentSession>();
    public DbSet<ThoughtStep> ThoughtSteps => Set<ThoughtStep>();
    public DbSet<ToolIntent> ToolIntents => Set<ToolIntent>();
    public DbSet<ToolOutcome> ToolOutcomes => Set<ToolOutcome>();
    public DbSet<CorrectionStep> CorrectionSteps => Set<CorrectionStep>();
    public DbSet<SessionSummary> SessionSummaries => Set<SessionSummary>();
    public DbSet<ConversationMessage> ConversationMessages => Set<ConversationMessage>();
    public DbSet<ScheduledTask> ScheduledTasks => Set<ScheduledTask>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AgentSession>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasMany(x => x.ThoughtSteps).WithOne(x => x.Session).HasForeignKey(x => x.SessionId);
            b.HasMany(x => x.ToolIntents).WithOne(x => x.Session).HasForeignKey(x => x.SessionId);
            b.HasMany(x => x.CorrectionSteps).WithOne(x => x.Session).HasForeignKey(x => x.SessionId);
            b.HasOne(x => x.Summary).WithOne(x => x.Session).HasForeignKey<SessionSummary>(x => x.SessionId);
        });

        modelBuilder.Entity<ToolIntent>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasOne(x => x.Outcome).WithOne(x => x.Intent).HasForeignKey<ToolOutcome>(x => x.ToolIntentId);
        });

        modelBuilder.Entity<ToolOutcome>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasOne(x => x.Session).WithMany().HasForeignKey(x => x.SessionId);
        });

        modelBuilder.Entity<ConversationMessage>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.TelegramChatId);
        });

        modelBuilder.Entity<ScheduledTask>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.IsEnabled);
            b.HasIndex(x => x.NextRunAt);
        });
    }
}
