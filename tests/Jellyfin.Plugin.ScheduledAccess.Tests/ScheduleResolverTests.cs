using System;
using Jellyfin.Plugin.ScheduledAccess.Configuration;
using Jellyfin.Plugin.ScheduledAccess.Scheduling;
using Xunit;

namespace Jellyfin.Plugin.ScheduledAccess.Tests;

/// <summary>
/// Tests for the slot logic. This is the part worth testing: it decides when a
/// restriction is in force, it has no server dependencies, and every awkward
/// case lives here — midnight wrapping, overlaps and boundary maths.
/// </summary>
public class ScheduleResolverTests
{
    // 2026-08-16 is a Sunday. Fixed dates rather than DateTime.Now so the
    // suite doesn't pass or fail depending on the day it runs.
    private static readonly DateTime Sunday = new(2026, 8, 16);
    private static readonly DateTime Monday = new(2026, 8, 17);
    private static readonly DateTime Saturday = new(2026, 8, 15);

    private static readonly Guid UserA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid UserB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static ScheduleRule Rule(int start, int end, Guid user, params DayOfWeek[] days)
        => new()
        {
            UserId = user,
            StartMinutes = start,
            EndMinutes = end,
            Days = days
        };

    private static ScheduleRule Rule(int start, int end, params DayOfWeek[] days)
        => Rule(start, end, UserA, days);

    [Fact]
    public void SlotIsActiveInsideItsWindow()
    {
        var rule = Rule(480, 660, DayOfWeek.Sunday);   // 08:00-11:00

        Assert.True(ScheduleResolver.IsActiveAt(rule, Sunday.AddHours(9)));
    }

    [Fact]
    public void SlotStartIsInclusive()
    {
        var rule = Rule(480, 660, DayOfWeek.Sunday);

        Assert.True(ScheduleResolver.IsActiveAt(rule, Sunday.AddHours(8)));
    }

    [Fact]
    public void SlotEndIsExclusive()
    {
        // At 11:00 sharp the slot is over. Getting this backwards would leave
        // a restriction lingering for a whole extra minute at every boundary.
        var rule = Rule(480, 660, DayOfWeek.Sunday);

        Assert.False(ScheduleResolver.IsActiveAt(rule, Sunday.AddHours(11)));
    }

    [Fact]
    public void SlotDoesNotApplyOnUncheckedDays()
    {
        var rule = Rule(480, 660, DayOfWeek.Sunday);

        Assert.False(ScheduleResolver.IsActiveAt(rule, Monday.AddHours(9)));
    }

    [Theory]
    [InlineData(0, 0)]        // both ends at midnight, as the UI sends it
    [InlineData(0, 1440)]     // as rules migrated from before slots existed look
    public void ZeroLengthAndFullSpanBothMeanWholeDay(int start, int end)
    {
        var rule = Rule(start, end, DayOfWeek.Sunday);

        Assert.True(ScheduleResolver.IsActiveAt(rule, Sunday.AddMinutes(1)));
        Assert.True(ScheduleResolver.IsActiveAt(rule, Sunday.AddHours(23).AddMinutes(59)));
        Assert.False(ScheduleResolver.IsActiveAt(rule, Monday.AddHours(12)));
    }

    [Fact]
    public void OvernightSlotStaysActiveAfterMidnight()
    {
        // Sunday 22:00-06:00 is still in force on Monday at 02:00: the checked
        // day is the one the slot starts on, not the one the clock says.
        var rule = Rule(1320, 360, DayOfWeek.Sunday);

        Assert.True(ScheduleResolver.IsActiveAt(rule, Sunday.AddHours(23)));
        Assert.True(ScheduleResolver.IsActiveAt(rule, Monday.AddHours(2)));
    }

    [Fact]
    public void OvernightSlotEndsOnTheFollowingMorning()
    {
        var rule = Rule(1320, 360, DayOfWeek.Sunday);

        Assert.False(ScheduleResolver.IsActiveAt(rule, Monday.AddHours(7)));
        Assert.False(ScheduleResolver.IsActiveAt(rule, Sunday.AddHours(12)));
    }

    [Fact]
    public void OvernightSlotDoesNotLeakIntoThePreviousDay()
    {
        // Saturday night belongs to a Saturday rule, not to the Sunday one.
        var rule = Rule(1320, 360, DayOfWeek.Sunday);

        Assert.False(ScheduleResolver.IsActiveAt(rule, Saturday.AddHours(23)));
    }

