package com.tuvima.library.core

// The phone shell as the Dashboard draws it on a phone (docs: "Phone navigation"): a top bar, the section chips, a floating dock of three
// tabs and a menu. The destinations, their order and their words are the Dashboard's (`ShellNavigation`, `ShellDock`); the Kids shell keeps
// only the lanes a child needs. Pure rules; nothing here draws anything.

/** The sections under the top bar, in the Dashboard top bar's order (Read, Watch, Listen, View, Collections). */
enum class PhoneSection(val label: String, val lane: String) {
    Read("Read", "read"),
    Watch("Watch", "watch"),
    Listen("Listen", "listen"),
    View("View", "view"),
    Collections("Collections", "collections"),
}

/** The three tabs of the floating dock, the same for adults and Kids. */
enum class PhoneDockTab(val label: String) {
    Home("Home"),
    Search("Search"),
    MyList("My List"),
}

/** Where the menu can go. */
enum class PhoneMenuTarget { Home, MyList, RecentlyAdded, Section, Settings, SwitchProfile }

/** One menu row. [section] is set for a lane row. [afterDivider] starts the group below the rule (Settings, Switch profile). */
data class PhoneMenuItem(
    val target: PhoneMenuTarget,
    val label: String,
    val section: PhoneSection? = null,
    val afterDivider: Boolean = false,
)

/** What a top-bar icon is. The row's order is fixed: hamburger, logo, then Downloads, My List and the avatar on the right. */
enum class PhoneTopBarAction(val label: String) {
    Menu("Menu"),
    Downloads("Downloads"),
    MyList("My List"),
    Account("Account"),
}

object PhoneShellLogic {
    const val MENU_TITLE = "Menu"
    const val SECTIONS_LABEL = "Sections"

    /** The lanes a Kids profile keeps, in the Dashboard's Kids order. */
    private val kidsSections = listOf(PhoneSection.Watch, PhoneSection.Listen, PhoneSection.Read)

    /** The chips row: the five lanes for an adult; Watch, Listen and Read for a Kids profile. */
    fun sections(isKids: Boolean): List<PhoneSection> = if (isKids) kidsSections else PhoneSection.entries.toList()

    /** The dock: Home, Search, My List. */
    fun dockTabs(): List<PhoneDockTab> = PhoneDockTab.entries.toList()

    /** The top bar's icons from the left: the menu, then (right side) Downloads, My List and the avatar. */
    fun topBarActions(): List<PhoneTopBarAction> = PhoneTopBarAction.entries.toList()

    /**
     * The menu: Home, My List, Recently added (adults), then the lanes; below the rule, Settings (not for Kids) and Switch profile (only
     * when the household has more than one profile). Sign out stays in Settings.
     */
    fun menuItems(isKids: Boolean, profileCount: Int): List<PhoneMenuItem> = buildList {
        add(PhoneMenuItem(PhoneMenuTarget.Home, "Home"))
        add(PhoneMenuItem(PhoneMenuTarget.MyList, "My List"))
        if (!isKids) add(PhoneMenuItem(PhoneMenuTarget.RecentlyAdded, "Recently added"))
        sections(isKids).forEach { add(PhoneMenuItem(PhoneMenuTarget.Section, it.label, section = it)) }
        var firstBelow = true
        if (!isKids) {
            add(PhoneMenuItem(PhoneMenuTarget.Settings, "Settings", afterDivider = firstBelow))
            firstBelow = false
        }
        if (profileCount > 1) add(PhoneMenuItem(PhoneMenuTarget.SwitchProfile, "Switch profile", afterDivider = firstBelow))
    }

    /** The one letter on the avatar: the profile's first letter, or a dot while the name is not known. */
    fun avatarInitial(displayName: String?): String =
        displayName?.trim()?.firstOrNull { it.isLetterOrDigit() }?.uppercaseChar()?.toString() ?: "•"
}
