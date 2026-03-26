using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ClawPilot.Worker.Tools;

public class WebSearchOptions
{
    /// <summary>Brave Search API key. Get a free key at https://api.search.brave.com/</summary>
    public string BraveApiKey { get; set; } = string.Empty;

    /// <summary>Maximum number of search results to return to the agent.</summary>
    public int MaxResults { get; set; } = 5;
}

public class WebSearchTool(IOptions<WebSearchOptions> options, ILogger<WebSearchTool> logger)
{
    private readonly HttpClient _http = CreateHttpClient(options.Value);

    private static HttpClient CreateHttpClient(WebSearchOptions opts)
    {
        HttpClient http = new HttpClient();
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(opts.BraveApiKey))
            http.DefaultRequestHeaders.Add("X-Subscription-Token", opts.BraveApiKey);
        return http;
    }

    [CopilotTool("web_search", "Search the web for up-to-date information. Returns titles, URLs, and descriptions of the top results.")]
    public async Task<string> SearchAsync(string query)
    {
        logger.LogInformation("Web search: {query}", query);

        if (string.IsNullOrWhiteSpace(options.Value.BraveApiKey))
            return "Web search is not configured. Set WebSearch:BraveApiKey in configuration.";

        try
        {
            string url = $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(query)}&count={options.Value.MaxResults}";
            HttpResponseMessage response = await _http.GetAsync(url);
            response.EnsureSuccessStatusCode();

            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            JsonElement results = doc.RootElement
                .GetProperty("web")
                .GetProperty("results");

            StringBuilder sb = new();
            sb.AppendLine($"Web search results for: {query}");
            sb.AppendLine();

            int i = 1;
            foreach (JsonElement result in results.EnumerateArray())
            {
                string? title = result.TryGetProperty("title", out JsonElement t) ? t.GetString() : "(no title)";
                string? urlProp = result.TryGetProperty("url", out JsonElement u) ? u.GetString() : "";
                string? desc = result.TryGetProperty("description", out JsonElement d) ? d.GetString() : "";

                sb.AppendLine($"{i}. {title}");
                sb.AppendLine($"   URL: {urlProp}");
                if (!string.IsNullOrWhiteSpace(desc))
                    sb.AppendLine($"   {desc}");
                sb.AppendLine();
                i++;
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Web search failed for query: {query}", query);
            return $"Search failed: {ex.Message}";
        }
    }
}
