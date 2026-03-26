using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace ClawPilot.Worker.Tools;

public class DbTool(string connectionString, ILogger<DbTool> logger)
{
    [CopilotTool("get_schema", "Get the schema of the database including tables and columns.")]
    public async Task<string> GetSchemaAsync()
    {
        logger.LogInformation("Getting database schema...");
        using NpgsqlConnection conn = new(connectionString);
        await conn.OpenAsync();

        IEnumerable<string> tables = await conn.QueryAsync<string>(@"
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = 'public'
        ");

        StringBuilder schema = new();
        foreach (string table in tables)
        {
            schema.AppendLine($"Table: {table}");
            IEnumerable<dynamic> columns = await conn.QueryAsync(@"
                SELECT column_name, data_type
                FROM information_schema.columns
                WHERE table_name = @table
            ", new { table });
            foreach (dynamic col in columns)
            {
                schema.AppendLine($"  - {col.column_name} ({col.data_type})");
            }
        }

        return schema.ToString();
    }

    [CopilotTool("execute_query", "Execute a read-only SQL query and return the results as JSON.")]
    public async Task<string> ExecuteQueryAsync(string sql)
    {
        logger.LogInformation("Executing query: {sql}", sql);

        // Basic safety check: only SELECT
        if (!sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            return "Error: Only SELECT queries are allowed.";
        }

        using NpgsqlConnection conn = new(connectionString);
        await conn.OpenAsync();

        IEnumerable<dynamic> results = await conn.QueryAsync(sql);
        return JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
    }
}
