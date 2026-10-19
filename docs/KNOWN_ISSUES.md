# Known issues and resolved regressions

Version 6.4.2 changes startup, search, and image loading. The following separates
the fixed migration regression from remaining inherited findings.

## Fixed in 6.4.2: partial legacy migration lost new gate records

When `gates.json` already exists but a legacy unit has no `gateId`,
`MigrateGateHierarchyIfNeeded` creates a new gate in memory, assigns its ID to the
unit, and saves `systems.json`. It never saves the modified `gates.json`. The unit
then references a gate that does not exist on disk and cannot be browsed normally.

Source: `SafetyGateSupport/Services/DataService.cs`, existing-gates branch of
`MigrateGateHierarchyIfNeeded` (lines 114–129).

The fix persists any new gate records before saving their unit references. The
regression now runs in every standard validation and passes. First-run migration
and repeat initialization are checked separately.

## Source-review findings requiring a separate fix

- **Read errors look like empty data.** `DataService.LoadList<T>` catches read and
  deserialization exceptions and returns an empty list. Later saves can replace
  existing content after a transient read error or corrupt JSON. This was found
  by source inspection, not a simulated shared-drive failure.
- **Concurrent JSON writers share a temporary file.** `SaveJsonWithBackup` always
  uses `path + ".tmp"`. Simultaneous writers can collide; the application also
  has no merge/conflict handling for shared index updates. Concurrency was not
  exercised against a Windows share.
- **Deletion can broaden content assignments.** Removing a gate/unit deletes its
  manual, part, and flow assignments. If the final assignment disappears, the
  models interpret empty assignment lists as globally applicable (or apply the
  flow's manufacturer filter). The deletion paths are in `AdminWindow.xaml.cs`;
  applicability rules are in the corresponding models.
- **Troubleshooting resource links differ from normal browsing.** The
  troubleshooting window opens the first matching manual directly and sends a
  Parts resource to the storeroom URL. It does not offer the normal manual or
  filtered-part selection. See `TroubleshootWindow.Resource_Click`.

None of these findings blocks compiling or packaging the application. The remaining source-review findings and plant-specific integration behavior
are outside the performance changes in this version.
