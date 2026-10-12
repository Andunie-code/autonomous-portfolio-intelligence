# Autonomous Portfolio Intelligence

Personal crypto spot portfolio research and paper/Testnet trading system (.NET 10, modular monolith).
No real-money trading. Planning, requirements and decisions live in [`docs/`](docs/README.md).

## Status
M0 (foundation) — see [`docs/roadmap.md`](docs/roadmap.md). No market data, strategies, AI calls or trading yet.

## Requirements
- .NET SDK 10.0.401+ (pinned in `global.json`)
- Docker (dev database and Testcontainers-based tests)
- Windows PowerShell 5.1 or PowerShell 7

## Commands
```powershell
# Bring up the local dev database and apply migrations (creates .env with a random local password)
powershell -File scripts/dev-up.ps1

# Full local verification: build (warnings as errors), all tests, secret scan
powershell -File scripts/verify.ps1

# Secret scan only (run before every push)
powershell -File scripts/scan-secrets.ps1
```

## Layout
```
src/Kernel                 Pure deterministic domain code (no dependencies)
src/Infrastructure         Logging with secret redaction, SQL migrations
src/Modules/Research       Research module (never references Execution)
src/Modules/Execution      Execution module (Trading.Host only)
src/Hosts/Trading.Host     The only process that will hold exchange credentials
src/Hosts/Research.Host    Research process
src/Tools/DbMigrator       Applies embedded SQL migrations
tests/Architecture.Tests   Module boundaries, holdout-secret rule, repository guardrails
tests/Infrastructure.Tests Redaction, host log canary, migrations (Testcontainers)
```
