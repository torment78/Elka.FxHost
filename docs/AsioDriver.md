# Testing Elka FX Host from Visual Studio

## Build and launch

1. Open `Elka.FxHost.sln` in Visual Studio 2026 (2022 also works).
2. Pick `Debug | x64` with `Elka.VoiceMeeterFxHost.App` as the startup project, then press **F5**.
   The build runs CMake and produces the engine, the plugin worker, `ElkaFxHost.Asio.dll`
   and `MatrixLinkLoopback.exe`. All of them land next to the app in
   `src\app-wpf\bin\Debug\net8.0-windows\win-x64\`.
3. The ASIO link is always on while the app runs; there is no Start/Stop. The **ASIO Link**
   panel shows the driver state. The driver itself is always listed in Matrix, even when
   FX Host is closed (Matrix then gets silence on FX Out).

VoiceMeeter is not needed and not used.

## Register the driver (once, as administrator)

ASIO hosts only list drivers registered under `HKLM\SOFTWARE\ASIO`:

```powershell
.\scripts\register-asio-driver.ps1            # registers the Debug build's DLL
.\scripts\register-asio-driver.ps1 -Channels 32
.\scripts\register-asio-driver.ps1 -Unregister
```

The script asks for elevation. Matrix loads the DLL from the registered path, so stop the
ASIO device in Matrix (or close Matrix) before you rebuild. Otherwise Windows keeps the
DLL locked.

## Test without Matrix

With the app running:

```powershell
cd src\app-wpf\bin\Debug\net8.0-windows\win-x64
.\MatrixLinkLoopback.exe                       # tone into FX In A1, expect it back on FX Out A1
.\MatrixLinkLoopback.exe --channel 127 --buffer 64
.\MatrixLinkLoopback.exe --fake-host           # tests only the driver; the app is not needed
```

The loopback tool loads the DLL directly, so it does not need the driver registered.
`RESULT: PASS` means the clock rate is right and the tone came back at the same level.

## Test in Matrix

Matrix scans ASIO drivers when it starts, so restart Matrix once after installing.

1. Register the driver, start FX Host, then open Matrix.
2. Add the ASIO device **Elka FX Host**. Its outputs are `FX In A1…` and its inputs are `FX Out A1…`.
3. Route a source into `FX In A1/A2`. In FX Host every channel is its own mono node
   (`FX In A1`, `FX In A2`, … 64 per side for Coconut).
   - **Link nodes:** Ctrl-click two or more neighbouring nodes on one side (for example
     FX In A1 and A2), then press Enter. They become one node, `FX In A1-A2` (stereo), or
     wider, such as `A1-A6`. Each side links separately. Right-click a linked node and
     choose **Unlink Channels** to split it into mono nodes again. Links are saved with
     the session, and cables stay attached per channel.
   - **Wire quickly:** Ctrl-click a node, Ctrl-click a VST, Ctrl-click an FX Out node,
     then press Enter.
4. Route `FX Out A1/A2` in Matrix to your speakers.

Things to check: low buffer sizes (64/128), 44.1/48/96 kHz, starting and stopping FX Host
while Matrix runs, and changing the channel count (Matrix should reset the device).

## Settings and data

- App data: `%LOCALAPPDATA%\ElkaSoft\FxHost` and `%APPDATA%\ElkaSoft\FxHost`. This is
  separate from VoiceMeeter FX Host, so neither app touches the other's settings.
- Driver: `HKCU\Software\ElkaSoft\FxHost\Asio`
  - `ChannelCount`: 8–128 in steps of 8 (default 64)
  - `PreferredBufferSize`: default 256
  - `UseInt32Samples`: 1 makes the driver report Int32LSB instead of Float32LSB, for hosts that need it
