$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSScriptRoot '..\TelemetryFreshness.cs') -ErrorAction Stop
$now = [DateTime]::SpecifyKind([DateTime]::Parse('2026-10-08T10:00:00'), [DateTimeKind]::Utc)
$timeout = [TimeSpan]::FromSeconds(5)
$cases = @(
  @{Name='missing'; Time=[DateTime]::MinValue; Expected=$false},
  @{Name='same moment'; Time=$now; Expected=$true},
  @{Name='within threshold'; Time=$now.AddSeconds(-4); Expected=$true},
  @{Name='at threshold'; Time=$now.AddSeconds(-5); Expected=$true},
  @{Name='older than threshold'; Time=$now.AddSeconds(-6); Expected=$false},
  @{Name='future timestamp'; Time=$now.AddMilliseconds(1); Expected=$false}
)
foreach ($c in $cases) {
  $got = [OmenSuperHub.TelemetryFreshness]::IsFresh($c.Time, $now, $timeout)
  if ($got -ne $c.Expected) { throw "Case $($c.Name): got $got expected $($c.Expected)" }
  Write-Host "PASS $($c.Name)"
}
Write-Host 'Telemetry freshness tests passed.'
