# Installation, upgrade, and uninstall

## Verify and install

1. Download and expand `PaperBridge-0.1.0-win-x64.zip`.
2. Compare the archive SHA-256 with `SHA256SUMS.txt` using `Get-FileHash`.
3. Review the included PowerShell scripts. They are intentionally plain text and unsigned.
4. Run:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\Install-PaperBridge.ps1
```

The default program directory is `%LOCALAPPDATA%\Programs\PaperBridge`. A Start menu shortcut is created. Use `-NoStartMenuShortcut` or an explicit `-InstallDirectory` when needed.

Running the installer again atomically replaces program files and preserves `%LOCALAPPDATA%\PaperBridge` plus Credential Manager entries.

## Uninstall choices

Close PaperBridge first. The default action removes only program files and preserves user data and API credentials:

```powershell
.\Uninstall-PaperBridge.ps1
```

Explicit destructive choices are separate:

```powershell
# Remove program files and all local PaperBridge data, but retain API credentials.
.\Uninstall-PaperBridge.ps1 -DeleteUserData

# Remove program files and known PaperBridge Credential Manager entries, but retain local data.
.\Uninstall-PaperBridge.ps1 -DeleteCredentials

# Remove program files, local data, and known credentials.
.\Uninstall-PaperBridge.ps1 -DeleteUserData -DeleteCredentials
```

The credential targets are `PaperBridge/translation/openai`, `PaperBridge/translation/deepseek`, and `PaperBridge/translation/openai-compatible`. The scripts reject drive-root targets and do not recursively delete an unresolved broad path.
