using PartyPulse.Windows;

namespace PartyPulse.NavigationTests;

internal static class Program
{
    private static readonly MainSubtabDefinition[] DjsSubtabs =
    [
        new(MainSubtab.DjsSettings, "Pricing"),
        new(MainSubtab.DjsDirectory, "Directory"),
    ];

    public static int Main()
    {
        var tests = new Action[]
        {
            TogglePageExpandsAndSelectsFirstSubtab,
            SecondToggleCollapsesWithoutChangingSelection,
            HiddenPreferredSubtabFallsBackAndRestores,
            NavigationStateIsVenueScoped,
            UnknownVisibilityIsHiddenUntilResolved,
            VisibilityIsRetainedWhileCapabilitiesReload,
            VisibilityCacheCanBeClearedForReauthentication,
            AccessLoadRunsOncePerAuthenticationSession,
            RefreshDeferralIsCappedAndResets,
        };

        foreach (var test in tests)
        {
            test();
        }

        Console.WriteLine($"Passed {tests.Length} PartyPulse navigation tests.");
        return 0;
    }

    private static void TogglePageExpandsAndSelectsFirstSubtab()
    {
        var state = new MainWindowNavigationState();
        var profileId = Guid.NewGuid();

        var selected = state.TogglePage(profileId, MainPage.Djs, DjsSubtabs);

        Equal(MainSubtab.DjsSettings, selected, nameof(TogglePageExpandsAndSelectsFirstSubtab));
        True(state.IsExpanded(profileId, MainPage.Djs), nameof(TogglePageExpandsAndSelectsFirstSubtab));
    }

    private static void SecondToggleCollapsesWithoutChangingSelection()
    {
        var state = new MainWindowNavigationState();
        var profileId = Guid.NewGuid();
        state.TogglePage(profileId, MainPage.Djs, DjsSubtabs);
        state.Select(profileId, MainPage.Djs, MainSubtab.DjsDirectory);

        var selected = state.TogglePage(profileId, MainPage.Djs, DjsSubtabs);

        Equal(MainSubtab.DjsDirectory, selected, nameof(SecondToggleCollapsesWithoutChangingSelection));
        False(state.IsExpanded(profileId, MainPage.Djs), nameof(SecondToggleCollapsesWithoutChangingSelection));
    }

    private static void HiddenPreferredSubtabFallsBackAndRestores()
    {
        var state = new MainWindowNavigationState();
        var profileId = Guid.NewGuid();
        state.Select(profileId, MainPage.Djs, MainSubtab.DjsDirectory);

        var fallback = state.Resolve(profileId, MainPage.Djs, [DjsSubtabs[0]]);
        var restored = state.Resolve(profileId, MainPage.Djs, DjsSubtabs);

        Equal(MainSubtab.DjsSettings, fallback.Id, nameof(HiddenPreferredSubtabFallsBackAndRestores));
        Equal(MainSubtab.DjsDirectory, restored.Id, nameof(HiddenPreferredSubtabFallsBackAndRestores));
    }

    private static void NavigationStateIsVenueScoped()
    {
        var state = new MainWindowNavigationState();
        var firstProfile = Guid.NewGuid();
        var secondProfile = Guid.NewGuid();
        state.Select(firstProfile, MainPage.Djs, MainSubtab.DjsDirectory);

        var first = state.Resolve(firstProfile, MainPage.Djs, DjsSubtabs);
        var second = state.Resolve(secondProfile, MainPage.Djs, DjsSubtabs);

        Equal(MainSubtab.DjsDirectory, first.Id, nameof(NavigationStateIsVenueScoped));
        Equal(MainSubtab.DjsSettings, second.Id, nameof(NavigationStateIsVenueScoped));
    }

