# Look v3.1 type walk (WP9)

One line per screen or component: which text moved to which type job (`--tl-type-<job>-size`, with the job's weight where the rule already set one). Parts append their own section.

## Part 1: Settings, profiles and navigation

Profiles (`Components/Profiles/**`): already right, no font sizes of its own.

Navigation
- PhoneNavigationMenu: changed: title to heading.
- PhoneSectionChips: changed: chip text to control.
- PwaInstallPrompt: changed: copy line to caption.
- SystemActivityIndicator: changed: count to overline (bold 12 px badge); centre glyph left (icon size).
- TopNavAccountMenu: changed: trigger name to control, panel base and external link to menu, header name and secure title to subheading, review count to overline, secure body to body; item and sign-out rows already right (they read the menu-row tokens, which a guardrail test pins); avatar initial, chevron and sign-out icon left (icon sizes).

Settings, top level
- ActivityLogPanel: changed: time, detail and pager summary to secondary.
- AdministratorUnlockDialog: changed: hint to secondary.
- ApprovalDialog: changed: title to subheading, headings and lists to body.
- BackupRecoveryPanel: changed: fact numbers to heading, mobile note to secondary.
- ConvertFolderDialog: changed: h4 to subheading, reasons to body, sample code to secondary.
- DevicesTab: changed: filter label to label.
- DocumentTextSettings: changed: paragraphs and queue to secondary, language name to body, queue code to caption.
- EncodeSettingsTab: changed: current value to label, note to caption.
- ExcludedFilesSection: changed: title to subheading, hint to secondary, reason to caption (was 0.75rem).
- FolderJobStatusLine: changed: line to secondary.
- HouseholdsTab: changed: error to secondary.
- IngestionFacetIndicators: already right apart from icon sizes (left).
- IngestionMediaCard: changed: row title to control, small and step and status text to caption, format line to label.
- LibrariesTab: changed: example line and mobile note to secondary, save bar to body.
- LibraryWriteSchedulePanel: changed: message and failed list to body.
- LocalAiRuntimePanel: changed: diagnostics to body.
- LogExportPanel: changed: hint to secondary.
- LogRetentionPanel: changed: usage to body.
- ManagedAccessApplications: changed: labels, notes, meta and failure to secondary, pill to control.
- ManagedAccessUsers: changed: table header to overline, phone note to secondary.
- MergeFolderDialog: changed: h4 to subheading, tag to caption, rules and reasons to body, sample code to secondary.
- ModelsTab: changed: meta to secondary.
- NeedsReviewPageContent: changed: small text to caption, why to label, tried to secondary.
- NetworkRemoteAccessSettings: changed: panel heading and status value to subheading, supporting copy and alert to secondary, status labels and notes and fact labels to caption, fact values to control, danger and advanced rows to label with caption sub-lines.
- NetworkTestDialog: changed: check title to label, detail to caption.
- NotificationsPanel: changed: badge and matrix head to caption (were 0.8em and 0.85em).
- NotificationsPanel, PairedDevicesSection, SignedInSessionsSection: changed: small meta and tag to caption.
- PlaybackToolsStatusCard: changed: fact label to label, fact number to heading.
- PluginSettingsTab: changed: section heading to heading, counts and private note to secondary, warning pill to control.
- ProfileDetailPage: changed: name to page-title (hero on wide), role to heading, phone note to secondary.
- ProfilesTab: changed: sign-in line and phone note to secondary; wide-screen row line left (see unclear).
- RecycleBinSection: changed: names to body, note and soon and request to secondary, filter to control.
- ScheduledTasksCard: changed: what and values to body, status to control, history to secondary, fact label to label.
- SecuritySettingsPage: changed: section title to subheading, hint and note to secondary.
- SettingsAdvancedLinkRow: changed: action to control.
- StreamingLimitsPanel: changed: heading to subheading, copy to secondary, labels to caption, values to control.
- SystemSummaryPage: changed: fact label to label, note to secondary.
- TechnicalLogPanel: changed: list to secondary, details to caption.
- ViewKindAccessSettings: changed: empty line to secondary.
- Access/HouseholdKidSettings, Access/HouseholdLibraryCeiling: changed: saved and error lines to secondary, area title to label.
- Activity/ActivityPage: changed: field labels and list head to overline, count to overline, date word to secondary, section heading to subheading.
- Activity/ActivityRow: changed: person and song to body, attempts to secondary.
- Frame/SettingsPageFrame: changed: page title to page-title, breadcrumbs to label, subtitle to body (breadcrumb icon left).
- General/GeneralNameAddressPanel: changed: fact label to caption. General/GeneralPage: changed: heading to heading.
- Overview/OverviewAccountsPage: changed: user code to heading. OverviewContinueEnjoying: changed: kind to label, subtitle and percent to secondary. OverviewHeatmap: changed: day and legend text to caption. OverviewPage: changed: name to page-title. OverviewStatStrip: changed: values to page-title, label to label, note to secondary. OverviewTastePage: changed: sentence to page-title (heading on narrow), waiting sentence to body, block and group titles to subheading, scope label to label, basis and chip note to secondary. OverviewTimeline: changed: heading to subheading, time and detail and bar to secondary, done to control. RecentActivityTile: changed: caption to secondary, line to body. TasteShifts: changed: month and legend to secondary.
- Plugins/PluginConnectDialog: changed: status to secondary. PluginFileDiffView, PluginFileEditorPanel (style and css), PluginFileViewer: changed: monospace file text to body. PluginFileEditorPanel locked line: changed: secondary. PluginProvidesLine: changed: line to secondary, sentence to caption. PluginSettingsCard: changed: group h4 to subheading. PluginSetupPage: changed: connection small text to secondary, "more" summary to control.
- Tool tabs (ProviderTesterToolTab, EnrichmentTesterToolTab): already right; they only use the shared `tl-u-font-size-*` helper classes, which `app.css` already maps to tokens.

Left on purpose
- Icon and avatar sizes: `system-activity-indicator__center-icon` glyph, `top-nav-account-menu__avatar` initial, `__chevron` and sign-out `.tl-icon-root`, `settings-breadcrumbs .tl-icon-root`, `ingestion-facet > .tl-icon-root`, `ingestion-facet__spinner`, `ingestion-facet.is-none` glyph, and the `.tl-icon-root` sizes in IngestionMediaCard and NetworkRemoteAccessSettings.
- Unclear: `ProfilesTab.razor.css:24` (at 1600 px and wider the row line grows to the old large step, 20 px; the base size is inherited, so the job depends on what the line shows).
