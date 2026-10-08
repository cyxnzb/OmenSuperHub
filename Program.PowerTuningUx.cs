using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Forms;
using static OmenSuperHub.GpuAppManager;
using static OmenSuperHub.OmenHardware;

namespace OmenSuperHub {
  static partial class Program {
    private const string PowerTuningMenuName = "PowerTuningUxMenu";
    private const string PowerTuningSeparatorName = "PowerTuningUxSeparator";

    private static readonly bool powerTuningUxBootstrap = InitializePowerTuningUx();
    private static bool powerTuningContextHooked = false;
    private static bool powerTuningGpuLimitQueryStarted = false;
    private static float? powerTuningGpuMaxW = null;

    private static ToolStripMenuItem powerTuningMenu;
    private static ToolStripMenuItem powerTuningSummaryItem;
    private static ToolStripMenuItem powerTuningCpuItem;
    private static ToolStripMenuItem powerTuningGpuFullItem;
    private static ToolStripMenuItem powerTuningTppItem;
    private static ToolStripMenuItem powerTuningHighPerformanceItem;
    private static ToolStripMenuItem powerTuningPersistenceNoteItem;

    private static bool InitializePowerTuningUx() {
      Application.Idle += PowerTuningOnApplicationIdle;
      return true;
    }

    private static void PowerTuningOnApplicationIdle(object sender, EventArgs e) {
      if (powerTuningContextHooked || trayIcon == null || trayIcon.ContextMenuStrip == null)
        return;

      Application.Idle -= PowerTuningOnApplicationIdle;
      trayIcon.ContextMenuStrip.Opening += PowerTuningContextMenuOpening;
      powerTuningContextHooked = true;
      EnsurePowerTuningMenu();
    }

    private static void PowerTuningContextMenuOpening(object sender, CancelEventArgs e) {
      EnsurePowerTuningMenu();
    }

    private static void EnsurePowerTuningMenu() {
      if (performanceControlMenu == null || performanceControlMenu.IsDisposed)
        return;

      ToolStripItem existing = null;
      foreach (ToolStripItem item in performanceControlMenu.DropDownItems) {
        if (item.Name == PowerTuningMenuName) {
          existing = item;
          break;
        }
      }

      if (existing is ToolStripMenuItem existingMenu) {
        powerTuningMenu = existingMenu;
      } else {
        powerTuningMenu = BuildPowerTuningMenu();
        performanceControlMenu.DropDownItems.Insert(0, powerTuningMenu);

        var separator = new ToolStripSeparator { Name = PowerTuningSeparatorName };
        performanceControlMenu.DropDownItems.Insert(1, separator);
      }

      RefreshPowerTuningText();
      BeginGpuPowerLimitQuery();
    }

    private static ToolStripMenuItem BuildPowerTuningMenu() {
      var menu = new ToolStripMenuItem { Name = PowerTuningMenuName };

      powerTuningSummaryItem = new ToolStripMenuItem { Enabled = false };
      powerTuningCpuItem = new ToolStripMenuItem();
      powerTuningGpuFullItem = new ToolStripMenuItem();
      powerTuningTppItem = new ToolStripMenuItem();
      powerTuningHighPerformanceItem = new ToolStripMenuItem();
      powerTuningPersistenceNoteItem = new ToolStripMenuItem { Enabled = false };

      powerTuningCpuItem.Click += (s, e) => ShowCpuPowerLimitDialog();
      powerTuningGpuFullItem.Click += (s, e) => ApplyGpuFullPower();
      powerTuningTppItem.Click += (s, e) => ShowDynamicBoostThresholdDialog();
      powerTuningHighPerformanceItem.Click += (s, e) => ApplyHighPerformanceBase();
      menu.DropDownOpening += (s, e) => {
        RefreshPowerTuningText();
        BeginGpuPowerLimitQuery();
      };

      menu.DropDownItems.Add(powerTuningSummaryItem);
      menu.DropDownItems.Add(new ToolStripSeparator());
      menu.DropDownItems.Add(powerTuningCpuItem);
      menu.DropDownItems.Add(powerTuningGpuFullItem);
      menu.DropDownItems.Add(powerTuningTppItem);
      menu.DropDownItems.Add(new ToolStripSeparator());
      menu.DropDownItems.Add(powerTuningHighPerformanceItem);
      menu.DropDownItems.Add(new ToolStripSeparator());
      menu.DropDownItems.Add(powerTuningPersistenceNoteItem);

      return menu;
    }

    private static string PowerTuningText(string simplifiedChinese, string traditionalChinese, string english) {
      switch (Strings.Current) {
        case AppLanguage.TraditionalChinese:
          return traditionalChinese;
        case AppLanguage.English:
          return english;
        default:
          return simplifiedChinese;
      }
    }

