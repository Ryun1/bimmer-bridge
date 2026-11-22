# INPA screens on this install

*Recorded on the VM of a 2005 E61 550i (a BimmerGeeks INPA pack). Other installs have other
INPA versions and scripts, so their screens and keys differ; read them from a fresh screenshot.*

**Status: one screen recorded, the loader menu (desk, 2026-11-21).** No module screen has been
seen through the bridge yet.

This file is built only from real screenshots of INPA on that VM (`vm_screenshot`), with the
date each was seen. Nothing from memory, forums or the web goes in: INPA's screens and function
keys come from scripts that differ by version and by car, so a remembered key can be wrong
here and the wrong key can clear or activate something. Until a screen is recorded, read the
labels from a fresh screenshot.

## Entry format

### <Screen title as shown>
- **How the user got here:** the keys or menu items pressed, from the previous screen.
- **What it shows:** values and their units, in plain English (with the German label).
- **Keys on screen:** each function key or menu label as shown, what it does, and whether it
  reads, or writes / activates (flag those).
- **Seen:** date, INPA version, screenshot reference.

## Screens

### INPA - Loader:  BimmerGeeks.Net Version 10/10/2017
- **How the user got here:** INPA started (`INPALOAD`); this is the first screen it shows.
- **What it shows:** "Cable Detected" (partly hidden behind a round indicator) with "Yes", and
  "Ignition : off" with an empty circle. The cable was in the VM and not in the car.
- **Keys on screen,** as labelled (what each opens was not recorded; no label on this screen
  mentions clearing, coding or activating):
  - `F1` Information; `F2` 1 Series (E82, E87, E88); `F3` 3 Series (E90, E91, E92, E93);
    `F4` 5/6 Series (E60, E61, E63, E64); `F5` 7 Series (E65, E66, E67); `F6` X5/X6 (E70,
    E71, E72); `F7` X3 (E83); `F8` Z4 (E85, E86); `F9` Z4 (E89); `F10` End.
  - `Shift`+`F1` Change Editor; `Shift`+`F2` F01 (F02); `Shift`+`F3` F07 (F010/F011);
    `Shift`+`F5` F025; `Shift`+`F7` E46; `Shift`+`F8` E53 / X5; `Shift`+`F9` Old modells;
    `Shift`+`F10` Exit.
  - The button bar at the bottom repeats `F1` to `F10` as Info, E87, E90, E60, E65, E70, E83,
    E85, E89, End. A green "RUNNING" field is at its left.
- **For the E61:** `F4` (5/6 Series, which lists E61). What the next screen shows has not been
  recorded.
- **Seen:** 2026-11-21, at the desk, window 1016 x 833 captured with `printwindow`; fixture
  `dev/fixtures/inpa-open/` in the repo (eval 07 uses it).
