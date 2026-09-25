# Fake PS4 for testing Move

`fakeps4.py` pretends to be a PS4 on `127.0.0.1` so the storage move can be tested end to end without a console:

- **FTP 2121** (pyftpdlib) over a folder that stands in for the console's drives (`user/...` for system storage, `mnt/ext0/user/...` for extended storage).
- **Remote Package Installer on 12800**: `uninstall_game` deletes the title's `app`/`patch` folders; `get_task_progress` reports every task finished.
- **BinLoader on 9090**: takes DPI's experimental payload, reads the callback address from the `B4B4B4B4B4B4` marker, then answers like the payload does: command 3 (free space) and command 4 (install from a file on the console, replying result + task id). A file named `.fail` in the root makes it refuse installs with "not enough space" (0x80990039).

## Run

```
pip install pyftpdlib
python fakeps4.py <root folder> <path to Payload\payload_experimental.bin> <log file>
```

`MoveTest.cs` is a console program (reference `DirectPackageInstaller.csproj`) that runs `ConsoleMove`'s prepare / uninstall / install steps against it and prints the fake drives after each step. Arguments: `<root> [system] [failfirst]` (`failfirst` refuses the first install, then retries).

Put a title in the root first, e.g. `user/app/SPSX14001/app.pkg`, `user/patch/SPSX14001/patch.pkg`, `user/addcont/SPSX14001/<LABEL>/ac.pkg` (real PKG files: the headers are read).
