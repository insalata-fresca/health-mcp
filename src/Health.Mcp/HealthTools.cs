using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Health.Mcp;

/// <summary>
/// Read-only tools over the operator's aggregated Health Connect data (Garmin +
/// Withings + phone) as exposed by the Google Health API v4. Every tool is a GET;
/// none mutate anything. The <see cref="GoogleHealthClient"/> is DI-injected into
/// each static tool method; the remaining
/// parameters are the MCP tool arguments.
/// </summary>
[McpServerToolType]
public sealed class HealthTools
{
    private static readonly JsonSerializerOptions _json =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [McpServerTool(Name = "health_list_weight")]
    [Description(
        "List body-weight data points from Google Health (Withings + Garmin + phone, aggregated via " +
        "Health Connect). Read-only. Optional ISO-8601 start/end bound the window when supplied. " +
        "Verified data type.")]
    public static Task<string> ListWeight(
        GoogleHealthClient client,
        [Description("Optional ISO-8601 window start (e.g. 2026-07-01T00:00:00Z). Empty = no start bound.")] string start = "",
        [Description("Optional ISO-8601 window end. Empty = no end bound.")] string end = "",
        CancellationToken ct = default)
        => ListAsync(client, "weight", start, end, ct);

    [McpServerTool(Name = "health_list_sleep")]
    [Description(
        "List sleep data points from Google Health (Garmin + phone). Read-only. Optional ISO-8601 " +
        "start/end bound the window. Verified data type.")]
    public static Task<string> ListSleep(
        GoogleHealthClient client,
        [Description("Optional ISO-8601 window start. Empty = no start bound.")] string start = "",
        [Description("Optional ISO-8601 window end. Empty = no end bound.")] string end = "",
        CancellationToken ct = default)
        => ListAsync(client, "sleep", start, end, ct);

    [McpServerTool(Name = "health_list_steps")]
    [Description(
        "List step-count data points from Google Health (Garmin + phone). Read-only. Optional ISO-8601 " +
        "start/end bound the window. Verified data type.")]
    public static Task<string> ListSteps(
        GoogleHealthClient client,
        [Description("Optional ISO-8601 window start. Empty = no start bound.")] string start = "",
        [Description("Optional ISO-8601 window end. Empty = no end bound.")] string end = "",
        CancellationToken ct = default)
        => ListAsync(client, "steps", start, end, ct);

    [McpServerTool(Name = "health_list_datapoints")]
    [Description(
        "Generic read of Google Health API v4 data points for ANY data type string. Read-only. " +
        "dataType examples: weight, sleep, steps (verified), heart_rate / activity types (coverage " +
        "to be confirmed against the live account). Optional ISO-8601 start/end bound the window when " +
        "supplied. Use the typed tools (health_list_weight/sleep/steps) for the common cases.")]
    public static Task<string> ListDatapoints(
        GoogleHealthClient client,
        [Description("Google Health data type, e.g. weight, sleep, steps, heart_rate.")] string dataType,
        [Description("Optional ISO-8601 window start. Empty = no start bound.")] string start = "",
        [Description("Optional ISO-8601 window end. Empty = no end bound.")] string end = "",
        CancellationToken ct = default)
        => ListAsync(client, dataType, start, end, ct);

    [McpServerTool(Name = "health_list_data_types")]
    [Description(
        "List the Google Health data types this MCP is configured to advertise (env GOOGLE_HEALTH_DATA_TYPES). " +
        "Read-only, no upstream call. weight/sleep/steps are verified; others are best-effort until confirmed.")]
    public static string ListDataTypes(HealthOptions opt)
        => JsonSerializer.Serialize(new { data_types = opt.DataTypes }, _json);

    private static async Task<string> ListAsync(
        GoogleHealthClient client, string dataType, string start, string end, CancellationToken ct)
    {
        try
        {
            var result = await client.ListDataPointsAsync(
                dataType,
                string.IsNullOrWhiteSpace(start) ? null : start,
                string.IsNullOrWhiteSpace(end) ? null : end,
                ct).ConfigureAwait(false);

            var count = result.ValueKind == JsonValueKind.Object
                        && result.TryGetProperty("dataPoints", out var dp)
                        && dp.ValueKind == JsonValueKind.Array
                ? dp.GetArrayLength()
                : (int?)null;

            return JsonSerializer.Serialize(new
            {
                dataType,
                count,
                dataPoints = result,
            }, _json);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                dataType,
                error = ex.Message,
            }, _json);
        }
    }
}
