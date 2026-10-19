# Laptop Checker

**A portable, offline, step-by-step hardware checker for buying second-hand Windows laptops. Run it from a USB stick with nothing to install.**

## Features

**Dashboard**
- Specs: model, serial, BIOS, CPU, RAM modules, GPU, display, motherboard, OS, network adapters
- Storage: drive type, Windows health status, usage per volume
- Battery: health, cycle count and an automatically generated `powercfg` battery report
- Heads-up warnings for single-channel RAM, HDDs, unhealthy drives and worn batteries

**Guided tests** (Yes / No / Skip, one at a time, state remembered while the app is open)
- **Camera:** preview with camera dropdown, snapshots
- **Microphone:** record, play back, live visualiser
- **Speakers:** left, right, both and a frequency sweep, with a Stop button
- **Keyboard:** every key including the numpad, with key animations
- **Display:** fullscreen colour cycle for dead pixels and backlight bleed
- **Touchpad & mouse:** clicks, double-click and scroll on an interactive star field
- **USB ports:** detects each drive you plug in
- **Battery & charger:** capacity, live charging status, battery report

**Private by design:** everything runs offline, and all working files live in `Laptop Checker-data` next to the exe. That folder is deleted when you quit. No reports are saved; results can be copied to the clipboard.

## How it works

A small C# program starts a local web server and opens the UI in Edge's app-window mode (Chrome if Edge is missing). The browser handles camera, mic, speaker and keyboard tests. The C# side handles WMI hardware data, `powercfg`, USB detection and cleanup.

## Requirements

- **Run:** Windows 10 or 11 with Edge or Chrome. Nothing else.
- **Build (once, on any PC):** [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## Build

```bat
build.bat
```

or

```bat
dotnet publish -c Release -o dist
```

This produces `dist\Laptop Checker.exe` (about 70 MB, self-contained). Copy it to your USB stick.

## Usage

1. Run `Laptop Checker.exe` on the laptop you're checking.
2. Compare the **Dashboard** with the listing.
3. Work through the **Tests**, then copy the **Summary** if you want a record.
4. Quit. Everything the app created is deleted.

Tip: plug in the charger and set the volume to about 50% first.

An `index.html` placed next to the exe overrides the built-in UI, so you can edit the interface without rebuilding.

## Limitations

- The Fn key and some Windows-key combos can't be detected by the keyboard test.
- Many batteries report 0 cycles even when they have more.
- Drive health is Windows' summary status, not full SMART data.
- The speaker test is by ear and can't detect electrical faults.