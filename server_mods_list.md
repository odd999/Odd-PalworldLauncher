# Server Mods List

This file tracks the mods planned, approved, or installed on the Palworld server. It includes information on installation requirements (Client, Server, or Both), dependencies, and compatibility notes.

| # | Mod Name | Nexus Link | Side | Dependencies | Description & Compatibility Notes |
|---|----------|------------|------|--------------|-----------------------------------|
| 1 | **Less Restrictive Building** | [Nexus #98](https://www.nexusmods.com/palworld/mods/98) | **Both** (Client & Server) | UE4SS | Removes building restrictions (inclination, height, floating foundations). Must be installed on both client and server to prevent desyncs and building placement errors. |
| 2 | **Pal Analyzer** | [Nexus #336](https://www.nexusmods.com/palworld/mods/336) | **Client-Only** | None (if .pak) / UE4SS (if Script) | Allows viewing stats, IVs, and passive skills of wild Pals in 3D tooltips. Does **not** need to be installed on the server. Available as either a `.pak` (LogicMod) or UE4SS script. |
| 3 | **Better Night Light** | [Nexus #550](https://www.nexusmods.com/palworld/mods/550) | **Client-Only** | None | Visual/aesthetic mod that improves lighting during the night. Installed client-side only. Does **not** need to be installed on the server. |

---

## Master Load Order Reference (mods.txt)

This list defines the required loading order for the mods. If any subset of these mods is added, their relative order in the `mods.txt` file **must** match the order below:

1. `PalBoxReorganizedX : 1`
2. `DeadBodiesDespawnInstantlyX : 1`
3. `MoreStatPointsX : 1`
4. `MoreTechnologyPointsX : 1`
5. `MaxLevelIncreasedX : 1`
6. `BetterShieldUltimateX : 1`
7. `BetterRidingGroundMountsX : 1`
8. `BetterSellRatesX : 1`
9. `NoMoreBurningBasesX : 1`
10. `NoDamagedEquipmentPenaltiesX : 1`
11. `AutoSaveTimeX : 1`
12. `LessRestrictiveBuilding : 1`
13. `LessMapShroudX : 1`
14. `MoreMarkersX : 1`
15. `HUDFadeOutNever : 1`
16. `NoMoreWildPalsOnYourBase : 1`
17. `ConfigurableExpGainX : 1`
18. `NoMoreHoldButtonX : 1`

*Note: In UE4SS, mods are loaded sequentially from top to bottom. Preserving this relative order avoids load-order conflicts. If you ever add any mod from this list, make sure to keep this order.*

---

## Mod Details & Installation Guide

### 1. Less Restrictive Building (Mod ID: 98)
* **Type:** UE4SS Lua Mod
* **Required on:** Both Client and Server
* **Installation Path:** 
  * Extract to: `Pal/Binaries/Win64/Mods/LessRestrictiveBuilding`
  * Add line to `Pal/Binaries/Win64/Mods/mods.txt`: `LessRestrictiveBuilding : 1`
* **Dependencies:** UE4SS (Unreal Engine 4/5 Scripting System)
* **Potential Conflicts / Troubleshooting:**
  * **Version Mismatch:** Every client connecting must have the same mod version as the server, otherwise building desyncs or crashes may occur.
  * **UE4SS Crash:** If the game crashes on startup, check the server/client `UE4SS-settings.ini` and set `bUseUObjectArrayCache` to `false`.
  * **Palworld Game Updates:** Updates to Palworld often break UE4SS. When Palworld updates, UE4SS and the mod may need to be updated.

### 2. Pal Analyzer (Mod ID: 336)
* **Type:** Client-Only (Do not install on the server)
* **Installation Options:**
  * **Option A: .pak version (LogicMod) [Recommended if you have this file]**
    * **Path:** Place the `.pak` file in `Pal/Content/Paks/LogicMods/` (create the `LogicMods` folder if it doesn't exist).
    * **Configuration:** **No entry needed** in `mods.txt`. The game automatically loads all `.pak` files in the `LogicMods` folder.
    * **Dependencies:** None.
  * **Option B: Script version (UE4SS)**
    * **Path:** Extract to `Pal/Binaries/Win64/Mods/PalAnalyzer`.
    * **Configuration:** Add `PalAnalyzer : 1` to `Pal/Binaries/Win64/Mods/mods.txt`.
    * **Dependencies:** UE4SS.
* **Usage:** Hold **Left Alt** key (keyboard) or **Right Bumper** (gamepad) to view stats. Press **Shift + O** for settings.

### 3. Better Night Light (Mod ID: 550)
* **Type:** Visual Mod (.pak file)
* **Required on:** Client-Only (Do not install on the server)
* **Installation Path:** 
  * Place the `.pak` file in `Pal/Content/Paks/~mods/` (create the `~mods` folder if it does not exist).
* **Configuration:** **No entry needed** in `mods.txt`.
* **Dependencies:** None.
