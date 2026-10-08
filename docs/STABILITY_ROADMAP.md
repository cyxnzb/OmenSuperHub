# Stability Roadmap

> Historical stability phase plan. For the current 2026-10-08 audit, five-dimension priorities, acceptance gates and next tasks, see [`PROJECT_STATUS_AND_OPTIMIZATION_PLAN.md`](PROJECT_STATUS_AND_OPTIMIZATION_PLAN.md).

This roadmap intentionally prioritizes predictable hardware behavior over new features.

## Phase A — PR #58 hardware gate

Status: merged into `master` via PR #58 on 2026-09-30. The merge commit reports local hardware UAT, but this remains a continuing regression gate across BIOS/hardware combinations; it is **not** proof of universal compatibility.

Scope:

- temperature validity and freshness;
- fan ramp-up / ramp-down behavior;
- emergency and sensor-loss failsafes;
- fixed-RPM over-temperature protection;
- preset / firmware-mode alignment;
- suspend/resume and AC reconnect behavior.

Rule: do not tune fan constants based only on subjective impressions. Reproduce the issue, compare against a reference monitor and keep the failure log.

## Phase B — Stability foundation

Goal: failures in one subsystem must not freeze or corrupt unrelated behavior.

Planned order:

1. bounded/structured diagnostics and repository hygiene;
2. replace unbounded synchronous external-command execution with a timeout-aware process runner;
3. return explicit success/unsupported/timeout/error results from hardware operations where practical;
4. separate hardware sampling, fan control, UI refresh, DB unlock and resume recovery lifecycles;
5. add deterministic tests for hardware-independent control logic.

Non-goals:

- no fan-algorithm rewrite;
- no visual redesign;
- no new tuning controls;
- no runtime/framework migration.

## Phase C — Service boundaries

Extract behavior incrementally while keeping existing UI paths compatible:

- `OmenBiosTransport` / `HardwareControlService`;
- `HardwareMonitorService`;
- `FanControlService`;
- `PresetService`;
- `GpuService`;
- `LightingService`;
- `AppSettingsService`.

The UI layer should eventually decide presentation only; it should not encode BIOS payloads, own monitor process lifetimes or perform direct registry/hardware work.

## Phase D — Privilege and startup model

Review the current SYSTEM boot task + elevated tray process + relaunch behavior.

Long-term target if compatibility permits:

- elevated hardware agent/service;
- normal-user tray/UI process;
- narrow IPC contract between them;
- no need to keep the complete UI permanently elevated.

This phase must be compatibility-tested across supported OMEN generations before replacing the current startup mechanism.

## Phase E — User experience

Keep the default path simple:

- presets;
- fan mode;
- monitoring;
- clearly separated advanced tuning.

Low-level controls such as IccMax, AC Load Line, DB state and overclocking should remain explicitly advanced and capability-gated.

## Phase F — New features

Only after the stability/service work is healthy should the project reconsider feature requests such as additional monitoring metrics, FPS monitoring or broader tuning controls.

## Engineering rules

- unsupported/unknown capability must not be treated as success;
- failed telemetry must not be represented as a valid zero;
- desired configuration and effective hardware state are different concepts;
- hardware writes should be observable in logs and, where practical, verified by read-back;
- a diagnostic/logging failure must never terminate hardware control;
- CI build success is necessary but is not hardware validation.