    private static int GetSimpleCpuPowerMaximum() {
      if (platformSettings != null && platformSettings.NbPL1UpperBoundPerformance > 0)
        return Math.Max(10, Math.Min(254, platformSettings.NbPL1UpperBoundPerformance));
      return 254;
    }

    private static int GetCurrentCpuPowerForUi() {
      if (TryParseWattSetting(cpuPower, 10, out int current))
        return Math.Max(10, Math.Min(GetSimpleCpuPowerMaximum(), current));

      if (platformSettings != null && platformSettings.NbPL1UpperBoundDefault > 0)
        return Math.Max(10, Math.Min(GetSimpleCpuPowerMaximum(), platformSettings.NbPL1UpperBoundDefault));

      return Math.Min(55, GetSimpleCpuPowerMaximum());
    }

    private static int GetCurrentTppForUi() {
      int maximum = Math.Max(20, GetSimpleCpuPowerMaximum());
      if (TryParseWattSetting(tppPower, 20, out int current))
        return Math.Max(20, Math.Min(maximum, current));

      int firmwareDefault = GetDefaultConcurrentTdp();
      if (firmwareDefault >= 20)
        return Math.Max(20, Math.Min(maximum, firmwareDefault));

      return Math.Min(60, maximum);
    }

    private static void RefreshPowerTuningText() {
      if (powerTuningMenu == null || powerTuningMenu.IsDisposed)
        return;

      powerTuningMenu.Text = PowerTuningText("功耗调校", "功耗調校", "Power Tuning");

      string cpuText = TryParseWattSetting(cpuPower, 10, out int cpuWatts)
        ? $"{cpuWatts}W"
        : PowerTuningText("平台默认", "平台預設", "Platform default");

      bool gpuFull = hasNVIDIAGpu && tgpPower == "on" && ppabPower == "on" && dState == "normal";
      string gpuText;
      if (!hasNVIDIAGpu) {
        gpuText = PowerTuningText("无独立 NVIDIA GPU", "無獨立 NVIDIA GPU", "No NVIDIA dGPU");
      } else if (gpuFull && powerTuningGpuMaxW.HasValue) {
        gpuText = PowerTuningText($"完整释放 / 最高 {powerTuningGpuMaxW.Value:F0}W", $"完整釋放 / 最高 {powerTuningGpuMaxW.Value:F0}W", $"Full / up to {powerTuningGpuMaxW.Value:F0}W");
      } else if (gpuFull) {
        gpuText = PowerTuningText("完整释放", "完整釋放", "Full power");
      } else {
        gpuText = PowerTuningText("自定义/受限", "自訂/受限", "Custom/limited");
      }

      string tppText = TryParseWattSetting(tppPower, 20, out int tppWatts)
        ? $"{tppWatts}W"
        : PowerTuningText("平台默认", "平台預設", "Platform default");

      if (powerTuningSummaryItem != null)
        powerTuningSummaryItem.Text = PowerTuningText(
          $"请求设置（未验证生效）：CPU ≤ {cpuText} · GPU {gpuText} · DB阈值 {tppText}",
          $"要求設定（未驗證生效）：CPU ≤ {cpuText} · GPU {gpuText} · DB閾值 {tppText}",
          $"Requested (not verified): CPU ≤ {cpuText} · GPU {gpuText} · DB threshold {tppText}");

      if (powerTuningCpuItem != null) {
        powerTuningCpuItem.Text = PowerTuningText(
          $"CPU 功耗上限…（平台建议 ≤ {GetSimpleCpuPowerMaximum()}W）",
          $"CPU 功耗上限…（平台建議 ≤ {GetSimpleCpuPowerMaximum()}W）",
          $"CPU power limit… (platform ≤ {GetSimpleCpuPowerMaximum()}W)");
        powerTuningCpuItem.Enabled = isCPUPowerControlSupported;
      }

      if (powerTuningGpuFullItem != null) {
        string maxSuffix = powerTuningGpuMaxW.HasValue ? $" {powerTuningGpuMaxW.Value:F0}W" : "";
        powerTuningGpuFullItem.Text = PowerTuningText(
          $"GPU 完整释放{maxSuffix}（TGP + PPAB）",
          $"GPU 完整釋放{maxSuffix}（TGP + PPAB）",
          $"GPU full power{maxSuffix} (TGP + PPAB)");
        powerTuningGpuFullItem.Enabled = hasNVIDIAGpu;
        powerTuningGpuFullItem.Checked = gpuFull;
      }

      if (powerTuningTppItem != null) {
        powerTuningTppItem.Text = PowerTuningText(
          $"Dynamic Boost CPU 阈值…（当前 {tppText}）",
          $"Dynamic Boost CPU 閾值…（目前 {tppText}）",
          $"Dynamic Boost CPU threshold… (current {tppText})");
        powerTuningTppItem.Enabled = platformSettings != null && platformSettings.TppSupport;
        powerTuningTppItem.ToolTipText = PowerTuningText(
          "CPU 实际功耗低于此值时，GPU 才能获得额外 PPAB/DB 功耗；它不是 GPU 目标瓦数。",
          "CPU 實際功耗低於此值時，GPU 才能獲得額外 PPAB/DB 功耗；它不是 GPU 目標瓦數。",
          "When actual CPU power is below this threshold, the GPU may receive extra PPAB/DB power. This is not a GPU wattage target.");
      }

      if (powerTuningHighPerformanceItem != null) {
        powerTuningHighPerformanceItem.Text = PowerTuningText(
          "切换到极致性能基础（Performance BIOS + GPU完整释放）",
          "切換到極致效能基礎（Performance BIOS + GPU完整釋放）",
          "Switch to Extreme base (Performance BIOS + GPU full power)");
        powerTuningHighPerformanceItem.Enabled = platformSettings != null || hasNVIDIAGpu;
      }

      if (powerTuningPersistenceNoteItem != null)
        powerTuningPersistenceNoteItem.Text = PowerTuningText(
          "提示：内置预设上的手动调校会在下次切换预设时重置；长期保存请使用自定义预设。",
          "提示：內建預設上的手動調校會在下次切換預設時重設；長期保存請使用自訂預設。",
          "Note: manual tuning on built-in presets resets on the next preset switch. Use a custom preset to keep it.");
    }

