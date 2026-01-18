# IdolClick Installer Build

This folder contains scripts to build IdolClick installers and distributions.

## 🚀 Quick Start

### Build Windows Installer (Recommended)

```batch
build-installer.bat
```

Requires [Inno Setup 6](https://jrsoftware.org/isdl.php) to be installed.

### Build Portable ZIP

```powershell
.\Build-Portable.ps1
```

No dependencies required.

---

## Distribution Options

### 1. Windows Installer (Setup.exe) ⭐

Creates a professional Windows installer with:

| Feature | Description |
|---------|-------------|
| ✅ Program Files install | Installs to `C:\Program Files\IdolClick` |
| ✅ Start Menu shortcuts | IdolClick group with launch & uninstall |
| ✅ Desktop shortcut | Optional checkbox during install |
| ✅ Start with Windows | Optional - adds to registry Run key |
| ✅ Ctrl+Alt+T hotkey | Optional - assigns hotkey to shortcut |
| ✅ Add/Remove Programs | Full version info and branded icon |
| ✅ Clean uninstall | Removes all files and registry entries |

**Output:** `output/IdolClickSetup-1.0.0.exe`

### 2. Portable ZIP (No installation)

Self-contained ZIP that can be extracted and run anywhere.

**Output:** `output/IdolClick-1.0.0-win-x64-portable.zip`

---

## Build Scripts

| Script | Description |
|--------|-------------|
| `build-installer.bat` | Build Windows installer (requires Inno Setup) |
| `Build-Installer.ps1` | PowerShell version of installer build |
| `Build-Portable.ps1` | Build portable ZIP distribution |

### Build Options

```batch
:: Full build (publish + installer)
build-installer.bat

:: Skip publish step (use existing build)
build-installer.bat --skip-publish

:: Show help
build-installer.bat --help
```

---

## File Structure

```
installer/
├── IdolClick.iss           # Inno Setup script
├── build-installer.bat     # Windows batch build script
├── Build-Installer.ps1     # PowerShell installer build
├── Build-Portable.ps1      # Portable ZIP build
├── README.md               # This file
├── assets/                 # Installer assets
│   ├── idolclick.ico       # Application icon
│   ├── sample-config.json  # Default configuration
│   ├── wizard-large.bmp    # Installer left panel image
│   └── wizard-small.bmp    # Installer header icon
└── output/                 # Build outputs
    ├── IdolClickSetup-1.0.0.exe
    └── IdolClick-1.0.0-win-x64-portable.zip
```

---

## Requirements

| Distribution | Requirements |
|--------------|--------------|
| Windows Installer | [Inno Setup 6](https://jrsoftware.org/isdl.php), .NET 8 SDK |
| Portable ZIP | PowerShell 5.1+, .NET 8 SDK |

---

## Installer Options

During installation, users can choose:

- [x] **Start IdolClick when Windows starts** (checked by default)
- [ ] **Create a desktop shortcut** (unchecked by default)
- [ ] **Assign Ctrl+Alt+T hotkey to desktop shortcut** (unchecked)

---

## Customization

Edit `IdolClick.iss` to customize:

```pascal
#define MyAppName "IdolClick"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Jobi Joy"
```

### Changing Wizard Images

Replace the BMP files in `assets/`:
- `wizard-large.bmp` - 164×314 pixels (left panel)
- `wizard-small.bmp` - 55×55 pixels (header icon)

---

## Signing (Optional)

For enterprise deployment, sign the installer:

```batch
signtool sign /f cert.pfx /p password /t http://timestamp.digicert.com IdolClickSetup-1.0.0.exe
```
