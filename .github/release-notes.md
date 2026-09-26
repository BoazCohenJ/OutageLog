**Download `OutageLog.exe` below and run it.** No installer and no runtime to install: it uses .NET Framework 4.8, which is already part of Windows 10 and 11. It sits in the tray and starts with Windows; right-click the icon → **Make a report** when you need one.

This build was compiled by GitHub Actions from the tagged source; the `.sha256` file lets you verify it.

Windows SmartScreen may say "Windows protected your PC" because the exe isn't code-signed yet. Click **More info → Run anyway**, or build it yourself with `dotnet build src/OutageLog -c Release`.