    private static void BeginGpuPowerLimitQuery() {
      if (!hasNVIDIAGpu || powerTuningGpuLimitQueryStarted)
        return;

      powerTuningGpuLimitQueryStarted = true;
      Task.Run(() => {
        try {
          float[] limits = GetGpuPowerLimits();
          if (limits != null && limits.Length > 1 && limits[1] > 0)
            powerTuningGpuMaxW = limits[1];
        } catch (Exception ex) {
          Logger.Error($"Power tuning GPU limit query failed: {ex.Message}");
        }

        if (uiContext != null)
          uiContext.Post(_ => RefreshPowerTuningText(), null);
      });
    }

    private static void ShowCpuPowerLimitDialog() {
      if (!isCPUPowerControlSupported)
        return;

      int maximum = GetSimpleCpuPowerMaximum();
      int current = GetCurrentCpuPowerForUi();
      string description = PowerTuningText(
        $"设置 CPU 持续/短时功耗上限（当前实现同时写入 PL1/PL2）。平台报告的性能上限为 {maximum}W；更高数值通常不会带来额外性能。",
        $"設定 CPU 持續/短時功耗上限（目前實作同時寫入 PL1/PL2）。平台回報的效能上限為 {maximum}W；更高數值通常不會帶來額外效能。",
        $"Set the CPU sustained/short power limit (current implementation writes both PL1/PL2). The platform reports a performance limit of {maximum}W; higher values usually add no performance.");

      if (!ShowWattDialog(PowerTuningText("CPU 功耗上限", "CPU 功耗上限", "CPU Power Limit"), description, 10, maximum, current, out int value))
        return;

      cpuPower = $"{value} W";
      ApplyHardwareSettingNow("cpuPower", () => {
        SetCpuPowerLimit((byte)value);
        SaveConfig("CpuPower");
      });
      SyncCpuPowerUi(value);
      UpdateCheckedState("cpuPowerGroup", Strings.SetCpuPowerSlider);
      RefreshPowerTuningText();
    }

    private static void ShowDynamicBoostThresholdDialog() {
      if (platformSettings == null || !platformSettings.TppSupport)
        return;

      int maximum = Math.Max(20, GetSimpleCpuPowerMaximum());
      int current = GetCurrentTppForUi();
      string description = PowerTuningText(
        "这是 Dynamic Boost / PPAB 的 CPU 功耗触发阈值：CPU 实际功耗低于这个值时，GPU 才能获得额外功耗。阈值越高越偏向 GPU。它不是 GPU 的目标瓦数。",
        "這是 Dynamic Boost / PPAB 的 CPU 功耗觸發閾值：CPU 實際功耗低於這個值時，GPU 才能獲得額外功耗。閾值越高越偏向 GPU。它不是 GPU 的目標瓦數。",
        "This is the CPU-power trigger threshold for Dynamic Boost / PPAB. The GPU can receive extra power when actual CPU power is below it. A higher threshold favors the GPU. It is not the GPU wattage target.");

      if (!ShowWattDialog(PowerTuningText("Dynamic Boost CPU 阈值", "Dynamic Boost CPU 閾值", "Dynamic Boost CPU Threshold"), description, 20, maximum, current, out int value))
        return;

      tppPower = $"{value} W";
      ApplyHardwareSettingNow("tppPower", () => {
        SetConcurrentTdp((byte)value);
        SaveConfig("TppPower");
      });
      SyncTppUi(value);
      UpdateCheckedState("tppPowerGroup", Strings.SetTppSlider);
      RefreshPowerTuningText();
    }