    [Fact]
    public void RuleWithNoDaysNeverApplies()
    {
        var rule = Rule(480, 660);

        Assert.False(ScheduleResolver.IsActiveAt(rule, Sunday.AddHours(9)));
    }

    [Theory]
    [InlineData(480, 660, 180)]     // 08:00-11:00
    [InlineData(1320, 360, 480)]    // 22:00-06:00 wraps
    [InlineData(0, 0, 1440)]        // whole day
    [InlineData(0, 1440, 1440)]     // whole day, migrated form
    public void DurationIsMeasuredAcrossMidnight(int start, int end, int expected)
    {
        Assert.Equal(expected, ScheduleResolver.DurationMinutes(Rule(start, end, DayOfWeek.Sunday)));
    }

    [Fact]
    public void ShortestOverlappingSlotWins()
    {
        // A specific slot must be able to override a general one without
        // having to carve a hole in the general rule.
        var general = Rule(0, 0, DayOfWeek.Sunday);
        var specific = Rule(480, 660, DayOfWeek.Sunday);

        var active = ScheduleResolver.ActiveRules([general, specific], Sunday.AddHours(9));

        Assert.Single(active);
        Assert.Equal(480, active[0].StartMinutes);
    }

    [Fact]
    public void GeneralSlotAppliesOutsideTheSpecificOne()
    {
        var general = Rule(0, 0, DayOfWeek.Sunday);
        var specific = Rule(480, 660, DayOfWeek.Sunday);

        var active = ScheduleResolver.ActiveRules([general, specific], Sunday.AddHours(15));

        Assert.Single(active);
        Assert.Equal(0, active[0].StartMinutes);
    }

    [Fact]
    public void EachUserGetsAtMostOneRule()
    {
        var a1 = Rule(0, 0, UserA, DayOfWeek.Sunday);
        var a2 = Rule(480, 660, UserA, DayOfWeek.Sunday);
        var b1 = Rule(0, 0, UserB, DayOfWeek.Sunday);

        var active = ScheduleResolver.ActiveRules([a1, a2, b1], Sunday.AddHours(9));

        Assert.Equal(2, active.Count);
        Assert.Single(active, r => r.UserId == UserA);
        Assert.Single(active, r => r.UserId == UserB);
    }

    [Fact]
    public void EqualLengthOverlapsResolveByDeclarationOrder()
    {
        // Deterministic rather than correct-by-intent: with no way to tell the
        // two apart, the result must at least not depend on enumeration order.
        var first = Rule(480, 660, DayOfWeek.Sunday);
        var second = Rule(500, 680, DayOfWeek.Sunday);

        var active = ScheduleResolver.ActiveRules([first, second], Sunday.AddHours(9));

        Assert.Single(active);
        Assert.Equal(480, active[0].StartMinutes);
    }

    [Fact]
    public void NextBoundaryIsTheEndOfTheSlotInForce()
    {
        var rules = new[] { Rule(0, 0, DayOfWeek.Sunday), Rule(480, 660, DayOfWeek.Sunday) };

        Assert.Equal(Sunday.AddHours(11), ScheduleResolver.NextBoundary(rules, Sunday.AddHours(9)));
    }

    [Fact]
    public void NextBoundaryIsTheStartOfAnUpcomingSlot()
    {
        var rules = new[] { Rule(480, 660, DayOfWeek.Sunday) };

        Assert.Equal(Sunday.AddHours(8), ScheduleResolver.NextBoundary(rules, Sunday.AddHours(7)));
    }

    [Fact]
    public void NextBoundaryRollsOverToMidnightWhenTodayIsDone()
    {
        // Midnight always counts as a boundary even if no rule ends there:
        // it's when the day of the week changes.
        var rules = new[] { Rule(480, 660, DayOfWeek.Sunday) };

        Assert.Equal(Monday, ScheduleResolver.NextBoundary(rules, Sunday.AddHours(15)));
    }

    [Fact]
    public void NextBoundaryIsAlwaysInTheFuture()
    {
        // Returning "now" would spin the watcher in a tight loop.
        var rules = new[] { Rule(480, 660, DayOfWeek.Sunday) };
        var now = Sunday.AddHours(8);

        Assert.True(ScheduleResolver.NextBoundary(rules, now) > now);
    }

    [Fact]
    public void NextBoundaryWithNoRulesIsMidnight()
    {
        Assert.Equal(Monday, ScheduleResolver.NextBoundary([], Sunday.AddHours(15)));
    }
}
