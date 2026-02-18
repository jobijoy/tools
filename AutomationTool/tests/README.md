# IdolClick Test Suites

## Smoke Test Suites

Desktop-focused test suites with graduated complexity for autonomous end-to-end testing.

## Test Files

| File | Level | Focus | Tests | Difficulty |
|------|-------|-------|-------|------------|
| `level-1-desktop-basic.json` | 1 | Desktop Apps | 6 | Simple |
| `level-2-desktop-workflows.json` | 2 | Desktop Workflows | 5 | Medium → Complex |

**Total: 11 tests** across all suites.

## Running

```powershell
# Run a single level
.\Run-SmokeTests.ps1 -File "tests\level-1-desktop-basic.json"

# Run specific tests from a level
.\Run-SmokeTests.ps1 -File "tests\level-2-desktop-workflows.json" -Tests "L2-01,L2-03"

# Run with custom log output
.\Run-SmokeTests.ps1 -File "tests\level-2-desktop-workflows.json" -LogPath "C:\temp\level2.txt"

# CLI directly
IdolClick.exe --smoke --file tests\level-1-desktop-basic.json
IdolClick.exe --smoke --file tests\level-2-desktop-workflows.json --log output.txt
```

## Level Details

### Level 1 — Basic Desktop (Simple)
Single-step app launches and simple interactions. Validates the agent can reliably open and verify Windows desktop applications: Calculator, Notepad, Settings, Paint, screenshots.

### Level 2 — Desktop Workflows (Medium → Complex)
Multi-step interactions across one or two desktop apps. Chained calculations, text editing with read-back, file save/reopen, and cross-app data transfer (Calculator → Notepad pipeline).

## Output Structure

Each test run produces:
- **Log file**: Incremental real-time log in `logs/smoke_YYYYMMDD_HHMMSS.txt`
- **Screenshots**: Per-test screenshots in `reports/_smoke_screenshots/{testId}/`
  - Step screenshots: `step1.png`, `step2.png`, etc. (or custom labels)
  - Verification screenshots: `verify_HHMMSS_fff.png`
- **Execution reports**: `reports/{testName}_{timestamp}/report.json`

All output follows the `execution-report.schema.json` format for machine consumption.

## Schema

Test files validate against `schemas/smoke-test.schema.json`. Key features:
- **Multi-step prompts**: Each test can have multiple sequential agent prompts
- **Screenshots**: Capture after each step or on demand
- **Intermediate verifications**: Check state between steps
- **File-level defaults**: Set timeout, screenshot, delay defaults for all tests
- **Difficulty tiers**: Simple / Medium / Complex for filtering and reporting
