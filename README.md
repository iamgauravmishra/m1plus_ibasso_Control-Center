# DAC Control Center

Windows desktop control application for supported USB DAC devices:

- iBasso DC07 Pro
- Shanling M1 Plus (USB DAC mode / SyncLink volume control)

The application provides device-specific controls, connection monitoring, volume handling, and Windows notification-area integration.

> **Status:** Version 9.7.4. This is a community project package. Hardware behaviour should be verified on each supported Windows/device configuration. The source archive intentionally excludes compiled binaries and installer output.

## Requirements

- Windows 10 or Windows 11, 64-bit
- .NET 10 SDK
- Inno Setup 6 (only needed to build the Windows installer)
- USB connection and any required vendor driver for the DAC

## Build and run from source

Open PowerShell in the repository root:

```powershell
dotnet restore .\DC07Pro.Wpf.csproj
dotnet run --project .\DC07Pro.Wpf.csproj
```

Open `DC07Pro.Wpf.csproj` in Visual Studio 2022 or newer with the .NET desktop development workload to edit the WPF/XAML UI.

## Build the installer

Install Inno Setup 6, then run from PowerShell:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\Build-Installer.ps1
```

The script publishes a self-contained Windows x64 build and, when Inno Setup is installed, creates:

```text
installer\Output\DAC-Control-Center-Setup-9.7.4.exe
```

The installer is generated locally and is not committed to this source repository.

## Project layout

```text
App.xaml
App.xaml.cs
MainWindow.xaml
MainWindow.xaml.cs
DC07Pro.Wpf.csproj
Assets/
  DAC-Control-Center.ico
installer/
  DAC-Control-Center.iss
Build-Installer.ps1
```

## Dependencies

NuGet dependencies are declared in `DC07Pro.Wpf.csproj` and restored by `dotnet restore`:

- HidSharp
- NAudio
- System.Management

## Device notes

- Shanling M1 Plus in USB DAC mode is treated as a volume-control device; playback-library controls are outside this mode's scope.
- Shanling connection status depends on the USB DAC endpoint and SyncLink communication.
- DAC volume ranges and supported controls are device-specific.

## Security and privacy

Review the source and installer script before installing or distributing builds. The installer requires administrator privileges to install under Program Files. Do not run builds from untrusted forks.

## License

No license is included in this repository package. Unless the project owner adds a license, normal copyright restrictions apply; public visibility on GitHub does not automatically grant permission to reuse, modify, or redistribute the code.
