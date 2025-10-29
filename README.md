# UCX SyncTool

A WPF application for monitoring and managing file synchronization in UCX projects using robocopy.

## Overview

UCX SyncTool is a graphical WPF application (.NET 8) that provides:

- Start and stop file synchronization processes via robocopy
- Monitor system performance (CPU load, memory usage, and disk activity)
- Track synchronization progress in real-time
- Automatically parse robocopy logs to display statistics
- Configure synchronization parameters through a user-friendly interface

## Key Features

- **Performance Monitoring**: displays CPU load, memory usage, and disk activity using `PerformanceCounter` with moving average smoothing
- **Robocopy Log Parsing**: accurate extraction of statistics from robocopy log summary sections (supports English and Russian localization)
- **Automatic Management**: automatic robocopy shutdown after idle timeout
- **Configuration**: persist settings between sessions (source paths, synchronization settings)

## Project Structure

```
UCX_SyncTool/
├─ UCXSyncTool/              # Main WPF application
│  ├─ Assets/
│  │  └─ icon.ico           # Application icon (multi-resolution)
│  ├─ Models/
│  │  ├─ ActiveCopyViewModel.cs
│  │  └─ AppSettings.cs
│  ├─ Services/
│  │  ├─ SettingsService.cs
│  │  └─ SyncService.cs     # Core synchronization logic
│  ├─ App.xaml
│  ├─ MainWindow.xaml
│  └─ UCX.SyncTool.csproj
├─ logo.tif                  # Source logo image
├─ UCX.SyncTool.sln
└─ README.md

```

## System Requirements

- **OS**: Windows 10/11
- **.NET**: .NET 8 SDK
- **Robocopy**: built into Windows (uses the standard `robocopy.exe` utility)

Download .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0

## Build and Run

### Building the Project

```powershell
# From the project root directory
dotnet restore
dotnet build -c Release
```

### Running the Application

```powershell
# From the project root directory
dotnet run --project UCXSyncTool\UCX.SyncTool.csproj
```

Or run the compiled executable from `UCXSyncTool\bin\Release\net8.0-windows\`.

## Usage

1. **Configure Sources**: add paths to source folders that need synchronization
2. **Select Project**: specify the project name for synchronization
3. **Set Destination**: choose the destination folder for synchronized files
4. **Robocopy Parameters**: configure additional synchronization parameters (exclusions, multithreading, etc.)
5. **Start**: click the start button to begin synchronization
6. **Monitor**: track progress in real-time through the UI

## Technical Details

### Performance Monitoring

- **CPU**: uses `Processor Information\% Processor Utility` with fallback to `Processor\% Processor Time`, applies moving average (3 samples) for stable readings
- **Memory**: tracks physical memory usage
- **Disk**: monitors disk read/write speeds

### Robocopy Log Parsing

The application uses a structured approach to extract statistics from robocopy log summary sections:

- **Files**: parses the `Files : <Total> <Copied> <Skipped>` line, extracts the number of copied files
- **Bytes**: parses the `Bytes : <Total> <Copied> <Skipped>` line with unit support (k, m, g, t)
- **Localization**: supports English (`Files`, `Bytes`) and Russian (`Файлы`, `Байт`) versions of robocopy

### Update Intervals

- **UI**: interface updates every 2 seconds
- **Performance**: performance counter updates every 1 second
- **Robocopy logs**: log parsing every 10 seconds

## Configuration

Application settings are stored in `AppSettings` and automatically persisted between sessions:

- List of synchronization sources
- Destination folder path
- Robocopy parameters
- UI settings

To modify default settings, edit `Services/SettingsService.cs`.

## Development

### Architecture

- **MVVM pattern**: uses Model-View-ViewModel pattern to separate logic and UI
- **Services**: isolated services for settings (`SettingsService`) and synchronization (`SyncService`)
- **Performance monitoring**: uses `System.Diagnostics.PerformanceCounter`
- **Async/await**: asynchronous processing to prevent UI blocking

### Key Components

- `MainWindow.xaml.cs`: main UI logic and component coordination
- `SyncService.cs`: robocopy process management and log parsing
- `SettingsService.cs`: application settings persistence
- `ActiveCopyViewModel.cs`: data model for active copy operations

## Troubleshooting

- **Application won't start**: ensure .NET 8 Runtime for Windows is installed
- **Inaccurate CPU readings**: verify the application has permissions to access `PerformanceCounter`
- **Robocopy logs not parsing**: ensure robocopy writes logs in standard format (don't use `/NJH` or `/NJS`)
- **Files not synchronizing**: check access permissions to source and destination folders

## License

This project is distributed without an explicit license. For commercial use, it is recommended to add a `LICENSE` file.