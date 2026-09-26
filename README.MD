# DPI Enhanced

**A PS4 package manager for Windows and Android.** Keep your `.pkg` files in a library, see what's already on the console, send packages, move games between system and extended storage, back up saves, copy screenshots, switch on game patches and install homebrew, all from your PC or phone.

DPI Enhanced is a fork of [marcussacana/DirectPackageInstaller](https://github.com/marcussacana/DirectPackageInstaller), rebuilt around a new design. Sending from direct download links still works as before; that original README is below the line.

**On the console you need** a jailbroken PS4 with [GoldHEN](https://github.com/GoldHEN/GoldHEN): its **BinLoader** (payload server) to send packages and its **FTP server** to read what's installed. [Remote Package Installer](https://github.com/flatz/ps4_remote_pkg_installer) is optional (uninstalling, moving games, live progress), and etaHEN is supported for PS5.

---

## Features

### Library
- **Your PKG folders as a cover grid:** local, USB or network folders, scanned (subfolders too) into games, updates and DLC grouped by title, with search, filters and sorting. Folders on a disconnected drive show as offline.
- **What's already on the PS4:** one click, or automatically at startup. Each cover's bar shows it: green on the console, amber stripes when an update is missing, blue while sending. *Not on PS4* and *Duplicates* filters.
- **Newer official updates:** titles whose newest local update is behind Sony's latest are marked ("Update 03.49 is out · FW 13.52"), and the update can be downloaded from Sony next to your files.
- **Send what's missing:** the game if it isn't installed, the newest update if it's newer than the console's, and DLC that isn't there yet.
- **Check a package for damage** before sending it: the checksums inside the PKG are recomputed, and a broken download is named in plain words.
- **BACKPORT badges** on updates rebuilt for older firmware, **split packages** (`name_0.pkg`, `name_1.pkg`, …) sent as one, and **tidy-up**: clear file names, one folder per game, duplicates to the Recycle Bin / Trash.
- **Unlock-key DLC** (licence-only packages the console's downloader refuses) are copied to `/data/pkg` for GoldHEN's Package Installer, and show as installed afterwards.
- Windows **Explorer right-click menu**: *Send to PS4 (DPI)* on `.pkg` files, *Add to DPI library* on folders. Drag and drop works too.

### On PS4
- **Everything installed**, on system and extended storage, with covers, versions, updates and DLC, and what's running right now.
- **Storage boxes:** free space, total and usage for each drive (experimental payload).
- **Move between system and extended storage** (right-click). It runs on the console, the way Itemzflow restores apps: the installed packages are set aside on the same drive, the title is removed with Remote Package Installer, and the payload installs each package from that file onto the other drive. Nothing is sent from your PC, the copy set aside is only deleted after the console's own task finished, and a move interrupted by closing the app is found again and can be retried.
- **Game patches:** 60 FPS, resolution and other patches from the [PS-Game-Patch](https://github.com/illusionyy/PS-Game-Patch) database for the installed version. Switch them on, save, and they apply when the game starts (needs GoldHEN's Game Patch plugin).
- **Saves:** every save on the console, all games and all users, in one list with sizes. Back up the ones you tick (zipped per user) to a folder you choose, `Documents\DPI Save Backups` by default. Restore them with Apollo Save Tool.
- **Copy screenshots & videos** to `Pictures\DPI Captures`, a folder per game.
- **Uninstall** games, updates and DLC (Remote Package Installer).

### Sending and installing
- **Queue:** packages go out one at a time (game, then updates, then DLC) with live progress, pause/resume, retry and readable error messages. It says *Installed* when the console confirms it, and the Library and On PS4 update by themselves.
- **Install to** system or extended storage for each package (experimental payload): the console's Application Install Location setting is switched for that install and put back.
- **Direct link:** paste a URL or a path (quotes from Explorer's *Copy as path* are removed), preview the package, install. Archives, JSON manifests and debrid services work as in the original.
- Pop-ups tell you when sends and moves finish.

### Homebrew
- Free PS4 apps from their authors' GitHub releases: **Apollo Save Tool**, **ezRemote Client**, the **Homebrew Store** app and **GoldHEN Cheat Manager**, plus any GitHub project you add. *Download & send* puts the package in the Queue; nothing is re-hosted.

### Settings
- **Find consoles** on your networks (PS4 and PS5), and pick **this PC's address** from your network adapters (it never changes on its own).
- **Send a payload** (`.bin` / `.elf`) to GoldHEN's BinLoader.
- **Updates:** a dialog at startup when a newer version is out (can be switched off), and *Check now* in About.
- **About:** contributor and special-thanks cards with GitHub links.

### Android
The same pages under bottom tabs, sized for phones, with the app's own folder picker for your PKG folders.

Buttons are only enabled when they can do something, and their tooltip says why when they can't.

---

## Getting started

1. On the PS4: start GoldHEN and turn on its **BinLoader** and **FTP server** (GoldHEN settings). Open Remote Package Installer too if you want to uninstall or move games.
2. On the PC: run `DirectPackageInstaller.Desktop.exe`, open **Settings**, press **Find consoles** (or type the PS4's address), and check **This PC's address** is on the same network.
3. In **Library**, press **Add folder** and pick where your `.pkg` files are. Press **Check PS4** to see what's already installed.
4. Select packages and press **Send to PS4**. Keep DPI open until the Queue says they're done.

For the experimental features (free space, Install to, moving games), turn on **Experimental GoldHEN payload** in Settings.

## Updates

DPI checks for new versions the way InstaEclipse does: it reads [`version.json`](version.json) on this repository's default branch:

```json
{ "latest_version": "8.6.0", "update_url": "https://github.com/ReSo7200/DirectPackageInstaller-Enhanced/releases/latest" }
```

To publish a version: raise `SelfUpdate.CurrentVersion` in the app, publish a GitHub release tagged `v8.6.0` with the builds attached (`Windows-X64.zip`, the `.apk`, …), then bump `latest_version`. Without `version.json`, the latest GitHub release is used.

## Build

**Windows**

```powershell
powershell -ExecutionPolicy Bypass -File build-windows.ps1            # Release\Windows-X64.zip
powershell -ExecutionPolicy Bypass -File build-windows.ps1 -Runtimes win-x64,win-arm64 -SelfContained
```

The script installs a .NET 8 SDK into `.dotnet\` if none is found. The GitHub workflow `.github/workflows/windows.yml` builds the same zips.

**Android** needs the .NET Android workload, the Android SDK and JDK 17:

```powershell
dotnet publish DirectPackageInstaller\DirectPackageInstaller.Android\DirectPackageInstaller.Android.csproj -c Release -r android-arm64 -p:AndroidSdkDirectory=<sdk> -p:JavaSdkDirectory=<jdk17>
```

**GoldHEN payload** (`Payload\`): build with `make` / `make experimental` on Linux (or WSL) from a Linux-side clone, since the Windows checkout breaks the header symlinks.

**Testing without a console:** [`Tools/FakePS4`](Tools/FakePS4) pretends to be a PS4 on localhost (FTP, Remote Package Installer and the payload) so moves can be tested end to end.

## Credits

**Contributors:** [ReSo7200](https://github.com/ReSo7200) (DPI Enhanced), [marcussacana](https://github.com/marcussacana) (DirectPackageInstaller), [0xMH](https://github.com/0xMH), [ksanjeev284](https://github.com/ksanjeev284).

**Special thanks:** [flatz](https://github.com/flatz) (Remote Package Installer), [GoldHEN](https://github.com/GoldHEN), [LightningMods](https://github.com/LightningMods) (Itemzflow, Homebrew Store), [illusionyy](https://github.com/illusionyy) (PS-Game-Patch), [OpenOrbis](https://github.com/OpenOrbis) (LibOrbisPkg), [oct0xor](https://github.com/oct0xor) (PS4 registry research), [jpmikkers](https://github.com/jpmikkers) (DHCPServer), [AvaloniaUI](https://github.com/AvaloniaUI).

---

# DirectPackageInstaller

A tool to preview and send PKG files to your PS4 or PS5 using direct download links.  
This tool supports three different methods for sending PKGs:

-   Remote Package Installer Homebrew
-   GoldHEN Payload Server    
-   etaHEN DPIv2 API
    

Features
----------

-   Preview PKG files
-   Standalone PS4 package downloads
-   PS5 etaHEN support
-   Automatic proxying to speed up PS4 downloads
-   Support for RAR/7z compressed files
-   Resumable downloads (only for uncompressed files)
-   Segmented downloads
-   Support for several file hosting services
-   Support for PSN update manifests (split PKGs)
-   Works with or without Remote Package Installer
-   Command Line Interface (CLI)
-   JDownloader Click'n Load support
-   Auto LAN Cable Connection (Windows Only)
-   Can Install PKGs from NFS source
    

Supported File Hosting Services
----------

-   Any direct link (without authentication)
-   AllDebrid ([API key required](https://alldebrid.com/apikeys/))
-   RealDebrid ([API key required](https://real-debrid.com/apitoken))
-   DebridLink ([API key required](https://debrid-link.com/webapp/apikey))
-   Google Drive
-   Mediafire
-   PixelDrain
-   1Fichier (must wait 60 minutes after each download)
    

How to Install
----------

1.  Download a build compatible with your platform (see below).
2.  On Windows, Linux, and Android, install the [.NET 8 Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0/runtime) first.
    macOS builds are self-contained and do not require a separate .NET install.
    
Downloads:
----------

<details> <summary>Windows</summary>

-   [ARM](https://raw.githubusercontent.com/marcussacana/DirectPackageInstaller/Updater/Windows-ARM.zip)    
-   [ARM64](https://raw.githubusercontent.com/marcussacana/DirectPackageInstaller/Updater/Windows-ARM64.zip)    
-   [x64](https://raw.githubusercontent.com/marcussacana/DirectPackageInstaller/Updater/Windows-X64.zip)    
-   [x86](https://raw.githubusercontent.com/marcussacana/DirectPackageInstaller/Updater/Windows-X86.zip)    

</details> <details> <summary>Android</summary>

-   [ARM](https://raw.githubusercontent.com/marcussacana/DirectPackageInstaller/Updater/Android-ARM.zip)    
-   [ARM64](https://raw.githubusercontent.com/marcussacana/DirectPackageInstaller/Updater/Android-ARM64.zip)    
-   [x64](https://raw.githubusercontent.com/marcussacana/DirectPackageInstaller/Updater/Android-X64.zip)    
-   [x86](https://raw.githubusercontent.com/marcussacana/DirectPackageInstaller/Updater/Android-X86.zip)    

</details> <details> <summary>Linux</summary>

-   [ARM](https://raw.githubusercontent.com/marcussacana/DirectPackageInstaller/Updater/Linux-ARM.zip)    
-   [ARM64](https://raw.githubusercontent.com/marcussacana/DirectPackageInstaller/Updater/Linux-ARM64.zip)    
-   [x64](https://raw.githubusercontent.com/marcussacana/DirectPackageInstaller/Updater/Linux-X64.zip)    

</details> <details> <summary>macOS</summary>

**CLI builds:**

-   [ARM64](https://raw.githubusercontent.com/marcussacana/DirectPackageInstaller/Updater/OSX-ARM64.zip)    
-   [x64](https://raw.githubusercontent.com/marcussacana/DirectPackageInstaller/Updater/OSX-X64.zip)    

**App builds (zip):**

-   [ARM64](https://raw.githubusercontent.com/marcussacana/DirectPackageInstaller/Updater/OSX-ARM64-APP.zip)    
-   [x64](https://raw.githubusercontent.com/marcussacana/DirectPackageInstaller/Updater/OSX-X64-APP.zip)   

**App builds (DMG, drag-to-Applications):**

-   [ARM64](https://raw.githubusercontent.com/marcussacana/DirectPackageInstaller/Updater/OSX-ARM64-APP.dmg)    
-   [x64](https://raw.githubusercontent.com/marcussacana/DirectPackageInstaller/Updater/OSX-X64-APP.dmg)

macOS builds are self-contained (.NET 8 runtime bundled) and the .app is ad-hoc signed so it launches on Apple Silicon without further steps. If Gatekeeper still blocks the app after extracting the zip, run:

```
xattr -dr com.apple.quarantine /Applications/DirectPackageInstaller.app
```

</details>


How to Use
----------

#### Direct Download Mode

1.  Launch Remote Package Installer or enable GoldHEN Payload Server.    
2.  Open DirectPackageInstaller.    
3.  Paste a direct PKG URL into the "PKG URL" field.    
4.  Click "Load" and wait.    
5.  Click "Install" and wait again.    
6.  Done! You can close the application or shut down your PC.    

#### Proxied Download Mode (for PS4s without internet access)

1.  Launch Remote Package Installer or enable GoldHEN Payload Server.    
2.  Open DirectPackageInstaller.    
3.  Go to `Options -> Proxy Downloads` and enable it.    
4.  Paste a direct PKG URL.    
5.  Click "Load", then "Install".    
6.  Wait for the installation to finish.
    

#### Segmented Download Mode (fastest)

1.  Make sure you have enough free disk space.
2.  Launch Remote Package Installer or enable GoldHEN Payload Server.
3.  Open DirectPackageInstaller.
4.  Go to `Options -> Segmented Downloads` and enable it.
5.  Paste a direct PKG URL.
6.  Click "Load", then "Install".
7.  The PKG will be downloaded and preloaded before installation.

> Note: In segmented mode, the PS4 cannot track download progress accurately.

#### Compressed File Mode (always proxied)
1.  Make sure you have enough free disk space.    
2.  Launch Remote Package Installer or enable GoldHEN Payload Server.    
3.  Paste the direct URL to your compressed file.    
4.  Click "Load" and wait.    
5.  [Optional] Select the desired PKG from the file list.    
6.  Click "Install" and wait.

macOS CLI Usage
----------

Use the terminal to run the app:
```
./DirectPackageInstaller.Desktop
```
In some cases, you may need to run:
```
dotnet DirectPackageInstaller.Desktop.dll
```

NFS PKG Source
----------

To use an NFS server as a source for DPI, you just need to provide a URL in a format supported by DPI.
There are two supported NFS URL formats:

- `nfs://YOUR_NFS_IP_OR_HOSTNAME/ExportedPath/FilePath/FileName.pkg`

- `nfs://YOUR_NFS_IP_OR_HOSTNAME/FilePath/FileName.pkg`

In the first format, the path starts with the exact exported directory (as configured in the NFS server's /etc/exports).
DPI will mount that specific export and look for the file within it.

In the second format, no specific export path is provided. In this case, DPI will attempt to search across all available exports on the NFS server to locate the requested file.
Note that this may slow down the initial access if your server has many exports.

PS: The DPI is incompatible with the winnfsd due this bug [#88](https://github.com/winnfsd/winnfsd/issues/88), for windows, you can use [haneWIN NFS Server](https://www.hanewin.net/nfs-e.htm) instead.


How It Works
----------
DirectPackageInstaller is a simple tool that uses the Remote Package Installer's API to add PKG downloads directly to your PS4 or PS5. It helps you preview and send packages easily.

Additionally, the tool features an automatic proxy server to accelerate downloads. Since the PS4 has much better speeds over LAN than WAN, proxying through your PC can significantly improve performance.

It also works without the Remote Package Installer if you enable the binloader — the tool will automatically decide which method to use.

When using compressed files (RAR/7z), everything happens in parallel: download, extraction, and upload to your console. This saves time and disk space compared to traditional methods.

Segmented mode downloads PKGs in parallel threads to your PC, while sending them to the console. It's the fastest option, though it won't show progress on the PS4 screen.


Command Line Interface (CLI)
----------

An experimental CLI is available.

Note: RAR/7z and segmented downloads are not supported in CLI mode (yet).  
To view available options:

-   On Windows: `DirectPackageInstaller.Desktop.exe -help`    
-   On other systems: `DirectPackageInstaller.Desktop -help`
    



Dependencies
----------
Windows, Linux, and Android builds require the [.NET 8.0 Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0/runtime).
macOS builds are self-contained and ship with the runtime bundled.


How to Build
----------

1.  Install the .NET 8.x SDK.    
2.  Run the `Build.cmd` script (works on both Windows and Unix-like systems).    
3.  A `Release` directory with the compiled binaries will be created.    

For custom payloads, run `make` in the payload directory and replace the generated `payload.bin` inside `DirectPackageInstaller/DirectPackageInstaller/payload`.

Android builds:
----------
-   Targets API level 26    
-   Install Android SDK and NDK    
-   Accept SDK licenses    
-   Edit `Build.cmd` to set SDK/NDK paths    
-   Run `Build.cmd` as admin (first time only)

Alternatively, fork the project and use GitHub Actions for builds.

Q&A
----------

**My download is taking too long to start**  
You're probably downloading a multipart compressed file. The more volumes, the longer it takes to scan and validate. This isn’t a bug — just how compressed file access works. Please be patient.

**Download speed is slow, even in segmented mode**  
Segmented mode doesn't report accurate progress to the PS4. Use your PC's Task Manager to monitor real network usage.

**Segmented mode and only 2 Mbps?!**  
Some hosting sites (e.g., Zippyshare) are just slow. Try testing with Google Drive for better performance.

**Recommended file hostings?**

-   AllDebrid / RealDebrid    
-   Google Drive    
-   Mediafire
-   Everything else
-   1Fichier (slowest)
    

**I got an error during installation**  
Open an issue! Seriously, don’t worry — mistakes happen. If there's a crash, check the `DPI-Crash.log` for details.

Disclaimer
----------

This tool is for legitimate purposes only. It’s intended to help users install their personal backups. Piracy is not supported.  
You are responsible for how you use this software.

Warnings
----------

-   Avoid multiple parallel decompressions — process one file at a time.
-   Compressed file downloads create a temporary PKG cache, requiring free disk space equal to the final PKG size.

Credits
----------

-   **LibOrbisPkg** by _maxton_
-   **HttpServerLite** by _jchristn_
-   Payload template by _sleirsgoevy_
-   PS4 OS internals help by _LM_
-   PS4 export definitions by _OpenOrbis SDK_
-   **DirectPackageInstaller** by _marcussacana_
