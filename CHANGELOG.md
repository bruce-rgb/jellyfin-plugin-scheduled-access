# Changelog

Notes for each release. The section for a version is published in two places
by the release workflow: the **version history in Jellyfin's plugin page**, and
the **GitHub release notes**. Write it for the person installing the plugin,
not for the person who wrote the code.

Headings must stay at `## <version>` with the four-part version exactly as
tagged, because the workflow extracts the section by matching that line.

## 1.2.0.0

### Added
- Time slots. A rule now applies within a start and end time, not just for
  whole days — educational content in the morning, cartoons before dinner.
  Slots may run past midnight, in which case the checked day is the one the
  slot starts on.
- Library restrictions. A rule can limit which libraries are visible while
  it is active, on top of the tag filter. Both combine, so you can restrict
  to one library and still filter by tags inside it.

### Changed
- Slot switching is driven by a background watcher that wakes exactly at each
  boundary, replacing the hourly scheduled task. Saving the configuration
  applies it immediately.
- The scheduled task is now a manual button and a daily fallback rather than
  the thing that switches slots, and no longer floods the task history.
- When several rules overlap for the same user, the shortest slot wins.

### Notes
- Rules created with earlier versions keep applying all day. Nothing to
  migrate and nothing to re-enter.
- Snapshots taken before this version restore tags only, not libraries.
  Applying their defaults would strip a user's access to every library, so
  the plugin deliberately leaves libraries untouched for those.
- Restrictions apply to administrator accounts too. Take care not to lock
  yourself out of content you need.

## 1.1.0.0

### Added
- The configuration page is localised in English and Spanish, following the
  language chosen in Jellyfin and falling back to the browser's.
- Saving the configuration now applies the rules within seconds instead of
  waiting for the next scheduled run.

### Fixed
- Deleting a rule left the user restricted forever. Restoration is now driven
  by the saved snapshots rather than by the rules, so removing a rule undoes
  its restriction.
- Saving the configuration page could destroy the record of a user's original
  policy, after which "restoring" returned them to the restricted state. The
  server no longer accepts that state from the browser.
- Uninstalling or updating from the dashboard failed with a 404 when the
  plugin's manifest version did not match the assembly version.

### Changed
- The scheduled task and log messages are in English. A task name is a single
  server-wide string in Jellyfin, so it cannot be localised per user.

## 1.0.0.0

### Added
- First release. Restrict what a user can see based on the day of the week,
  using library tags.
- Two filter modes: hide content carrying the listed tags, or show only
  content carrying them.
- The original policy of every affected user is saved before the first
  restriction and restored when no rule applies, so restrictions are always
  reversible — even if the server is shut down while one is in force.
