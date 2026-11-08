# Crucify.Extract

A drag-and-drop add-on installer for **WoW Forever**. Drop RAR/ZIP/7Z archives (or whole folders) onto the glowing drop zone — Crucify.Extract finds your game folder, extracts the archives, and copies the add-ons straight into `Interface\AddOns`.

## Run it

`dist\Crucify.Extract.exe` — self-contained, no .NET install or WinRAR required.

## How it works

1. **Detects your client.** On first launch it checks saved settings, then installed-program entries, then common folders, then scans your drives for a folder containing `Wow.exe` and/or `Data` + `Interface`. You can always click **Browse** and pick the folder with `Wow.exe` in it.
2. **Queue anything.** Drag in multiple archives at once, mix formats, or drop a folder of archives — folders are scanned for `.rar`, `.zip`, `.7z`, `.tar`, `.gz`, `.tgz`, `.bz2`, `.xz`. Dropping an add-on folder (anything with a `.toc`) installs it as-is.
3. **Smart unpacking.** Each archive is extracted to a temp staging folder, then the real add-on folders are located by finding `.toc` files. This means it handles archives that contain a wrapper folder, a bare add-on at the root, or a full `Interface\AddOns\...` tree. Junk like `__MACOSX` and `._` files is skipped.
4. **Installs.** Add-on folders land in `Interface\AddOns\<AddonName>`. Existing add-ons are replaced when **Replace existing add-ons** is on, otherwise the archive is skipped. Split RAR volumes (`.part1.rar`, `.r00`) are handled.
5. **Restart WoW** (or `/reload`) to see the add-ons.

Errors are shown per queue row: protected archives, corrupt downloads, "nothing that looks like an add-on", etc. If Windows blocks writing into a protected install folder, the app offers to relaunch itself as Administrator with the queue preserved.

## Notes

- Settings live in `%AppData%\Crucify.Extract\settings.json`; crashes are logged to `error.log` next to it.
- Add-on titles shown after install are read from each `.toc` (`## Title:` / `## Version:`).
- The `.exe` is a single self-contained Windows x64 build (~60 MB), so it runs on machines without .NET.

## Rebuilding

```powershell
# icon (optional, only if you change tools/make-icon.ps1)
powershell -ExecutionPolicy Bypass -File tools/make-icon.ps1

# dev build
dotnet build Crucify.Extract.csproj

# single-file release build
dotnet publish Crucify.Extract.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o dist
```

### Headless checks

```powershell
dist\Crucify.Extract.exe --detect                                # prints detected clients as JSON
dist\Crucify.Extract.exe --selftest --archive C:\path\addon.rar --dest C:\temp\test
dist\Crucify.Extract.exe --selftest --folder C:\path\AddonFolder --dest C:\temp\test
```
