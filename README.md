# Elka FX Host

A VST2/VST3 host that sits behind its own virtual ASIO driver. It is built for VB-Audio
Matrix (and works with any ASIO host).

```
Matrix  ──►  "FX In A1…P8"   (driver outputs)  ──►  FX Host canvas: cables + VST nodes
Matrix  ◄──  "FX Out A1…P8"  (driver inputs)   ◄──  anything wired to the right side
```

It is based on Elka FX Host and keeps its canvas, VST hosting, groups,
sandboxed plugins, signal monitor, DSP diagnostics and save/load. The VoiceMeeter Remote
API, the VoiceMeeter callback, ASIO Patch / Patch.insert and the delay workspace are
**removed**. The only audio path is the Elka FX Host ASIO driver.

## Pieces

| Piece | Where | What it does |
|---|---|---|
| `ElkaFxHost.Asio.dll` | `src/asio_driver` | User-mode ASIO driver (COM). Matrix loads it in its own process. Lists as **Elka FX Host**. |
| Link protocol | `src/matrix_link/MatrixLinkProtocol.h` | Shared memory between the driver and the app: a 4-slot ring per direction, sequence numbers, heartbeats. |
| `MatrixLinkHost` | `src/matrix_link` | App side: serves each driver block through the realtime engine (`RealtimeEngine::processInsertAsio`). |
| `ElkaFxHost.Native.dll` | `src/native_api` | Engine + plugin host, exposed to the WPF app. No VoiceMeeter code. |
| WPF app | `src/app-wpf` | One canvas: FX In banks on the left, FX Out banks on the right. |
| `MatrixLinkLoopback.exe` | `tools/MatrixLinkLoopback` | Stands in for Matrix: plays a tone into FX In and checks that it comes back on FX Out. |

## Channels

Channels come in banks of 8: `FX In A1–A8`, `B1–B8`, and so on, with `FX Out` mirrored.
You set the count in the app (**ASIO Link → ASIO channels**) or in the registry at
`HKCU\Software\ElkaSoft\FxHost\Asio\ChannelCount`:

- 32 channels (A–D): Matrix
- 64 channels (A–H): Matrix Coconut (the default)
- Custom: any multiple of 8 up to 128 (A–P)

When you change the count, the app restarts and asks Matrix to reset the driver. The
protocol and the bank naming (A–Z, then AA, AB, …) can go past 128 later. Only
`MaxChannels` in the protocol and the engine would need to change.

## Timing

The driver has no hardware, so it runs its own clock (MMCSS "Pro Audio" thread with a
high-resolution timer). Each block runs through the FX Host engine during the following
block, so the round trip is two buffers in total. The driver reports `in = out = buffer
size` to Matrix. If the app is closed or misses a block, Matrix gets silence on FX Out,
never stale audio, and streaming carries on.

FX In and FX Out are severed: an FX Out channel is silent unless a line on the canvas
feeds it (FX In -> VST -> FX Out, or FX In -> FX Out directly). Nothing passes through dry; the dotted lines are
only guides.

## Run and test

See [docs/AsioDriver.md](docs/AsioDriver.md).

## Installer

`scripts\publish-release.ps1` publishes the app and builds
`artifacts\release\ElkaFxHostSetup-v<version>.exe` with Inno Setup
(`installer/ElkaFxHost.iss`). One setup installs the app and the driver. It registers the
driver silently, asks Matrix (32) / Matrix Coconut (64) / Custom (8–128), and the
uninstaller removes the driver again. For silent installs, use `/VERYSILENT /CHANNELS=32`.
It installs side by side with Elka FX Host.

## Not done yet

- The check projects under `tests/` have not been updated to remove VoiceMeeter and ASIO Patch.
- Internal type names (`VoicemeeterKind`, `VoicemeeterIoLayout`, the `.csproj` and native
  DLL names) are still the old ones. They no longer do anything with VoiceMeeter.
