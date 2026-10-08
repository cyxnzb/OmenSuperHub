#requires -Version 5.1
<#
.SYNOPSIS
  Read-only process resource baseline for OmenSuperHub.
.DESCRIPTION
  Samples the OmenSuperHub main process and its --hwmonitor child (same image name).
  Does not modify registry, BIOS, EC, fan settings, performance presets or processes.
  The results are process-level observations, not temperature/thermal performance data.
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Collect-ResourceBaseline.ps1 -DurationSeconds 600
#>
[CmdletBinding()]
param(
  [ValidateRange(10,86400)][int]$DurationSeconds = 600,
  [ValidateRange(1,60)][int]$IntervalSeconds = 2,
  [string]$OutputDirectory = (Join-Path $PWD 'baseline-results'),
  [string]$ProcessName = 'OmenSuperHub'
)

$ErrorActionPreference = 'Stop'
$started = Get-Date
$startClock = [System.Diagnostics.Stopwatch]::StartNew()
$logicalCpus = [Environment]::ProcessorCount
if ($logicalCpus -lt 1) { $logicalCpus = 1 }

New-Item -Path $OutputDirectory -ItemType Directory -Force | Out-Null
$stamp = $started.ToString('yyyyMMdd-HHmmss')
$csv = Join-Path $OutputDirectory ("resource-$stamp.csv")
$meta = Join-Path $OutputDirectory ("resource-$stamp.metadata.txt")
$rows = New-Object 'System.Collections.Generic.List[object]'
$lastCpuByPid = @{}
$processSeen = @{}

function Safe-CimValue([string]$ClassName, [string]$Property) {
  try {
    $obj = Get-CimInstance -ClassName $ClassName -ErrorAction Stop | Select-Object -First 1
    return [string]$obj.$Property
  } catch { return '(unavailable)' }
}

@(
  'OmenSuperHub resource baseline (read-only)'
  "Start: $($started.ToString('o'))"
  "Duration requested (s): $DurationSeconds"
  "Interval requested (s): $IntervalSeconds"
  "Process name: $ProcessName"
  "Logical processors: $logicalCpus"
  "Computer model: $(Safe-CimValue 'Win32_ComputerSystem' 'Model')"
  "BIOS version: $(Safe-CimValue 'Win32_BIOS' 'SMBIOSBIOSVersion')"
  "Windows version: $(Safe-CimValue 'Win32_OperatingSystem' 'Version')"
  'Interpretation: CpuPercentMachine = delta(process CPU seconds) / delta(wall seconds) / logical CPUs * 100.'
  'Missing process means not running; it does not mean 0% CPU.'
  'System/driver CPU cost is not attributed to this process by this script.'
) | Set-Content -Path $meta -Encoding UTF8

Write-Host "Sampling $ProcessName for $DurationSeconds s (every $IntervalSeconds s)."
Write-Host "Data: $csv"
Write-Host "Press Ctrl+C to stop early."

try {
  while ($startClock.Elapsed.TotalSeconds -lt $DurationSeconds) {
    $sampleWall = $startClock.Elapsed.TotalSeconds
    $now = (Get-Date).ToString('o')
    $procs = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) {
      $rows.Add([pscustomobject]@{
        Timestamp = $now; ElapsedSeconds = [math]::Round($sampleWall,3)
        PID = ''; Instance = 'not-running'; CpuPercentMachine = ''
        WorkingSetMB = ''; PrivateMemoryMB = ''; Handles = ''; Threads = ''
      })
    } else {
      foreach ($proc in $procs) {
        try {
          $pidValue = [int]$proc.Id
          $cpuSeconds = [double]$proc.TotalProcessorTime.TotalSeconds
          $startTicks = [string]$proc.StartTime.ToUniversalTime().Ticks
          # PID reuse must not be treated as continuous CPU history.
          $key = "$pidValue-$startTicks"
          $previous = $lastCpuByPid[$key]
          $cpuPct = $null
          if ($null -ne $previous) {
            $deltaTime = $sampleWall - [double]$previous.Wall
            $deltaCpu = $cpuSeconds - [double]$previous.Cpu
            if ($deltaTime -gt 0 -and $deltaCpu -ge 0) {
              $cpuPct = [math]::Round(($deltaCpu / $deltaTime / $logicalCpus) * 100,3)
            }
          }
          $lastCpuByPid[$key] = @{ Wall=$sampleWall; Cpu=$cpuSeconds }
          $processSeen[$key] = $true
          $rows.Add([pscustomobject]@{
            Timestamp = $now; ElapsedSeconds = [math]::Round($sampleWall,3)
            PID = $pidValue; Instance = $startTicks
            CpuPercentMachine = $cpuPct
            WorkingSetMB = [math]::Round($proc.WorkingSet64 / 1MB,2)
            PrivateMemoryMB = [math]::Round($proc.PrivateMemorySize64 / 1MB,2)
            Handles = $proc.HandleCount
            Threads = $proc.Threads.Count
          })
        } catch {
          Write-Warning "Unable to sample a process: $($_.Exception.Message)"
        } finally {
          if ($null -ne $proc) { $proc.Dispose() }
        }
      }
    }
    $remaining = $DurationSeconds - $startClock.Elapsed.TotalSeconds
    if ($remaining -le 0) { break }
    Start-Sleep -Milliseconds ([math]::Max(1, [math]::Min($IntervalSeconds * 1000, [int]($remaining * 1000))))
  }
} finally {
  $rows | Export-Csv -Path $csv -NoTypeInformation -Encoding UTF8
  Write-Host "Saved $($rows.Count) samples across $($processSeen.Count) process instances."
  Write-Host "CSV: $csv"
  Write-Host "Metadata: $meta"
  Write-Warning "Do not publish these raw files before reviewing machine details and paths."
}
