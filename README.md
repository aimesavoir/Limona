# Limona V1.0

A Windows desktop quick-action app: pressing **Ctrl + Right Mouse Button** anywhere
pops a transparent radial wheel centered on the cursor with per-file-type actions
(convert, edit, resize…). Runs in the background (tray icon) and starts with Windows.

Built with WinUI 3 (.NET 9, Windows App SDK 1.8.x), unpackaged and
self-contained. The per-user installer supports 64-bit Windows 10 (version
1809/build 17763 or newer) and Windows 11; it does not require administrator
rights or a separately installed .NET runtime.

## Build

Run from the repository root in PowerShell with the .NET 9 SDK and Windows App
SDK workloads installed:

```powershell
dotnet build .\Limona\src\Limona\Limona.csproj
```

Output: `Limona\src\Limona\bin\x64\Debug\net9.0-windows10.0.19041.0\win-x64\Limona.exe`

## Run

Launch `Limona.exe`. It runs in the background with a tray icon
(**Limona V1.0 · Ctrl + Right-click**). Right-click the tray icon for
Settings / Exit; left-click opens the wheel at the cursor.

Only one instance runs at a time.

The interface supports English, French, Arabic, and Spanish. Right-click the
tray icon and choose **Language** to switch; the choice is saved in
`%LOCALAPPDATA%\Limona\settings.json`. Arabic helper windows use a right-to-left
layout.

Dev/automation aids:
- `Limona.exe --install-fonts` — installs the bundled font pack for the current user and exits.
- `Limona.exe --test-show` — dev smoke test that opens the menu briefly.

## Build the Windows installer

Install Inno Setup 6, then run from the repository root in PowerShell:

```powershell
.\Limona\installer\build-installer.ps1
```

The script publishes a self-contained x64 Release build and creates
`Limona\artifacts\setup\Limona-Setup-1.0.0.0.exe`. The English installer
offers an app-language choice (English by default, French, Arabic, or Spanish),
creates a Start Menu shortcut, offers an optional desktop shortcut, and
automatically launches Limona when setup finishes. Limona is installed per user
under `%LOCALAPPDATA%\Programs`. When an existing installation is detected,
setup offers repair (replace program files while keeping settings and data) or
uninstall-and-reinstall (also keeping settings and data). It force-closes
Limona and its child processes before replacing or removing application files.
The installer registers the existing Windows startup behavior; uninstalling
removes that startup entry and application files while preserving user settings
and data under `%LOCALAPPDATA%\Limona`.
