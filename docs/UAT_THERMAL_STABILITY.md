# Thermal / Fan Stability UAT

This checklist is the hardware gate for PR #58. The goal is to validate behavior, not to tune the fan curve by feel.

## Test setup

- Use the same BIOS, Windows power plan and NVIDIA driver for the whole run.
- Keep HWiNFO (or another trusted hardware monitor) visible as the reference source.
- Record OmenSuperHub version/commit, machine model, BIOS version and ambient temperature.
- Start each scenario from a stable state for at least 2 minutes.
- Keep `OmenSuperHub.log` after each failed scenario.

## 1. Idle and light-load stability

1. Select the Balanced preset and automatic fan control.
2. Leave the machine idle for 5 minutes.
3. Open Explorer/browser/settings intermittently for another 5 minutes.
4. Observe CPU/GPU temperature and fan RPM.

Pass criteria:

- no 0°C or obviously invalid temperature samples;
- no repeated large fan-speed oscillation from short UI/application bursts;
- no max-fan failsafe while valid temperature telemetry is present;
- steady-state CPU/GPU temperature should normally remain close to the reference monitor (investigate a sustained difference greater than about 3°C before changing fan logic).

## 2. Sudden load response

1. Start from the idle state above.
2. Launch a CPU-heavy workload quickly.
3. Repeat with a GPU-heavy workload.
4. Repeat once with a combined CPU+GPU load.

Pass criteria:

- rising temperature causes an immediate fan response toward the calculated target;
- the controller does not wait for several smoothing intervals before responding to a real thermal rise;
- emergency behavior wins over acoustic/ramp-down limiting near the thermal limit;
- no UI freeze or persistent mouse stutter attributable to the fan-control transition.

## 3. Sustained load

1. Run a representative game or combined stress load for at least 10 minutes.
2. Capture HWiNFO maximum/average CPU and GPU temperature, package/GPU power and fan RPM.
3. Capture the same visible OmenSuperHub values.

Pass criteria:

- temperature telemetry remains available throughout the run;
- automatic fan control does not unexpectedly stop or fall to a low target while temperature is still high;
- no fan command exceeds the platform maximum detected by OmenSuperHub;
- no unexpected performance-mode or preset change occurs during the load.

## 4. Cooldown / ramp-down

1. End the sustained workload abruptly.
2. Observe fan behavior for 3-5 minutes.

Pass criteria:

- fan speed is allowed to fall gradually rather than oscillating rapidly;
- a new temperature rise interrupts ramp-down immediately;
- fans eventually settle at the expected curve value once temperature stabilizes.

## 5. Refresh-rate independence

Run the same short load once with the monitor refresh interval at 1 s and once at 250 ms.

Pass criteria:

- thermal/fan response is materially equivalent;
- changing UI/monitor refresh rate must not make the fan controller meaningfully more or less aggressive.

## 6. Suspend / resume

1. Enter sleep with Balanced + automatic fan control enabled.
2. Resume after at least 30 seconds.
3. Repeat once while the machine is warm.

Pass criteria:

- monitoring recovers without displaying 0°C as valid data;
- the selected preset and expected fan mode are restored;
- the fan controller does not remain disabled after resume.

## 7. AC disconnect / reconnect

1. With monitoring active, disconnect AC power.
2. Wait for state changes to settle.
3. Reconnect AC power.

Pass criteria:

- no crash or stuck hardware query;
- power/preset restoration does not leave the machine in an unexpected firmware mode;
- temperature and fan monitoring continue normally.

## 8. Fixed-RPM thermal protection

Use a conservative fixed-RPM value and a controlled workload. Do not intentionally drive the machine beyond normal thermal limits.

Pass criteria:

- approaching the configured CPU or GPU thermal limit triggers the protection path;
- protection covers both CPU and GPU temperature sources;
- returning to a safe thermal state does not leave the controller permanently stuck.

## 9. Temperature-source loss / failsafe

Only perform this scenario if a safe/reversible way to interrupt the monitor source is available on the test machine.

Pass criteria:

- a source that previously supplied valid data becoming stale/invalid is not converted to 0°C;
- if all previously working temperature sources are lost, max-fan failsafe is enabled;
- when valid temperature telemetry returns, failsafe is released and normal automatic control resumes.

## 10. Preset / firmware-mode persistence

Cycle through Balanced, Extreme, GPU Priority and Light Use where supported, then return to Balanced. Repeat once across sleep/resume.

Pass criteria:

- the selected preset matches the expected firmware performance mode;
- built-in presets do not silently apply GPU overclock offsets;
- unsupported controls remain unavailable instead of pretending to succeed.

## Failure capture

For any failure, record:

- scenario number and exact step;
- wall-clock time of the failure;
- OmenSuperHub commit SHA;
- machine model / BIOS / NVIDIA driver;
- HWiNFO CPU/GPU temperatures and power around the failure;
- fan RPM before/after;
- `OmenSuperHub.log`;
- whether the issue reproduces on current `master`.

Do not tune smoothing, deadband or fan-curve values until the failing scenario is reproducible and the telemetry/logs identify which layer is wrong.