    private static bool ShowWattDialog(string title, string description, int minimum, int maximum, int current, out int value) {
      value = current;
      using (var form = new Form())
      using (var descriptionLabel = new Label())
      using (var valueSelector = new NumericUpDown())
      using (var unitLabel = new Label())
      using (var okButton = new Button())
      using (var cancelButton = new Button()) {
        form.Text = title;
        form.Width = 520;
        form.Height = 250;
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.MaximizeBox = false;
        form.MinimizeBox = false;
        form.StartPosition = FormStartPosition.CenterScreen;

        descriptionLabel.Left = 12;
        descriptionLabel.Top = 12;
        descriptionLabel.Width = 480;
        descriptionLabel.Height = 82;
        descriptionLabel.Text = description;

        valueSelector.Left = 120;
        valueSelector.Top = 105;
        valueSelector.Width = 170;
        valueSelector.Minimum = minimum;
        valueSelector.Maximum = Math.Max(minimum, maximum);
        valueSelector.Increment = 5;
        valueSelector.Value = Math.Max(minimum, Math.Min(maximum, current));

        unitLabel.Left = 300;
        unitLabel.Top = 108;
        unitLabel.Width = 60;
        unitLabel.Text = "W";

        okButton.Text = PowerTuningText("应用", "套用", "Apply");
        okButton.Left = 145;
        okButton.Top = 150;
        okButton.Width = 100;
        okButton.DialogResult = DialogResult.OK;

        cancelButton.Text = Strings.Cancel;
        cancelButton.Left = 255;
        cancelButton.Top = 150;
        cancelButton.Width = 100;
        cancelButton.DialogResult = DialogResult.Cancel;

        form.Controls.Add(descriptionLabel);
        form.Controls.Add(valueSelector);
        form.Controls.Add(unitLabel);
        form.Controls.Add(okButton);
        form.Controls.Add(cancelButton);
        form.AcceptButton = okButton;
        form.CancelButton = cancelButton;

        if (form.ShowDialog() != DialogResult.OK)
          return false;

        value = (int)valueSelector.Value;
        return true;
      }
    }

    private static void ApplyGpuFullPower() {
      if (!hasNVIDIAGpu)
        return;

      ApplyGpuFullPowerCore();
      RefreshPowerTuningText();
    }

    private static void ApplyGpuFullPowerCore() {
      tgpPower = "on";
      ppabPower = "on";
      dState = "normal";
      SetGpuPowerState(true, true, 1);
      SaveConfig("TgpPower");
      SaveConfig("PpabPower");
      SaveConfig("DState");
      UpdateCheckedState("tgpPowerGroup", Strings.Enable);
      UpdateCheckedState("ppabPowerGroup", Strings.Enable);
      UpdateCheckedState("dStateGroup", Strings.Standard);
    }

    private static void ApplyHighPerformanceBase() {
      try {
        if (platformSettings != null) {
          // Reuse the existing preset state machine so the UI, persisted preset and
          // firmware performance mode stay aligned. The user can then lower CPU power
          // from the simple control (for example to 130W) without bypassing #58 logic.
          applyPresetLogic("PresetExtreme");
        } else if (hasNVIDIAGpu) {
          ApplyGpuFullPowerCore();
        }
      } catch (Exception ex) {
        Logger.Error($"Apply Extreme power base failed: {ex.Message}");
      }
      RefreshPowerTuningText();
    }

    private static void SyncCpuPowerUi(int value) {
      if (cpuPowerTrackBar == null)
        return;

      suppressPerformanceSliderEvents = true;
      try {
        cpuPowerTrackBar.Value = Math.Max(cpuPowerTrackBar.Minimum, Math.Min(cpuPowerTrackBar.Maximum, value));
        if (cpuPowerValueLabel != null)
          cpuPowerValueLabel.Text = string.Format(Strings.CurrentSliderValueTemp, $"{value} W");
      } finally {
        suppressPerformanceSliderEvents = false;
      }
    }

    private static void SyncTppUi(int value) {
      if (tppTrackBar == null)
        return;

      suppressPerformanceSliderEvents = true;
      try {
        tppTrackBar.Value = Math.Max(tppTrackBar.Minimum, Math.Min(tppTrackBar.Maximum, value));
        if (tppValueLabel != null)
          tppValueLabel.Text = string.Format(Strings.CurrentSliderValueTemp, $"{value} W");
      } finally {
        suppressPerformanceSliderEvents = false;
      }
    }
  }
}
