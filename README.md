# P.L.U.M.B.Y.R (F# / Avalonia)

**Plumbing Linked Universal Maintenance & Binary Yield Reclaimer**

A modern, high-performance system maintenance utility for Windows, rebuilt from the ground up in F# using the Avalonia UI framework. Plumbyr provides a premium cleaning experience with real-time feedback and dynamic rule sets.

## Key Features

- **Dynamic Rule Engine**: Powering Plumbyr are open-source cleaning rules sourced from the **Kudu** project. These JSON-based rules allow for deep, safe cleaning of hundreds of system and application targets without requiring hardcoded logic updates.
- **Categorical Cleaning**: Organized into logical categories including **System**, **Browsers**, **Applications**, and **Gaming/GPU** for targeted optimization.
- **Command Center Terminal**: A real-time, high-fidelity log stream that shows you exactly what the application is doing, which files it's detecting, and what it's purging.
- **High-Performance Scanning**: Leverages F#'s efficient asynchronous processing to scan thousands of directories in seconds.
- **Premium UI**: Features a sleek, modern dark-mode interface with glassmorphism elements, SVG iconography, and an intuitive sidebar navigation system.
- **Log Exporting**: Save your cleaning session reports as `.txt` files directly from the app for auditing and history tracking.
- **Safety First**: Automatically identifies and skips protected system files (like waasmedic) and allows for administrative elevation only when absolutely necessary.

## Tech Stack

- **Language**: F# (Functional-first approach for reliability)
- **UI Framework**: Avalonia UI (Cross-platform ready, high-performance rendering)
- **Data Format**: JSON (Compatible with Kudu system rules)
- **Iconography**: Custom 3D rendered assets and SVG vectors

## Getting Started

### Prerequisites
- Windows 10/11
- .NET 10.0 SDK

### Building
```powershell
# Navigate to the project folder
cd plumbyr

# Build the project
dotnet build

# Run the application
dotnet run
```

## Acknowledgments
Special thanks to the open-source community for the cleaning rulesets that make this project possible.
