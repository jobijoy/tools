# Capture Profile Packs

Reusable capture packs bundle four things together:

- a capture profile that can be imported into your local IdolClick config
- an optional bootstrap flow that opens or navigates the target app
- an observation plan describing interval, duration, and optional short audio clip collection
- queue and analysis hints for downstream utilities

## Included Packs

| File | Purpose |
|------|---------|
| [calculator-harness.capture-profile.json](calculator-harness.capture-profile.json) | Reusable Calculator capture profile used by the headless harness |
| [youtube-video-monitor.capture-profile.json](youtube-video-monitor.capture-profile.json) | Browser-monitoring pack for periodic YouTube screenshots plus short audio notes |
| [finance-yahoo-stock-monitor.capture-profile.json](finance-yahoo-stock-monitor.capture-profile.json) | Finance-monitoring pack for repeated Yahoo Finance quote snapshots |
| [google-search-monitor.capture-profile.json](google-search-monitor.capture-profile.json) | Website-navigation pack for repeated Google search result snapshots |
| [notepad-region-monitor.capture-profile.json](notepad-region-monitor.capture-profile.json) | Window-region monitoring pack that captures only the interesting panel inside a window |
| [stock-chart-monitor.capture-profile.json](stock-chart-monitor.capture-profile.json) | Template for watch-and-queue scenarios such as charting apps |

## Loading a Pack

```powershell
.\Import-CaptureProfile.ps1 -File "examples\capture-profiles\calculator-harness.capture-profile.json"
```

By default the import script writes into the built app config under `src/IdolClick.App/bin/Release/net8.0-windows/config.json` if that file exists. You can also pass `-ConfigPath` explicitly.

## One-Command Validation

Each canonical pack now has a direct wrapper script:

- `./Run-YoutubeVideoMonitor.ps1`
- `./Run-FinanceYahooStockMonitor.ps1`
- `./Run-GoogleSearchMonitor.ps1`
- `./Run-NotepadRegionMonitor.ps1`

Add `-Full` to run the full observation duration from the pack metadata instead of the short smoke mode.
For Yahoo Finance, add `-Symbol MSFT` or another ticker to run a different quote without editing JSON.
For Google Search, add `-Query "Windows UI Automation"` or another query without editing JSON.

## Workflow Pattern

The pack itself stores the reusable capture target. The `bootstrapFlowPath` points to the deterministic launch/navigation flow that gets the app into the right state before observation starts.

That separation matters because capture targets are stable observation definitions, while flows describe how to reach a state.

The `observationPlan` section is intentionally metadata for now. It describes how a scheduler, orb automation, or another utility should run the pack:

- `triggerMode`: manual, interval, status-change, or hybrid
- `intervalSeconds`: recommended screenshot cadence
- `durationSeconds`: total observation window
- `audioClipSeconds`: recommended short audio-note duration around each snapshot
- `queueSnapshots`: whether another utility should treat captures as a queue source

Packs can now also declare `inputs`, which lets wrapper scripts or the CLI pass values such as ticker symbols and search queries without rewriting pack files.

The runner now records status-selector snapshots in the pack report, so each capture includes lightweight state context in addition to image and metadata paths.
