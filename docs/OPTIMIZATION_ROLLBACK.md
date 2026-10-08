# 优化分支回归与回退手册（2026-10）

本文件适用于 OmenSuperHub fork 的逐项优化。**PR 创建、CI 通过和部署到真实 OMEN 设备是不同阶段**；涉及自动风扇/温控时，实机 UAT 前不得视为正式交付。

## 1. 先记录当前版本

在 Windows 开发机器记录：

```powershell
git rev-parse HEAD
git status --short
```

备份当前用户配置、日志以及自己的风扇曲线（如有），但**不要**把注册表导出、机型标识、日志或自定义敏感路径直接上传公开 PR。记录 Windows/BIOS/PawnIO/GPU 驱动版本和供电状态。不要同时运行 OSH 与其他可能接管风扇的软件。

## 2. PR 验证边界

| PR | 非实机检查 | 实机 UAT 重点 | 回滚触发条件 |
|---|---|---|---|
| [#69](https://github.com/cyxnzb/OmenSuperHub/pull/69) | Windows 脚本冒烟、文档和 .gitignore | 采样器真实进程识别/成本；热基线 | 脚本影响运行、占用过高或输出不正确 |
| [#70](https://github.com/cyxnzb/OmenSuperHub/pull/70) | Release x64 CI 编译、补丁只限自动风控回调 | 高温突发、连续负载、慢 WMI、休眠恢复/监控丢失 | 升温响应劣化、失控、长时间阻塞或无法恢复 BIOS |
| [#71](https://github.com/cyxnzb/OmenSuperHub/pull/71) | Release x64 CI 编译、提示文案路径核对 | GPU 休眠/唤醒、样本超时及真实 0W | 有效数据被误隐藏、状态误导 |
| [#72](https://github.com/cyxnzb/OmenSuperHub/pull/72) | Release x64 CI 编译、中英繁语义 | 设置面板文案正确，切换/重启仍真实 | 误表述为已经生效 |
| [#73](https://github.com/cyxnzb/OmenSuperHub/pull/73) | Release x64 CI 编译、顶层菜单顺序核对 | 菜单勾选、鼠标操作、DPI 与语言切换 | 预设、风扇等菜单不可操作 |

**注意**：#70 对阻塞的 BIOS/WMI 回调使用非重入门禁，但不应视为拥有超时取消能力。单次控制调用卡死仍有安全风险。优先保证已有 BIOS 安全兜底和可逆的运行方式。

## 3. 验证矩阵（必须写结果，不可凭感觉标记通过）

- 启动、退出、异常关闭、配置恢复；
- 预设切换、CPU/GPU 控制能力不可用时的安全降级；
- 自动/固定/最大风扇切换及高温保护；
- CPU/GPU 实时温度与 HWiNFO 或等效参考对照（传感器必须一致）；
- GPU 休眠/唤醒、AC 插拔、睡眠恢复；
- 悬浮显示与托盘状态、低/高刷新频率；
- 简体、繁体、英文，以及 100% / 125% / 150% / 200% DPI；
- 对 S0 CPU/内存、句柄、温控曲线和 WMI 时延的同条件回归。

## 4. 可逆合并流程

建议每次合并**一个** PR，完成实际硬件回归后再合并下一个。合并前使用 GitHub PR 页面查看最新 CI 与对比差异。

如某次合并导致回归，首选在 GitHub 中对**对应的合并提交**使用 Revert 创建独立恢复 PR。不要 `git reset --hard` 或强推多人共享的 `master`。若原 PR 是 squash merge，则 revert squash commit；若包含后续依赖补丁，应先分析依赖关系再回退。回滚后再次运行 Release x64 CI、热安全 UAT 与设置迁移测试。

## 5. 未完成项不是 UAT 的替代项

- 监控采样与显示彻底解耦、监控子进程的有界重启；
- 遥测统一有效性模型和硬件设置读回状态机；
- 以真实基线驱动的轮询/性能优化；
- 完整信息架构简化、高 DPI 系统性修复；
- 核心逻辑抽离与确定性单测。

这些项需要进一步代码设计与测试；**不能仅因为等待实机 UAT，就将其标为已完成**。