    private static void VisibilityIsRetainedWhileCapabilitiesReload()
    {
        var state = new SubtabVisibilityState();
        var profileId = Guid.NewGuid();
        var hiddenKey = (profileId, MainSubtab.DjsPayments);
        var visibleKey = (profileId, MainSubtab.DjsDirectory);

        False(state.Resolve(hiddenKey, false), nameof(VisibilityIsRetainedWhileCapabilitiesReload));
        False(state.Resolve(hiddenKey, null), nameof(VisibilityIsRetainedWhileCapabilitiesReload));
        True(state.Resolve(hiddenKey, true), nameof(VisibilityIsRetainedWhileCapabilitiesReload));
        True(state.Resolve(visibleKey, true), nameof(VisibilityIsRetainedWhileCapabilitiesReload));
        True(state.Resolve(visibleKey, null), nameof(VisibilityIsRetainedWhileCapabilitiesReload));
    }

    private static void UnknownVisibilityIsHiddenUntilResolved()
    {
        var state = new SubtabVisibilityState();
        var key = (Guid.NewGuid(), MainSubtab.GiveawaysManage);

        False(state.Resolve(key, null), nameof(UnknownVisibilityIsHiddenUntilResolved));
        True(state.Resolve(key, true), nameof(UnknownVisibilityIsHiddenUntilResolved));
    }

    private static void VisibilityCacheCanBeClearedForReauthentication()
    {
        var state = new SubtabVisibilityState();
        var profileId = Guid.NewGuid();
        var key = (profileId, MainSubtab.DjsDirectory);
        True(state.Resolve(key, true), nameof(VisibilityCacheCanBeClearedForReauthentication));

        state.Clear(profileId);

        False(state.Resolve(key, null), nameof(VisibilityCacheCanBeClearedForReauthentication));
    }

    private static void AccessLoadRunsOncePerAuthenticationSession()
    {
        var state = new NavigationAccessLoadState();
        var profileId = Guid.NewGuid();
        var firstSession = new DateTimeOffset(2026, 8, 31, 11, 0, 0, TimeSpan.Zero);
        var secondSession = firstSession.AddMinutes(5);

        True(state.ShouldStart(profileId, firstSession), nameof(AccessLoadRunsOncePerAuthenticationSession));
        False(state.ShouldStart(profileId, firstSession), nameof(AccessLoadRunsOncePerAuthenticationSession));
        True(state.ShouldStartRemainder(profileId), nameof(AccessLoadRunsOncePerAuthenticationSession));
        False(state.ShouldStartRemainder(profileId), nameof(AccessLoadRunsOncePerAuthenticationSession));
        state.MarkResolved(profileId);
        True(state.IsResolved(profileId), nameof(AccessLoadRunsOncePerAuthenticationSession));
        True(state.ShouldStart(profileId, secondSession), nameof(AccessLoadRunsOncePerAuthenticationSession));
        False(state.HasStartedRemainder(profileId), nameof(AccessLoadRunsOncePerAuthenticationSession));
        True(state.IsResolved(profileId), nameof(AccessLoadRunsOncePerAuthenticationSession));

        state.Reset(profileId);

        False(state.IsResolved(profileId), nameof(AccessLoadRunsOncePerAuthenticationSession));
        True(state.ShouldStart(profileId, secondSession), nameof(AccessLoadRunsOncePerAuthenticationSession));
    }

    private static void RefreshDeferralIsCappedAndResets()
    {
        var state = new RefreshDeferralState();
        var startedAt = new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

        state.Observe(true, startedAt);
        True(state.ShouldDefer(startedAt.AddSeconds(59)), nameof(RefreshDeferralIsCappedAndResets));
        False(state.ShouldDefer(startedAt.AddMinutes(1)), nameof(RefreshDeferralIsCappedAndResets));

        state.Observe(false, startedAt.AddMinutes(2));
        state.Observe(true, startedAt.AddMinutes(3));
        True(state.ShouldDefer(startedAt.AddMinutes(3).AddSeconds(1)), nameof(RefreshDeferralIsCappedAndResets));
    }

    private static void Equal<T>(T expected, T actual, string testName)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{testName}: expected {expected}, received {actual}.");
        }
    }

    private static void True(bool value, string testName)
    {
        if (!value)
        {
            throw new InvalidOperationException($"{testName}: expected true.");
        }
    }

    private static void False(bool value, string testName) => True(!value, testName);
}
