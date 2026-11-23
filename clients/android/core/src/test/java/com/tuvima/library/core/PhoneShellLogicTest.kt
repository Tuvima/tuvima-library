package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class PhoneShellLogicTest {
    @Test
    fun theChipsAreTheDashboardsFiveLanesInTheDashboardsOrder() {
        assertEquals(
            listOf("Read", "Watch", "Listen", "View", "Collections"),
            PhoneShellLogic.sections(isKids = false).map { it.label },
        )
    }

    @Test
    fun aKidsProfileKeepsOnlyTheLanesAChildNeeds() {
        assertEquals(listOf("Watch", "Listen", "Read"), PhoneShellLogic.sections(isKids = true).map { it.label })
    }

    @Test
    fun theDockHasThreeTabsHomeSearchAndMyList() {
        assertEquals(listOf("Home", "Search", "My List"), PhoneShellLogic.dockTabs().map { it.label })
    }

    @Test
    fun theTopBarHoldsTheMenuThenDownloadsMyListAndTheAvatar() {
        assertEquals(
            listOf("Menu", "Downloads", "My List", "Account"),
            PhoneShellLogic.topBarActions().map { it.label },
        )
    }

    @Test
    fun theMenuListsHomeMyListRecentlyAddedTheLanesThenSettingsSwitchProfileAndSignOut() {
        val items = PhoneShellLogic.menuItems(isKids = false, profileCount = 3)

        assertEquals(
            listOf(
                "Home", "My List", "Recently added", "Read", "Watch", "Listen", "View", "Collections",
                "Settings", "Switch profile", "Sign out",
            ),
            items.map { it.label },
        )
        assertEquals(listOf("Settings"), items.filter { it.afterDivider }.map { it.label })
        assertEquals(PhoneSection.Read, items.first { it.label == "Read" }.section)
    }

    @Test
    fun switchProfileIsOnlyOfferedWhenTheHouseholdHasMoreThanOneProfile() {
        assertFalse(PhoneShellLogic.menuItems(isKids = false, profileCount = 1).any { it.target == PhoneMenuTarget.SwitchProfile })
        assertTrue(PhoneShellLogic.menuItems(isKids = false, profileCount = 2).any { it.target == PhoneMenuTarget.SwitchProfile })
        assertEquals(
            listOf("Settings", "Sign out"),
            PhoneShellLogic.menuItems(isKids = false, profileCount = 1).takeLast(2).map { it.label },
        )
    }

    @Test
    fun aKidsMenuHasNoRecentlyAddedAndNoSettingsAndEndsWithSignOut() {
        val items = PhoneShellLogic.menuItems(isKids = true, profileCount = 1)

        assertEquals(listOf("Home", "My List", "Watch", "Listen", "Read", "Sign out"), items.map { it.label })
        // With no Settings row, Sign out starts the group below the rule.
        assertTrue(items.last().afterDivider)
    }

    @Test
    fun aKidsProfileStillMayChangePersonWhenTheHouseholdHasMoreThanOne() {
        val items = PhoneShellLogic.menuItems(isKids = true, profileCount = 2)

        assertEquals(listOf("Switch profile", "Sign out"), items.takeLast(2).map { it.label })
        assertEquals(listOf("Switch profile"), items.filter { it.afterDivider }.map { it.label })
        assertFalse(items.any { it.target == PhoneMenuTarget.Settings })
    }

    @Test
    fun theShellFailsClosedToTheKidsShellUntilTheProfileIsKnownToBeAnAdult() {
        assertTrue(PhoneShellLogic.treatAsKids(true))
        assertTrue(PhoneShellLogic.treatAsKids(null))
        assertFalse(PhoneShellLogic.treatAsKids(false))
    }

    @Test
    fun theAvatarShowsTheFirstLetterOrADotWhileTheNameIsUnknown() {
        assertEquals("M", PhoneShellLogic.avatarInitial("mary"))
        assertEquals("J", PhoneShellLogic.avatarInitial("  Jim"))
        assertEquals("•", PhoneShellLogic.avatarInitial(null))
        assertEquals("•", PhoneShellLogic.avatarInitial("   "))
    }
}
