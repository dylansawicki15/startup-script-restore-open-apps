# Installing startup-apps

This folder is a Windows sign-in helper. It reopens the apps that were open at the last
shutdown, each on the monitor and virtual desktop (Win+Tab) where it was. The user wants
it installed on this PC.

## Files
- `startup-apps.ps1`: the script. `-AtLogon` restores the saved windows, then starts a
  background recorder (`-Watch`) that saves open windows to
  `%LOCALAPPDATA%\startup-apps-layout.json` every 20 s.
- `WindowLayout.cs`: C# helper the script loads with `Add-Type` from its own folder. It
  must sit in the same folder as the script.
- `install.ps1`: copies both files to `%USERPROFILE%\.local\bin` and creates
  `startup-apps.lnk` in the user's Startup folder, which runs
  `pwsh -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File <script> -AtLogon`.

## Steps
1. **Check for PowerShell 7.** Run `pwsh -v`. If it's missing, ask the user before running
   `winget install Microsoft.PowerShell`. The script needs pwsh 7 because it uses
   `[Environment]::ProcessPath`. Windows PowerShell 5.1 won't work.
2. **Check for an earlier install.** If `%USERPROFILE%\.local\bin\startup-apps.ps1` already
   exists, or the Startup folder already has a `startup-apps.lnk`, show the user and ask
   before overwriting them.
3. **Install.** From this folder, run:
   `pwsh -NoProfile -ExecutionPolicy Bypass -File .\install.ps1`
4. **Check that the helper compiles** without starting a restore:
   `pwsh -NoProfile -Command "Add-Type -Path $env:USERPROFILE\.local\bin\WindowLayout.cs; [StartupApps.Windows]::List() | Select-Object -First 3 ProcessName, Desktop, Monitor"`
   You should get a few window records back. If this fails, report the compiler error to
   the user word for word. Don't patch `WindowLayout.cs` blind.
5. **Check the shortcut.** Read `startup-apps.lnk` with `WScript.Shell` `CreateShortcut`.
   Its target should be pwsh.exe, and its arguments should point at the installed script
   with `-AtLogon`.
6. **Tell the user what to expect:** nothing is restored at the first sign-in, because no
   layout has been recorded yet. From the second sign-in on, their apps come back where
   they were. The log is `%LOCALAPPDATA%\startup-apps.log`.

## If something goes wrong
- **Nothing runs at sign-in.** Company policy may block scripts (AppLocker or
  Constrained Language Mode, which `-ExecutionPolicy Bypass` doesn't get around) or the
  Startup folder. Check `$ExecutionContext.SessionState.LanguageMode` (it should be
  `FullLanguage`) and the log. Don't try to work around company security controls. Tell
  the user and let them take it up with IT.
- **The log says "desktop moves unavailable".** This Windows build uses different hidden
  interface IDs for virtual desktops than the ones in `WindowLayout.cs`. Windows still go
  back to the right monitor, just not the right desktop. Fixing it means finding the IDs
  for this build (`winver`), so ask the user before taking that on.
- **Uninstall:** delete the Startup shortcut, then the two files in `.local\bin`, and
  optionally `%LOCALAPPDATA%\startup-apps-layout.json` and `startup-apps.log`. Ask before
  deleting.
