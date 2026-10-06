# P4 â€” Structural components and shell

AppTabs/AppTabPanel supply selected panels, controlled index, keep-alive behavior,
roving focus and Arrow/Home/End navigation. AppExpansionPanels/AppExpansionPanel
retain controlled expanded state and single/multiple-open policy. Native tables
and lists retain the used content templates, dense/hover/striped behavior and
empty-state semantics. The used chart is a native accessible donut, with ordered
values, labels and owned/missing counts; it does not introduce a chart dependency.

AppLayout, AppAppBar, AppDrawer, AppDrawerHeader and AppMainContent own the shell.
Temporary drawers use modal focus/dismissal behavior and retain app-bar/dock
offsets. Final browser review found the non-fixed app bar lacked the original
relative positioning and z-index, hiding navigation behind Home's hero. Its
native rule now restores relative flex sizing, theme color/background, z-index
1300 and the original sizing transition. The fixed variant retains top/right/left
anchoring. Original global 8px scrollbar styling was also restored so browser
gutters follow the expected page geometry. These findings were fixed, not ignored.
The gallery also exposed a 43px native tab bar against the original 48px height.
The shared toolbar now retains 48px border-box height, 160px tab minimum widths,
the original root minimum width behavior and 1.5rem glyph sizing. Vertical toolbars
retain automatic height. This correction targets the observed original 93px
gallery tab surface rather than accepting the former 88px native result.

NativeStructureTests cover tabs, disabled-panel navigation, retained panel content,
expansion policies, alerts, avatar parameters, tables and chart counts. Browser
selectors map original wrapped tab buttons to native direct toolbar children.
An observed original trace differed in selected versus focused tab state; native
acceptance requires truthful synchronized selection and focus.

Native tab and expansion keyboard assertions passed. Representative Home captures
include desktop, phone and lower-height viewports; gallery, editor, browse and
detail captures support structural review. Full drawer/table/chart/responsive
matrix coverage is not established. The final serial full suite passed 5,100 tests
with 34 skips and zero failures, including all 1,766 Dashboard tests.
Live player geometry also confirmed three visible utility buttons retain 44px
targets and 22px glyphs at 1920Ã—1080, 390Ã—844 and 390Ã—667. The recorded
`player-icon-rows.json` proves those browser measurements, not physical touch or
complete device/fullscreen behavior.

## Plain-English completion summary

Navigation, tabs, expanding sections, tables and the ownership chart now have
first-party implementations. Browser review caught and corrected the hidden
navigation and scrollbar sizing issues. Representative responsive review supports
the implementation, while complete requested visual acceptance remains limited.
