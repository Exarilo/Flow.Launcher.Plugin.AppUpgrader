# Flow Launcher Plugin AppUpgrader

## Description

The `AppUpgrader` plugin for Flow Launcher helps you keep your Windows applications updated. It queries the `winget` command-line tool in the background, resolves paths, fetches application icons, and executes silent upgrades directly from your launcher shell.

---

## Features

* **Lists Upgradable Applications:** Instantly view all packages on your system that have pending upgrades available.
* **Silent Background Upgrades:** Updates applications silently behind the scenes without popping up console windows or interactive prompts.
* **"Upgrade All" Support:** Quickly trigger updates for all packages at once with a single command.
* **Winget Pin Sync & Exclusions:** 
  * Hide pinned packages (configured via `winget pin`) from your upgrade results list.
  * Optionally sync exclusions to winget: adding an app to the plugin exclusions list will automatically pin/unpin it in winget.
* **Manual Refresh Command:** Type `up refresh` or `up r` to bypass cache and query `winget` live for updates.
* **Customizable Cache Lifetime:** Customize how long pending updates are cached (e.g. 15 mins) via the settings UI.
* **Diagnostics Logging:** Integrates native Flow Launcher logging for failed updates and errors.
* **Missing Winget Fallbacks:** Provides automatic links to Microsoft's instructions or the community automated `asheroto/winget-install` script if `winget` is missing.

---

## Usage

1. Open Flow Launcher.
2. Type `up` to see the list of upgradable applications.
3. Select an application and press `Enter` to upgrade it silently.
4. Select the first **"Upgrade All Applications"** result to upgrade all packages at once.
5. Filter results by typing a search term (e.g. `up git` or `up vscode`).
6. Force check for new updates by typing `up r` or `up refresh`.

---

## Settings Configuration

Access the settings panel inside Flow Launcher Settings to configure:
* Enabling/disabling the "Upgrade All" keyword.
* Pinned application synchronization with winget pins.
* Cache expiration interval (in minutes).
* Excluded applications list (by Name or Package ID).

---

## Requirements

* **Windows Package Manager (winget)** installed.
* **.NET 8 Runtime** installed (pre-packaged with modern Flow Launcher releases).

---

https://github.com/user-attachments/assets/ed50a964-a352-4f20-9f2f-ab72507192e6

[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/I3I71MHY6B)
