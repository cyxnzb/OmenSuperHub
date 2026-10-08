# Windows PowerShell / PowerShell 7: dependency-free policy regression check.
$ErrorActionPreference = 'Stop'
$sourcePath = Join-Path $PSScriptRoot '..\FanControlPolicy.cs'
Add-Type -Path $sourcePath -ErrorAction Stop

function AssertNext([int]$target, [int]$current, [bool]$emergency, [int]$expected, [string]$label) {
  $actual = [OmenSuperHub.FanControlPolicy]::CalculateNextOrSkip($target, $current, $emergency)
  if ($actual -ne $expected) {
    throw ("{0}: next fan level was {1}; expected {2}" -f $label, $actual, $expected)
  }
  Write-Host ("PASS {0}: {1}" -f $label, $actual)
}

AssertNext 55 30 $false 55 'heat-up is immediate'
AssertNext 40 60 $false 58 'cool-down limited to 200 RPM'
AssertNext 59 60 $false -1 'deadband ignores 100 RPM reduction'
AssertNext 62 60 $false -1 'deadband ignores 200 RPM increase'
AssertNext 63 60 $false 63 'outside deadband increases immediately'
AssertNext 60 60 $true 60 'emergency bypasses deadband'
AssertNext 40 60 $true 40 'emergency bypasses slow cool-down'
AssertNext 999 250 $false 255 'target is clamped to device command range'
AssertNext -10 5 $false 3 'negative target is clamped'
Write-Host 'All fan ramp policy checks passed.'


function AssertBaseline([int]$requested, [int]$observed, [int]$expected, [string]$label) {
  $actual = [OmenSuperHub.FanControlPolicy]::SelectControlBaseline($requested, $observed)
  if ($actual -ne $expected) { throw ("{0}: {1} expected {2}" -f $label, $actual, $expected) }
  Write-Host ("PASS {0}: {1}" -f $label, $actual)
}
AssertBaseline -1 20 20 'first tick uses observed RPM'
AssertBaseline 100 20 100 'EC lag cannot force another ramp-up'
AssertBaseline 100 250 100 'observed overshoot does not change command baseline'
AssertBaseline 1000 20 255 'requested baseline is clamped'
