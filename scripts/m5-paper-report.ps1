<#
.SYNOPSIS
  Operational paper-trading report (docs/m5-protocol.md §7.2–§7.3; user decision U-M5-5). User only:
    powershell -ExecutionPolicy Bypass -File scripts/m5-paper-report.ps1
.DESCRIPTION
  Only operational fields, the part Claude Code may see: cycles and their durations, late and missed cycles,
  downtime intervals, processed and caught-up bars, reconciliation and invariant results, duplicate fills, error
  and rate-limit counts, operational action counts, memory and CPU. No prices, PnL, positions, trades, signals or
  correlations (those come from data after the research cutoff; the user reads them through the Ops API).
#>
. "$PSScriptRoot\lib\m5-common.ps1"
Set-Location $script:Root

$sql = @'
SELECT 'cycles=' || count(*) FROM trading.cycle_metrics;
SELECT 'cycle_duration_ms_median=' || coalesce(round(percentile_cont(0.5) WITHIN GROUP (ORDER BY extract(epoch FROM finished_at - started_at) * 1000))::text, '-')
     || ' p95=' || coalesce(round(percentile_cont(0.95) WITHIN GROUP (ORDER BY extract(epoch FROM finished_at - started_at) * 1000))::text, '-')
     || ' max=' || coalesce(round(max(extract(epoch FROM finished_at - started_at) * 1000))::text, '-') FROM trading.cycle_metrics;
SELECT 'late_cycles(>60s)=' || count(*) FILTER (WHERE started_at - scheduled_for > interval '60 seconds') FROM trading.cycle_metrics;
SELECT 'outcomes=' || coalesce(string_agg(outcome || ':' || n, ','), '-') FROM (SELECT outcome, count(*) n FROM trading.cycle_metrics GROUP BY outcome ORDER BY outcome) o;
SELECT 'bars_processed=' || coalesce(sum(bars_processed), 0) || ' catch_up_cycles=' || count(*) FILTER (WHERE catch_up) || ' stale_cycles=' || count(*) FILTER (WHERE stale) FROM trading.cycle_metrics;
SELECT 'bars_processed_late(>5min after close)=' || count(*) FROM trading.steps s JOIN trading.paper_runs r ON r.id = s.run_id
       WHERE s.bar_time >= r.trading_start AND s.processed_at > s.bar_time + interval '4 hours 5 minutes';
SELECT 'downtime=' || coalesce(string_agg(to_char(prev AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI"Z"') || '..' || to_char(started_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI"Z"'), ','), 'none')
       FROM (SELECT started_at, lag(started_at) OVER (ORDER BY started_at) prev FROM trading.cycle_metrics) g WHERE started_at - prev > interval '65 minutes';
SELECT 'reconciliation_runs=' || count(*) || ' failures=' || count(*) FILTER (WHERE NOT ok) FROM trading.reconciliation_runs;
SELECT 'duplicate_fills=' || count(*) FROM (SELECT 1 FROM trading.fills GROUP BY run_id, sleeve, symbol, fill_time, side HAVING count(*) > 1) d;
SELECT 'negative_cash_snapshots=' || count(*) FROM trading.balance_snapshots WHERE cash < 0;
SELECT 'audit=' || coalesce(string_agg(event || ':' || n, ','), '-') FROM (SELECT event, count(*) n FROM audit.events
       WHERE event IN ('cycle_error', 'strategy_hash_mismatch', 'live_data_ip_banned', 'paper_run_created', 'trading_kill_switch_engaged',
                       'trading_kill_switch_released', 'strategy_disabled', 'close_all_requested', 'close_position_requested', 'risk_config_version_added')
       GROUP BY event ORDER BY event) a;
SELECT 'live_candle_anomalies=' || count(*) FROM market.live_candle_anomalies;
'@
$lines = @(Invoke-Psql 'ops' $sql)
$stats = docker stats --no-stream --format '{{.Name}} mem={{.MemUsage}} cpu={{.CPUPerc}}' 2>$null | Where-Object { $_ -match '^portfolio-intelligence' }
$lines += @($stats | ForEach-Object { "resources $_" })
$report = ($lines -join "`n") + "`n"
$path = Join-Path (Get-ReportDirectory) ("m5-paper-report-" + (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ') + '.txt')
[System.IO.File]::WriteAllText($path, $report)
Write-Host $report
Write-Host "Report: $path (SHA-256 $(Get-Sha256Text $report))"
