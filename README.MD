# DirectPackageInstaller Enhanced

A fork of [marcussacana/DirectPackageInstaller](https://github.com/marcussacana/DirectPackageInstaller) with a redesigned desktop app, a local PKG library and several install fixes. Everything below the line is the original README.

### What's different

- **Library.** Add the folders where you keep `.pkg` files (local, USB or network shares). They are scanned into a cover grid with games, updates and DLC grouped by title, search, filters, sorting and duplicate detection. Folders on a disconnected drive show as offline.
- **What's on the console.** One click (or automatically at startup) asks the PS4 what is installed: through GoldHEN's FTP server you get installed games, update versions and DLC; through Remote Package Installer, installed games. Each cover's bar shows it: green = on the PS4, amber stripes = update available, blue = sending.
- **Newer official updates.** Titles whose newest local update is behind Sony's latest are marked ("Update 03.49 is out").
- **Send queue.** Select games and press *Send to PS4*: packages go out one at a time (game, then updates, then DLC) with live progress, readable error messages, retry.
- **Uninstall** games, updates or DLC from the Library (Remote Package Installer).
- **Settings page** for everything that used to live in menus; *This PC's address* is picked from your network adapters and never changes on its own.
- **Fixes** for GoldHEN installs that were silently lost when sending several packages, paths that failed because of `+` in base64, a busy port 9898 reported as success, the archive picker installing the wrong PKG, and more (see the commit history).
- Pasting a path from Explorer's *Copy as path* works: the quotes are removed.
- **On PS4 page.** Everything installed on the console (system and extended storage), with a box per drive showing free space and usage (experimental payload), what's running now, uninstall, and:
  - **Move between system and extended storage** (right-click). Done on the console like Itemzflow's restore: the installed packages are set aside on the same drive, the title is removed with Remote Package Installer, and the payload installs each package from that file on the other drive. Nothing is sent from the PC; the copy set aside is only deleted after the console's task finished, and an interrupted move is found again and can be retried.
  - **Copy captures:** screenshots and video clips to `Pictures\DPI Captures`, a folder per game.
  - **Back up saves:** a game's saved data for every console user, zipped to `Documents\DPI Save Backups` (restore with Apollo Save Tool).
  - **Game patches:** GoldHEN's patch database (60 FPS, resolution and more, from PS-Game-Patch) for the installed version; switch them on and save, and they apply when the game starts (needs GoldHEN's Game Patch plugin).
- **Install to** system or extended storage per package (experimental payload): it switches the console's Application Install Location setting around each install and puts it back.
- **Unlock-key DLC** (no-data licence packages, which the console's downloader refuses) are copied to `/data/pkg` for GoldHEN's Package Installer, and show as installed afterwards.
- **Check a package for damage** before sending it (recomputes the checksums inside the PKG), **BACKPORT** badges on updates rebuilt for older firmware, split packages (`_0.pkg`, `_1.pkg`, …), official update downloads, library tidy-up (clear names, game folders, duplicates to the Recycle Bin), Explorer right-click menu.
- **Homebrew page:** free PS4 apps (Apollo Save Tool, ezRemote Client, the Homebrew Store app, GoldHEN Cheat Manager, or any GitHub project you add) downloaded from their authors' releases and sent like any PKG.
- **Android:** the same pages under bottom tabs, sized for phones.
- **Settings:** send your own payload (.bin/.elf) to GoldHEN's BinLoader.
- `Tools/FakePS4`: a fake console on localhost to test moves end to end without a PS4.
- Buttons are only enabled when they can do something, and say why when they can't; pop-ups tell you when sends and moves finish.

### Build (Windows)

```powershell
powershell -ExecutionPolicy Bypass -File build-windows.ps1            # Release\Windows-X64.zip
powershell -ExecutionPolicy Bypass -File build-windows.ps1 -Runtimes win-x64,win-arm64 -SelfContained
```

The script installs a .NET 8 SDK into `.dotnet\` if none is found. The GitHub workflow `.github/workflows/windows.yml` builds the same zips; running it manually with *publish* ticked updates the `Updater` branch that the in-app updater reads.

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
