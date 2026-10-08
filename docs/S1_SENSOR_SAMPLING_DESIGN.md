# S1 温度采样与显示解耦：设计与安全约束

> 状态：技术方案，不是已实施。禁止因为修改了文档就声称完成风控解耦。
> 依据：`Program.cs` 中 `StartHardwareMonitor` / `QueryHardware` / `IsEmergencyThermalState` / `ApplyAutomaticFanControl`，`Program.Config.cs` 中 `GetFanSpeedForTemperature` / `ApplyPresetSettings`，`Program.Menu.cs` 中 CPU/GPU 监控项。

## 一、已经存在的保护

当前 `auto` 模式会阻止用户在 UI 直接关闭 CPU 采样，并在恢复配置时将 `monitorCPU` 强制恢复为 true。这避免了最明显的“两个监控都关、自动风控失明”问题；**不能据此认定 GPU 热源或异常恢复已得到完整保护**。

当前六项 `showCPU*` / `showGPU*` 布尔值控制悬浮与托盘的具体数据显示，它们与 `monitorCPU/monitorGPU` 的采集开关不同。必须保留此差别，不应把“隐藏数据显示”直接实现成发往 `--hwmonitor` 子进程的 `CPU:OFF/GPU:OFF`。

## 二、拆分后的状态与约束（待实现）

| 状态 | 含义 | 可由用户关闭？ |
|---|---|---|
| `DisplayCpuMetrics`, `DisplayGpuMetrics` | 仅控制 UI 渲染，与底层采样无关 | 是 |
| `SampleCpuForControl` | 软件自动风控下 CPU 安全采样 | 自动风控时否 |
| `SampleGpuForControl` | 当独显存在且平台需要 GPU 温度参与时的控制采样 | 不得默默关闭 |
| `DiagnosticCpu/GpuSampling` | 非自动风控时自愿启用的监控采集 | 是 |
| `TemperatureSnapshot` | Raw、Smoothed、UTC 时间、Valid、Source | 不适用 |
| `MonitorProcessState` | Starting / Running / Recovering / Stopped / Failed | 不适用 |

建议从已有 `show*` 状态迁移显示首选项，兼容既有注册表 `MonitorCPU/MonitorGPU`。不要静默重定义旧字段的含义，避免用户重启后监控和功耗状态变化。

## 三、事务顺序与失效行为

1. 先确定自动风控是否接管（`fanControl=="auto"`），再确定**有效采样集合**，最后将采样命令发给监控子进程。
2. 在显示菜单切换时，先更新 `Display*`；只有采样需求实际发生变化时才发送 `CPU:ON/OFF` / `GPU:ON/OFF`。应减少无谓重启与温度初始化抖动。
3. CPU/GPU 温度快照只在数值可信、时间戳未过期时有效，初始占位 50/40 不得混入有效数据。
4. 只要自动风控被启用且历史温度曾有效，全部采样源超时必须触发已有 failsafe，并明确来源/恢复状态。冷启动长时间没有第一份有效温度的行为需要单独安全设计，不能仅以噪音顾虑掩盖无可靠采样的风险。
5. 硬件控制由自动切到固定/最大，再返回自动时，**在启动采样且拿到新鲜数据前**不能基于旧温度发出正常自动曲线写入。
6. 独显低功耗/休眠不应强制唤醒来满足 UI 数据展示；GPU 热安全与省电之间需通过设备能力识别和实测确定，而不是简单把 `monitorGPU=true` 写死。
7. 子进程异常退出需检查重启是否与用户主动关闭、应用退出、旧 PID 的延迟回调冲突；不能无限快速自启。记录启停缘由与最近样本时间。

## 四、建议拆分次序及自动化验证

- 第一 PR：引入不改旧行为的纯函数 `ComputeEffectiveSamplingPolicy(display, controlMode, hardwareCapabilities)`，覆盖 auto / fixed / max / NVIDIA absent / low-power GPU / UI all hidden 等组合。
- 第二 PR：引入强类型 TemperatureSnapshot，先改显示路径，不更换风控输入，验证丢帧/过期与 0W/40°C 语义。
- 第三 PR：在单个入口统一向 `--hwmonitor` 下发状态；所有 fanControl/预设/启动/退出切换走同一采样协调函数。
- 第四 PR：控制器读取已校验快照，加入重启/冷启动/超时/恢复状态单测；控制器的高温保护优先，不降低采样频率。
- 每阶段 CI Release x64 + 纯逻辑单测。仅在上阶段实机热 UAT 达标后合并下一阶段风控影响代码。

## 五、风险排除条件

- 不依赖没有数据的“高温更容易/更不容易”主观判断调参。
- 不因降低监控进程 CPU 占用而停用自动风控安全温度源。
- 不将硬件读写失败表示为已成功应用。
- 不一次性迁移所有 `Program.*` 文件和菜单。
- 不将缺少一个平台温度源等价为整台机器没有风扇控制能力；按该机 BIOS 默认行为和实测确定兜底。

当前可交付内容包括现状确认、门禁、防重入、记录与纯策略测试。采样彻底解耦与状态机仍属**代码待办**，不能将其误列成 UAT 唯一剩余事项。
