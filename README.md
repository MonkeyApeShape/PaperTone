# PaperTone — Windows prototype

PaperTone is a Windows system-tray prototype that listens for global pen movement and pressure through the tablet driver's WinTab system context. It does not create a drawing canvas, take focus, or require a drawing application to be active.

## What this prototype does

- Opens a WinTab **system context** to receive pen packets globally.
- Starts audio only when the pen is in contact (pressure above a small threshold) and its coordinates are changing.
- Stops the sound when movement packets cease (45 ms watchdog), including when the pen is held still.
- Uses pressure to control the level and grain density of a procedural pencil/paper-like sound.
- Provides an enable/disable toggle and master-volume choices in the Windows notification-area tray menu.
- Targets .NET 10 LTS and publishes as a self-contained, single-file x64 Windows executable.

## Getting the EXE

The included GitHub Actions workflow builds the EXE on a hosted Windows runner. You do not need .NET, Visual Studio, or a compiler installed locally.

1. Open this repository's **Actions** tab.
2. Select **Build PaperTone for Windows**.
3. The workflow should start automatically after the initial upload. If not, choose **Run workflow**.
4. When the run succeeds, open it and download the `PaperTone-win-x64` artifact.
5. Extract the ZIP and run `PaperTone.exe`.

The build runner installs the .NET 10 SDK only on Microsoft's hosted build machine. The resulting executable includes the .NET runtime and does not require you to install it.

## Hardware/driver requirement

This version uses `Wintab32.dll` supplied by the tablet driver. Install the official Huion Windows driver for the Kamvas 16 Gen 3 and make sure its WinTab component is available. If the driver exposes only Windows Ink and does not install WinTab, PaperTone will show a tray status explaining that the WinTab DLL/context is unavailable.

The application is x64. The Huion driver must provide a compatible 64-bit WinTab DLL.

## Prototype limitations

- This source has not been tested against a physical Kamvas 16 Gen 3. The first GitHub Actions run verifies compilation; hardware testing is still needed to verify actual driver packets and the pressure curve.
- The sound is procedural noise/grain, not a field recording. It validates the input/audio pipeline; a convincing pencil recording and controls for pressure curve, sensitivity, and texture are future improvements.
- The 45 ms stop timeout is a starting point, not a measured latency guarantee.
- No automatic startup, device picker, calibration UI, or installer is included yet.

## Build settings

- Target: `net10.0-windows`, x64
- Deployment: self-contained, single-file
- .NET SDK is used only by the build runner (or optional local build), not by the end user.
