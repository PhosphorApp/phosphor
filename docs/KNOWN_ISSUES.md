# Known Issues

Tracked, intentionally-deferred issues. Each entry notes the tradeoff and where the
relevant code lives so it's easy to pick up later.

## Settings ComboBox — keyboard bring-into-view suppressed

**Where:** `Phosphor/Windows/SettingsWindow.xaml.cs` — static constructor class handlers
for `ComboBox` / `ComboBoxItem` `RequestBringIntoView`.

**Context:** Changing a `ComboBox` selection inside a `ScrollViewer` used to jump the
settings panel scroll position (usually to the top). This was fixed by registering class
handlers that mark `RequestBringIntoView` as handled for both `ComboBox` and
`ComboBoxItem`, covering every combo including dynamically-built plug-in dropdowns
(e.g. Vimeo/DailyMotion video quality).

**Tradeoff / known issue:** Suppressing `RequestBringIntoView` on the `ComboBox` type
also disables the *legitimate* behavior of auto-scrolling an off-screen combo into view
when it receives focus via keyboard (Tab). The Settings window is primarily mouse-driven
(cabinet keyboard shortcuts target playback, not settings), so this is an acceptable
trade for now.

**Possible future fix:** Make the handler selective instead of blanket-suppressing —
e.g. only mark handled when the requested rectangle corresponds to the combo/item that is
already visible (a selection-driven scroll), and let through genuine focus-driven
bring-into-view when the target is off-screen. Would restore keyboard navigation without
reintroducing the selection jump.
