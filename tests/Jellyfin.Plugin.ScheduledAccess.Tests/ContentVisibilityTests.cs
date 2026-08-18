using System;
using Jellyfin.Plugin.ScheduledAccess.Configuration;
using Jellyfin.Plugin.ScheduledAccess.Scheduling;
using Xunit;

namespace Jellyfin.Plugin.ScheduledAccess.Tests;

/// <summary>
/// Tests for the per-item check behind stopping playback.
/// </summary>
/// <remarks>
/// This decides whether a stream gets cut off mid-film, so the cost of an
/// error is high in both directions: cutting something that was allowed, or
/// letting a restricted item run to the end. It also has to agree with what
/// <c>ScheduleEnforcer</c> writes into the user policy, which is why the
/// cases below are phrased as "the rule said X, so the item is/isn't visible".
/// </remarks>
public class ContentVisibilityTests
{
    private static readonly Guid Kids = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid Movies = Guid.Parse("22222222-0000-0000-0000-000000000002");

    private static ScheduleRule Rule(TagFilterMode mode, string[] tags, params Guid[] libraries)
        => new()
        {
            Mode = mode,
            Tags = tags,
            LibraryIds = libraries
        };

    [Fact]
    public void NoRuleAllowsEverything()
    {
        // No active rule means the plugin is not restricting this user, so
        // there is nothing for it to interrupt.
        Assert.True(ContentVisibility.IsAllowed(["cartoon"], Kids, null));
    }

    [Fact]
    public void BlockModeHidesItemsCarryingTheTag()
    {
        var rule = Rule(TagFilterMode.Block, ["cartoon"]);

        Assert.False(ContentVisibility.IsAllowed(["cartoon", "kids"], Kids, rule));
    }

    [Fact]
    public void BlockModeAllowsItemsWithoutTheTag()
    {
        var rule = Rule(TagFilterMode.Block, ["cartoon"]);

        Assert.True(ContentVisibility.IsAllowed(["documentary"], Kids, rule));
    }

    [Fact]
    public void AllowOnlyModeRequiresOneOfTheTags()
    {
        var rule = Rule(TagFilterMode.AllowOnly, ["educational"]);

        Assert.True(ContentVisibility.IsAllowed(["educational"], Kids, rule));
        Assert.False(ContentVisibility.IsAllowed(["cartoon"], Kids, rule));
    }

    [Fact]
    public void AllowOnlyModeHidesUntaggedItems()
    {
        // The strict allowlist fails closed: this is what makes "show only
        // educational content" mean something for a library nobody finished
        // tagging.
        var rule = Rule(TagFilterMode.AllowOnly, ["educational"]);

        Assert.False(ContentVisibility.IsAllowed([], Kids, rule));
        Assert.False(ContentVisibility.IsAllowed(null, Kids, rule));
    }

    [Fact]
    public void TagsAreMatchedIgnoringCase()
    {
        // Jellyfin compares tags case-insensitively, and the enforcer merges
        // them that way too. Diverging here would cut off content that stayed
        // perfectly visible in the library.
        var rule = Rule(TagFilterMode.Block, ["Cartoon"]);

        Assert.False(ContentVisibility.IsAllowed(["cartoon"], Kids, rule));
    }

    [Fact]
    public void AnEmptyTagListAppliesNoTagFilter()
    {
        // An empty allowlist is not "hide everything": that is how Jellyfin
        // reads an empty AllowedTags, and the enforcer writes exactly that.
        var allowOnly = Rule(TagFilterMode.AllowOnly, []);
        var block = Rule(TagFilterMode.Block, []);

        Assert.True(ContentVisibility.IsAllowed(["anything"], Kids, allowOnly));
        Assert.True(ContentVisibility.IsAllowed(["anything"], Kids, block));
    }

    [Fact]
    public void LibraryRestrictionHidesItemsFromOtherLibraries()
    {
        var rule = Rule(TagFilterMode.Block, [], Kids);

        Assert.True(ContentVisibility.IsAllowed(["x"], Kids, rule));
        Assert.False(ContentVisibility.IsAllowed(["x"], Movies, rule));
    }

    [Fact]
    public void LibraryAndTagFiltersCombine()
    {
        // Both restrictions apply at once: the right library is not enough if
        // the tag filter rejects the item.
        var rule = Rule(TagFilterMode.AllowOnly, ["educational"], Kids);

        Assert.True(ContentVisibility.IsAllowed(["educational"], Kids, rule));
        Assert.False(ContentVisibility.IsAllowed(["cartoon"], Kids, rule));
        Assert.False(ContentVisibility.IsAllowed(["educational"], Movies, rule));
    }

    [Fact]
    public void AnUnknownLibraryDoesNotTriggerTheLibraryFilter()
    {
        // Falling open on purpose. If the library cannot be resolved the check
        // is a guess, and guessing wrong here cuts off a film someone is
        // watching. The tag filter still applies.
        var rule = Rule(TagFilterMode.Block, ["cartoon"], Kids);

        Assert.True(ContentVisibility.IsAllowed(["documentary"], Guid.Empty, rule));
        Assert.False(ContentVisibility.IsAllowed(["cartoon"], Guid.Empty, rule));
    }

    [Fact]
    public void NoLibrariesInTheRuleLeavesLibraryAccessAlone()
    {
        // An empty list means "do not touch libraries", matching what the
        // enforcer does with the policy.
        var rule = Rule(TagFilterMode.Block, ["cartoon"]);

        Assert.True(ContentVisibility.IsAllowed(["documentary"], Movies, rule));
    }
}
