# Odd Palworld Launcher

A professional, feature-rich C# WPF launcher and security integration suite for Palworld servers. Designed to streamline client-side mod synchronization, enforce server security settings, and manage automated player whitelisting.

---

## 🚀 Key Features

* **1-Click Mod Sync & ZIP Packing**: Includes helper scripts (`PackMods.ps1`/`PackMods.bat`) to automate zipping local mod directories directly to Google Drive, automatically purging OS-specific junk files (e.g., `desktop.ini`) beforehand.
* **Google Drive Verification Bypass**: Automatically handles and parses the Google Drive HTML "Virus Scan Warning" page for large files programmatically, extracting authentication tokens to download massive modpacks without interruption.
* **Anti-Cheat & Unapproved Mod Cleanup**: Scans the player's client directory and lists extra `.pak` files, DLLs, or unapproved script folders. Prompts the player (in English & Arabic) to clean up unauthorized changes before allowing the game to launch.
* **Multi-Platform Config Sync (Steam & Gamepass GDK)**: Automatically enforces server rules, such as disabling unauthorized fast-travel cheats, writing properties directly to both Steam and Xbox Gamepass folders simultaneously.
* **Config Locking vs. Keybind Preservation**: Overwrites and locks central server-side configuration files while intelligently ignoring individual custom settings (`.ini`, `.json`, `.cfg`, `.txt`) so players can keep their keybinds intact.
* **Cryptographic Whitelist Gateway**: Handshakes with the server side HTTP Security API prior to launch, preventing players from connecting unless they have validated through the launcher.

---

## 📂 Project Structure

* **[PalworldLauncher/](PalworldLauncher/)**: The core WPF application, written in C# (.NET Framework / .NET Core compatibility).
* **[PalworldLauncher.Tests/](PalworldLauncher.Tests/)**: Unit tests to validate logic blocks, bypass routines, and config parsing.
* **[PackMods.bat](PackMods.bat) / [PackMods.ps1](PackMods.ps1)**: Admin scripts for mod zipping and Google Drive synchronization.
* **[server_security_api.py](server_security_api.py)**: Python watchdog script for the game server, handling RCON communication and kicking players who connect without whitelisting first.

---

## 🛠️ Getting Started

### 1. Requirements
* **Developer**: Visual Studio 2022, .NET SDK, and Python 3.x (for the server-side watchdog).
* **Client**: Windows OS with Palworld installed on Steam or Xbox Gamepass.

### 2. Client Launcher Setup
1. Copy `launcher_config.json.example` to `launcher_config.json`.
2. Edit the configurations:
   ```json
   {
     "PalworldPath": "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Palworld",
     "ManifestUrl": "https://raw.githubusercontent.com/your-username/your-repo/main/manifest.json",
     "ServerIp": "127.0.0.1",
     "ServerPort": 8211,
     "ServerPassword": "YOUR_PASSWORD_HERE",
     "AutoConnect": true
   }
   ```
3. Open `Palworld.sln` in Visual Studio 2022, configure to `Release`, and build the project.

### 3. Server Watchdog Setup
1. Configure `server_security_api.py` with your server's RCON port and AdminPassword:
   ```python
   PORT = 8000
   API_SECRET = "YOUR_SHARED_HMAC_KEY"
   RCON_HOST = "127.0.0.1"
   RCON_PORT = 25575
   RCON_PASSWORD = "RCON_ADMIN_PASSWORD"
   ```
2. Run the server watchdog:
   ```bash
   python server_security_api.py
   ```
   *The watchdog listens on port 8000 for launcher handshakes and periodically kicks anyone online who is not authorized.*

---

## 🛡️ License

This project is created for private server administration. Redistribution or public hosting should align with Pocketpair's terms of service.
