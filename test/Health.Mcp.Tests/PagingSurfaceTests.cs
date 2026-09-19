using System.ComponentModel;
using System.Reflection;
using Health.Mcp;
using ModelContextProtocol.Server;
using Xunit;

namespace Health.Mcp.Tests;

/// <summary>
/// The paging surface of the list tools.
///
/// <para><b>The gap these close.</b> The server already accepted a <c>pageToken</c> on
/// <c>list_datapoints</c> and already returned <c>nextPageToken</c> on every list response — but the
/// typed tools (<c>list_weight</c>, <c>list_sleep</c>, <c>list_steps</c>) took neither parameter, and
/// no description mentioned either. The Google Health cursor therefore survived the whole way to the
/// caller and was reachable from nowhere: a caller handed a first page had no way to ask for the
/// second, and no way to know there was one.</para>
///
/// <para>These are reflection tests over the tool surface on purpose. The failure was never in the
/// paging logic — that always worked — it was in what the tool DECLARED, which is the only thing a
/// calling model can see.</para>
/// </summary>
public class PagingSurfaceTests
{
    private static MethodInfo Tool(string wireName) =>
        typeof(HealthTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == wireName);

    private static string Description(string wireName) =>
        Tool(wireName).GetCustomAttribute<DescriptionAttribute>()!.Description;

    [Theory]
    [InlineData("list_weight")]
    [InlineData("list_sleep")]
    [InlineData("list_steps")]
    [InlineData("list_datapoints")]
    public void Every_list_tool_accepts_a_page_token(string wireName)
    {
        var names = Tool(wireName).GetParameters().Select(p => p.Name).ToList();

        Assert.Contains("pageToken", names);
        Assert.Contains("pageSize", names);
    }

    [Theory]
    [InlineData("list_weight")]
    [InlineData("list_sleep")]
    [InlineData("list_steps")]
    [InlineData("list_datapoints")]
    public void Every_list_tool_documents_the_cursor(string wireName)
    {
        // A parameter a model is never told about is a parameter that does not exist.
        var d = Description(wireName);

        Assert.Contains("nextPageToken", d, StringComparison.Ordinal);
        Assert.Contains("pageToken", d, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("list_weight")]
    [InlineData("list_sleep")]
    [InlineData("list_steps")]
    [InlineData("list_datapoints")]
    public void Every_list_tool_says_that_absence_means_completeness(string wireName)
    {
        // The single most load-bearing sentence: without it a caller reports page one as the whole
        // window, with exactly the confidence it would have had if it were.
        Assert.Contains("ABSENCE", Description(wireName), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_page_token_parameter_is_optional()
    {
        // Paging is additive: an existing caller that passes neither must keep working unchanged.
        foreach (var name in new[] { "list_weight", "list_sleep", "list_steps", "list_datapoints" })
        {
            foreach (var p in Tool(name).GetParameters().Where(p => p.Name is "pageToken" or "pageSize"))
                Assert.True(p.IsOptional, $"{name}.{p.Name} must be optional");
        }
    }

    [Fact]
    public void List_steps_warns_that_the_source_filter_runs_after_paging()
    {
        // The one genuine trap in this surface: filtering happens on the returned page, so a page
        // can be empty while later pages still hold matching points.
        var d = Description("list_steps");

        Assert.Contains("AFTER paging", d, StringComparison.Ordinal);
    }
}
