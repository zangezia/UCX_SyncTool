# UCX SyncTool

A small set of utilities and a WPF client used to manage and monitor file synchronization for UCX projects. This repository contains:

- `UCXSyncTool/` — a WPF application (UI) for monitoring and managing sync operations.
- `tools/icon-gen/` — a small console utility used to generate or process icons (example tool included).
- `DUSync-4.ps1` — a PowerShell script for real-time project synchronization using `robocopy` across multiple worker nodes.

This README explains what each part does, how to build and run the projects on Windows, and quick usage examples.

## Project highlights

- Lightweight WPF monitoring UI built for .NET 8 targeting Windows (see `UCXSyncTool/`).
- A small .NET console tool in `tools/icon-gen/` for icon generation tasks used by the project.
- A pragmatic, battle-tested PowerShell script `DUSync-4.ps1` that runs multiple `robocopy` instances (one per source) and auto-stops them after an idle timeout.

## Repository layout

```
/ (repo root)
├─ UCXSyncTool/          # WPF application (UI)
│  ├─ App.xaml
│  ├─ MainWindow.xaml
│  ├─ UCX.SyncTool.csproj
│  └─ ...
├─ tools/
│  └─ icon-gen/          # small console app (IconGen)
│     ├─ Program.cs
│     └─ IconGen.csproj
└─ DUSync-4.ps1          # PowerShell realtime sync script
```

## Requirements

- Windows 10/11 (WPF UI)
- .NET 8 SDK (or matching SDK used to build the project)
- PowerShell (pwsh / PowerShell 7+ recommended) for `DUSync-4.ps1`

You can download .NET SDK from: https://dotnet.microsoft.com/en-us/download

## Build and run

All commands below assume you run them from a PowerShell (pwsh) prompt on Windows.

### Build the WPF application (`UCXSyncTool`)

1. Open a PowerShell terminal and go to the `UCXSyncTool` folder:

```powershell
cd .\UCXSyncTool
```

2. Restore and build with `dotnet`:

```powershell
dotnet restore
dotnet build -c Release
```

3. Run the app (use the appropriate runtime if you built for `net8.0-windows`):

```powershell
dotnet run --project .\UCX.SyncTool.csproj
```

Or launch the compiled executable from `UCXSyncTool\bin\Debug\net8.0-windows` after building.

### Build the icon generator (`tools/icon-gen`)

```powershell
cd .\tools\icon-gen
dotnet restore
dotnet build -c Release
dotnet run --project .\IconGen.csproj
```

The `icon-gen` utility shipped here is an example console tool; adapt its arguments or code to your needs.

## Using the real-time sync script (`DUSync-4.ps1`)

`DUSync-4.ps1` is a PowerShell script that runs `robocopy` in the background for multiple worker sources. It:

- Monitors a list of worker nodes and shares
- Starts a `robocopy` process for each source when the project folder appears
- Tracks the time of last file change and forcibly stops `robocopy` for a source if no new files appear for the configured idle timeout
- Writes per-node logs under a `Logs` folder inside the destination root

Basic usage:

```powershell
# Example: sync project "Flight_001" into D:\Collected with a 5-minute idle timeout
pwsh.exe -File .\DUSync-4.ps1 -Project "Flight_001" -DestRoot "D:\Collected" -IdleTimeoutMinutes 5
```

Important notes:

- The script contains a hard-coded list of worker `Nodes` and `Shares` near the top. Edit the script to match your environment.
- `robocopy` arguments used: `/S /E /MON:1 /MOT:2 /FFT /R:2 /W:3 /Z /MT:8` plus excluded folders. Adjust these for your environment.
- The script expects administrative/shared access to remote admin shares like `E$`/`F$`. Consider using proper credentials or network shares as appropriate.

## Configuration and customization

- UCXSyncTool: open `UCXSyncTool` in Visual Studio or your preferred editor to change UI, view models and services (`Services/SettingsService.cs`, `Services/SyncService.cs`).
- `DUSync-4.ps1`: modify the `$Nodes`, `$Shares`, and other parameters at the top. You can also pass parameters to the script when launching.
- `tools/icon-gen`: modify `Program.cs` to change how icons are generated or handled.

## Troubleshooting

- If the WPF app won't start, ensure you have the .NET 8 runtime installed and that you built for `net8.0-windows`.
- If `DUSync-4.ps1` cannot access remote shares, verify network connectivity, credentials, and that the target machines export the expected admin shares or UNC paths.
- Check log files produced by the script under the destination `Logs` folder to inspect `robocopy` output per source.

## Contributing

Contributions are welcome. Small suggestions:

- Open an issue describing the change you want.
- Send a PR with a clear description and focused commits.

Please keep UI changes, services and scripts separated and add/update tests where appropriate.

## License

This repository does not contain an explicit license file. If you plan to publish this project, consider adding a `LICENSE` file (e.g. MIT) to make the intended license explicit.

---

If you'd like, I can:

- Add a top-level `LICENSE` file (MIT/Apache/BSD)
- Improve `README` with screenshots and usage examples from the WPF UI
- Convert the `DUSync-4.ps1` script into a parameterized module with logging and email alerts

Tell me which of the above you'd like next and I will make the changes and test them locally where possible.