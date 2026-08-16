using Health.Mcp;
using Xunit;

namespace Health.Mcp.Tests;

/// <summary>
/// Tests for the AIP-160 window filter. These exist because the bug they replace was
/// invisible: the old code sent <c>?startTime=…&amp;endTime=…</c>, which the v4 API has no
/// such parameters for, so EVERY windowed call 400'd — including on the data types the
/// README called "verified", because the type had nothing to do with it. Nothing caught it
/// for a month.
/// </summary>
public class HealthFilterTests
{
    // ── Kind classification ────────────────────────────────────────────────

    [Theory]
    [InlineData("steps")]
    [InlineData("distance")]
    [InlineData("exercise")]
    [InlineData("heart_rate")]
    public void Interval_is_the_default_kind(string dataType)
        => Assert.Equal(HealthFilter.Kind.Interval, HealthFilter.ClassifyKind(dataType));

    [Theory]
    [InlineData("weight")]
    [InlineData("height")]
    public void Sample_types_are_classified_as_samples(string dataType)
        => Assert.Equal(HealthFilter.Kind.Sample, HealthFilter.ClassifyKind(dataType));

    [Theory]
    [InlineData("daily-vo2-max")]
    [InlineData("daily-heart-rate-variability")]
    [InlineData("daily-resting-heart-rate")]
    public void Daily_prefixed_types_are_daily_summaries(string dataType)
        => Assert.Equal(HealthFilter.Kind.DailySummary, HealthFilter.ClassifyKind(dataType));

    [Fact]
    public void Sleep_is_its_own_kind_bounded_on_end_time()
    {
        Assert.Equal(HealthFilter.Kind.Sleep, HealthFilter.ClassifyKind("sleep"));
        Assert.Equal("sleep.interval.end_time", HealthFilter.FieldFor("sleep"));
    }

    [Fact]
    public void Sample_set_is_overridable_so_a_misclassification_is_a_config_fix()
    {
        Assert.Equal(HealthFilter.Kind.Interval, HealthFilter.ClassifyKind("body-fat"));
        Assert.Equal(
            HealthFilter.Kind.Sample,
            HealthFilter.ClassifyKind("body-fat", ["weight", "height", "body-fat"]));
    }

    // ── The kebab/snake trap ───────────────────────────────────────────────

    [Fact]
    public void Filter_field_is_snake_case_even_though_the_path_segment_is_kebab()
    {
        // Same data type, two spellings in one request — the path keeps the hyphens,
        // the filter field must not. Getting this backwards is a 400.
        Assert.Equal(
            "daily_resting_heart_rate.date",
            HealthFilter.FieldFor("daily-resting-heart-rate"));
    }

    [Theory]
    [InlineData("steps", "steps.interval.start_time")]
    [InlineData("weight", "weight.sample_time.physical_time")]
    [InlineData("daily-vo2-max", "daily_vo2_max.date")]
    [InlineData("active-energy-burned", "active_energy_burned.interval.start_time")]
    public void Field_matches_the_kind(string dataType, string expected)
        => Assert.Equal(expected, HealthFilter.FieldFor(dataType));

    // ── Window construction ────────────────────────────────────────────────

    [Fact]
    public void No_bounds_means_no_filter_at_all()
    {
        Assert.Null(HealthFilter.BuildWindow("steps", null, null));
        Assert.Null(HealthFilter.BuildWindow("steps", "", "   "));
    }

    [Fact]
    public void A_full_window_uses_only_the_two_permitted_operators()
    {
        var f = HealthFilter.BuildWindow("steps", "2026-08-16T00:00:00Z", "2026-08-17T00:00:00Z");

        Assert.Equal(
            "steps.interval.start_time >= \"2026-08-16T00:00:00Z\" AND " +
            "steps.interval.start_time < \"2026-08-17T00:00:00Z\"",
            f);

        // The grammar admits only >= and <. Anything else is rejected upstream.
        Assert.DoesNotContain("<=", f);
        Assert.DoesNotContain(">=\"", f);          // spacing preserved
        Assert.DoesNotContain(" OR ", f);
    }

    [Fact]
    public void Either_bound_alone_is_valid()
    {
        Assert.Equal(
            "steps.interval.start_time >= \"2026-08-16T00:00:00Z\"",
            HealthFilter.BuildWindow("steps", "2026-08-16T00:00:00Z", null));
        Assert.Equal(
            "steps.interval.start_time < \"2026-08-17T00:00:00Z\"",
            HealthFilter.BuildWindow("steps", null, "2026-08-17T00:00:00Z"));
    }

    [Fact]
    public void Daily_types_take_a_bare_date_so_a_timestamp_is_truncated_not_rejected()
    {
        // Callers pass ISO instants everywhere else; they should not have to know which
        // types are date-keyed.
        Assert.Equal(
            "daily_heart_rate_variability.date >= \"2026-08-09\" AND " +
            "daily_heart_rate_variability.date < \"2026-08-16\"",
            HealthFilter.BuildWindow(
                "daily-heart-rate-variability", "2026-08-09T00:00:00Z", "2026-08-16T23:59:59Z"));
    }

    [Fact]
    public void An_already_bare_date_passes_through_unchanged()
        => Assert.Equal(
            "daily_vo2_max.date >= \"2026-08-01\"",
            HealthFilter.BuildWindow("daily-vo2-max", "2026-08-01", null));

    [Fact]
    public void A_bound_cannot_contribute_grammar()
    {
        // The grammar has no escape sequence. Stripping only the quote would leave the rest
        // of an injected fragment inside the literal; keeping only timestamp characters
        // means the bound cannot carry an operator or a keyword at all.
        var f = HealthFilter.BuildWindow("steps", "2026-08-16T00:00:00Z\" OR x >= \"", null);

        Assert.NotNull(f);
        Assert.Equal("steps.interval.start_time >= \"2026-08-16T00:00:00Z\"", f);
        Assert.Equal(2, f!.Count(c => c == '"'));   // exactly one literal, properly closed
        Assert.DoesNotContain(" OR ", f);
        Assert.DoesNotContain("x", f);
    }

    [Theory]
    [InlineData("DROP TABLE")]
    [InlineData("' OR 1=1 --")]
    [InlineData("AND steps.interval.start_time >= \"1970-01-01\"")]
    public void Junk_bounds_yield_an_unusable_literal_but_never_grammar(string junk)
    {
        // The guarantee is NOT "produces empty" — 'T' and 'Z' are legitimate timestamp
        // characters, so "DROP TABLE" survives as "T". The guarantee is that whatever
        // survives stays sealed inside one literal and carries no operator or keyword, so
        // the query can only fail upstream as a bad timestamp, never silently widen.
        var f = HealthFilter.BuildWindow("steps", junk, null);

        Assert.NotNull(f);
        Assert.StartsWith("steps.interval.start_time >= \"", f);
        Assert.EndsWith("\"", f);
        Assert.Equal(2, f!.Count(c => c == '"'));
        Assert.DoesNotContain(" OR ", f);
        Assert.DoesNotContain(" AND ", f);
        Assert.DoesNotContain("=", f[(f.IndexOf('"') + 1)..]);   // nothing operator-like inside
    }

    [Fact]
    public void Empty_data_type_is_rejected_rather_than_producing_a_dot_prefixed_field()
    {
        Assert.Throws<ArgumentException>(() => HealthFilter.ToSnake(""));
        Assert.Throws<ArgumentException>(() => HealthFilter.ClassifyKind("  "));
    }
}
