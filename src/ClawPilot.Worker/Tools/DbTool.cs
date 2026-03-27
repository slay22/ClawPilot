using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace ClawPilot.Worker.Tools;

public class DbTool(string connectionString, ILogger<DbTool> logger)
{
    [CopilotTool("get_schema", "List tables and columns from the database. Optionally filter by table name prefix.")]
    public async Task<string> GetSchemaAsync(string? tableFilter = null)
    {
        logger.LogInformation("Getting database schema...");
        using NpgsqlConnection conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        string tableQuery = string.IsNullOrEmpty(tableFilter)
            ? "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'"
            : "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_name LIKE @filter";

        IEnumerable<string> tables = string.IsNullOrEmpty(tableFilter)
            ? await conn.QueryAsync<string>(tableQuery)
            : await conn.QueryAsync<string>(tableQuery, new { filter = tableFilter + "%" });

        StringBuilder schema = new StringBuilder();
        foreach (string table in tables)
        {
            schema.AppendLine($"Table: {table}");
            IEnumerable<dynamic> columns = await conn.QueryAsync(@"
                SELECT column_name, data_type
                FROM information_schema.columns
                WHERE table_name = @table
            ", new { table });
            foreach (dynamic col in columns)
                schema.AppendLine($"  - {col.column_name} ({col.data_type})");
        }

        return schema.ToString();
    }

    [CopilotTool("execute_query", "Execute a read-only SQL query and return the results as JSON. Results are capped at 200 rows.")]
    public async Task<string> ExecuteQueryAsync(string sql)
    {
        logger.LogInformation("Executing query: {sql}", sql);

        if (!sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            return "Error: Only SELECT queries are allowed.";

        string normalizedSql = sql.TrimEnd();
        if (!normalizedSql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase))
            normalizedSql = normalizedSql + " LIMIT 200";

        using NpgsqlConnection conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        CommandDefinition cmd = new CommandDefinition(normalizedSql, commandTimeout: 5);
        IEnumerable<dynamic> results = await conn.QueryAsync(cmd);
        List<dynamic> resultList = [..results];

        string json = JsonSerializer.Serialize(resultList, new JsonSerializerOptions { WriteIndented = true });
        return resultList.Count == 200 ? "// Note: results capped at 200 rows\n" + json : json;
    }
}
