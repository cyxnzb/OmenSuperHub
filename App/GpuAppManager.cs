using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;
using NvAPIWrapper;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;

namespace OmenSuperHub {
  public static class GpuAppManager {
    public class GpuAppInfo {
      public int ProcessId { get; set; }
      public string ProcessName { get; set; }
    }

    public static void SetCoreClockOffset(int offsetMHz) {
      NVIDIA.Initialize();

      try {
        PhysicalGPU[] gpus = PhysicalGPU.GetPhysicalGPUs();
        if (gpus.Length == 0)
          throw new Exception("未找到 GPU");

        PhysicalGPU gpu = gpus[0];

        // 构造时钟偏移条目，单位 kHz
        var clockDelta = new PerformanceStates20ClockEntryV1(
            PublicClockDomain.Graphics,
            new PerformanceStates20ParameterDelta(offsetMHz * 1000)
        );

        // 第三个参数类型是 PerformanceStates20BaseVoltageEntryV1[]
        var pState = new PerformanceStates20InfoV1.PerformanceState20(
            PerformanceStateId.P0_3DPerformance,
            new PerformanceStates20ClockEntryV1[] { clockDelta },
            new PerformanceStates20BaseVoltageEntryV1[0]
        );

        // clocksCount=1，baseVoltagesCount=0
        var writeInfo = new PerformanceStates20InfoV1(
            new PerformanceStates20InfoV1.PerformanceState20[] { pState },
            1u,
            0u
        );

        GPUApi.SetPerformanceStates20(gpu.Handle, writeInfo);
      } finally {
        NVIDIA.Unload();
      }
    }

    public static void SetMemoryClockOffset(int offsetMHz) {
      NVIDIA.Initialize();
      try {
        PhysicalGPU[] gpus = PhysicalGPU.GetPhysicalGPUs();
        if (gpus.Length == 0)
          throw new InvalidOperationException("未找到 GPU");
        PhysicalGPU gpu = gpus[0];

        var clockDelta = new PerformanceStates20ClockEntryV1(
            PublicClockDomain.Memory,                              // 显存时钟域
            new PerformanceStates20ParameterDelta(offsetMHz * 1000)
        );

        var pState = new PerformanceStates20InfoV1.PerformanceState20(
            PerformanceStateId.P0_3DPerformance,
            new PerformanceStates20ClockEntryV1[] { clockDelta },
            new PerformanceStates20BaseVoltageEntryV1[0]
        );

        var writeInfo = new PerformanceStates20InfoV1(
            new PerformanceStates20InfoV1.PerformanceState20[] { pState },
            1u,
            0u
        );

        GPUApi.SetPerformanceStates20(gpu.Handle, writeInfo);
      } finally { NVIDIA.Unload(); }
    }

    public static int GetCoreClockOffset() {
      NVIDIA.Initialize();
      try {
        PhysicalGPU[] gpus = PhysicalGPU.GetPhysicalGPUs();
        if (gpus.Length == 0) return 0;
        PhysicalGPU gpu = gpus[0];
        var pstatesInfo = GPUApi.GetPerformanceStates20(gpu.Handle);

        // 从 Clocks 字典中获取 P0 状态的时钟条目数组
        if (pstatesInfo.Clocks.TryGetValue(PerformanceStateId.P0_3DPerformance, out var clockEntries)) {
          foreach (var clock in clockEntries) {
            if (clock.DomainId == PublicClockDomain.Graphics) {
              // FrequencyDeltaInkHz.DeltaValue 即为当前偏移量（单位 kHz）
              return clock.FrequencyDeltaInkHz.DeltaValue / 1000; // 转为 MHz
            }
          }
        }
        return 0;
      } finally {
        NVIDIA.Unload();
      }
    }

    public static int GetMemoryClockOffset() {
      NVIDIA.Initialize();
      try {
        PhysicalGPU[] gpus = PhysicalGPU.GetPhysicalGPUs();
        if (gpus.Length == 0) return 0;
        PhysicalGPU gpu = gpus[0];
        var pstatesInfo = GPUApi.GetPerformanceStates20(gpu.Handle);

        if (pstatesInfo.Clocks.TryGetValue(PerformanceStateId.P0_3DPerformance, out var clockEntries)) {
          foreach (var clock in clockEntries) {
            if (clock.DomainId == PublicClockDomain.Memory) {
              return clock.FrequencyDeltaInkHz.DeltaValue / 1000;
            }
          }
        }
        return 0;
      } finally {
        NVIDIA.Unload();
      }
    }

    public static int GetGraphicsBoostClock() {
      try {
        PhysicalGPU[] gpus = PhysicalGPU.GetPhysicalGPUs();

        if (gpus.Length == 0)
          return 0;

        PhysicalGPU gpu = gpus[0];

        var info = GPUApi.GetAllClockFrequencies(
            gpu.Handle,
            new ClockFrequenciesV2(ClockType.BoostClock));

        foreach (var kvp in info.Clocks) {
          if (kvp.Key == PublicClockDomain.Graphics &&
              kvp.Value.IsPresent) {
            return (int)(kvp.Value.Frequency / 1000);
          }
        }
      } catch (Exception ex) {
        Logger.Warn($"GetGraphicsBoostClock failed: {ex.Message}");
      }

      return 0;
    }

    public static int GetMemoryBoostClock() {
      try {
        PhysicalGPU[] gpus = PhysicalGPU.GetPhysicalGPUs();

        if (gpus.Length == 0)
          return 0;

        PhysicalGPU gpu = gpus[0];

        var info = GPUApi.GetAllClockFrequencies(
            gpu.Handle,
            new ClockFrequenciesV2(ClockType.BoostClock));

        foreach (var kvp in info.Clocks) {
          if (kvp.Key == PublicClockDomain.Memory &&
              kvp.Value.IsPresent) {
            return (int)(kvp.Value.Frequency / 1000);
          }
        }
      } catch (Exception ex) {
        Logger.Warn($"GetMemoryBoostClock failed: {ex.Message}");
      }

      return 0;
    }

    public static List<GpuAppInfo> GetGpuApps() {
      var apps = new List<GpuAppInfo>();
      try {
        // 直接构建命令字符串
        string command = "nvidia-smi --query-compute-apps=pid,process_name --format=csv,noheader";
        ProcessResult result = ExecuteCommand(command, NvidiaQueryTimeoutMilliseconds);

        if (result.ExitCode == 0) {
          string[] lines = result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
          foreach (string line in lines) {
            string[] parts = line.Split(',');
            if (parts.Length >= 2 && int.TryParse(parts[0].Trim(), out int pid)) {
              apps.Add(new GpuAppInfo {
                ProcessId = pid,
                ProcessName = parts[1].Trim()
              });
            }
          }
        }
      } catch (Exception ex) {
        Logger.Warn($"GetGpuApps failed: {ex.Message}");
      }
      return apps;
    }

    public static void RestartGpu() {
      try {
        // 1. WMI 查询获取 NVIDIA 显卡的 PNPDeviceID（保持不变）
        string instanceId = null;
        string query = "SELECT * FROM Win32_PnPEntity WHERE PNPClass = 'Display'";
        using (var searcher = new System.Management.ManagementObjectSearcher(query)) {
          foreach (System.Management.ManagementObject device in searcher.Get()) {
            string description = device["Description"]?.ToString();
            if (!string.IsNullOrEmpty(description) &&
                description.IndexOf("nvidia", StringComparison.OrdinalIgnoreCase) >= 0) {
              instanceId = device["PNPDeviceID"]?.ToString();
              break;
            }
          }
        }

        if (string.IsNullOrEmpty(instanceId)) {
          MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.DeviceNotFound, Strings.Hint, MessageBoxButtons.OK, MessageBoxIcon.Warning);
          return;
        }

        // 2. 通过 ExecuteCommand 执行 pnputil 重启设备
        string command = $"pnputil /restart-device \"{instanceId}\"";
        ProcessResult result = ExecuteCommand(command);

        // 可选：根据结果给出提示
        if (result.ExitCode != 0) {
          MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), $"{Strings.RestartGPUFailed} {Strings.Error}：{result.Error}", Strings.Hint, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
      } catch (Exception ex) {
        Logger.Error($"RestartGpu failed: {ex.Message}");
        MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.RestartGPUFailed, Strings.Hint, MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
    }

    /// <summary>
    /// 获取所有显卡名称列表（跳过 Microsoft 基本显示适配器）
    /// </summary>
    public static List<string> GetAllGpuNamesList() {
      var gpuNames = new List<string>();
      try {
        // 增加查询 PNPDeviceID 字段
        using (var searcher = new ManagementObjectSearcher("SELECT Name, AdapterCompatibility, PNPDeviceID FROM Win32_VideoController"))
        using (var collection = searcher.Get()) {
          foreach (ManagementObject obj in collection) {
            string name = obj["Name"]?.ToString() ?? "";
            string compatibility = obj["AdapterCompatibility"]?.ToString() ?? "";
            string pnpDeviceId = obj["PNPDeviceID"]?.ToString() ?? "";

            // 1. 过滤物理硬件特征：必须是 PCI 设备（排除 ROOT\ 等虚拟根设备）
            if (!pnpDeviceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
              continue;

            // 2. 过滤微软基础渲染/远程桌面代理
            if (name.Contains("Microsoft") || compatibility.Contains("Microsoft"))
              continue;

            // 3. 常见的虚拟显卡黑名单关键字（双重保险）
            if (name.Contains("Idd") || name.Contains("Virtual") || name.Contains("spacedesk"))
              continue;

            if (!string.IsNullOrWhiteSpace(name))
              gpuNames.Add(name.Trim());
          }
        }
      } catch (Exception ex) {
        Logger.Error($"GetAllGpuNamesList 异常: {ex.Message}");
      }
      return gpuNames.Distinct().ToList(); // 去重，防止核显独显重复上报
    }

    /// <summary>
    /// 通过 nvidia-smi -L 获取第一个 NVIDIA 显卡的型号名称
    /// </summary>
    public static string GetGpuModelFromNvidiaSmi() {
      var result = ExecuteCommand("nvidia-smi -L", NvidiaQueryTimeoutMilliseconds);
      if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
        return null;

      // 输出格式示例：GPU 0: NVIDIA GeForce RTX 4060 Laptop GPU (UUID: ...)
      // 提取 "GPU 0: " 之后，左括号之前的内容
      var output = result.Output.Trim();
      var colonIndex = output.IndexOf(':');
      if (colonIndex == -1) return null;

      var afterColon = output.Substring(colonIndex + 1).TrimStart();
      var parenIndex = afterColon.IndexOf('(');
      if (parenIndex != -1)
        afterColon = afterColon.Substring(0, parenIndex).TrimEnd();

      return string.IsNullOrEmpty(afterColon) ? null : afterColon;
    }

    /// <summary>
    /// 通过 WMI 枚举所有 NVIDIA 显卡信息（不依赖 nvidia-smi，UMA 模式下仍可检测到硬件）。
    /// </summary>
    public static List<(string Name, int ModelNum)> GetNvidiaGpuInfoList() {
      var result = new List<(string Name, int ModelNum)>();
      try {
        using (var searcher = new ManagementObjectSearcher(
            "SELECT Name FROM Win32_VideoController WHERE Name LIKE '%NVIDIA%'")) {
          foreach (ManagementObject obj in searcher.Get()) {
            string name = obj["Name"]?.ToString() ?? "";
            // 从名称中提取第一段纯数字（如 "RTX 4060" → 4060，"RTX 5080" → 5080）
            var m = Regex.Match(name, @"\b(\d{3,})\b");
            int modelNum = m.Success ? int.Parse(m.Value) : -1;
            result.Add((name, modelNum));
          }
        }
        
      } catch (Exception ex) {
        Logger.Error($"WMI GPU query failed: {ex.Message}");
      }
      return result;
    }

    /// <summary>是否存在 NVIDIA 独显。</summary>
    public static bool HasNvidiaGpu() {
      try {
        var gpus = PhysicalGPU.GetPhysicalGPUs();

        return gpus != null &&
               gpus.Length > 0;
      } catch (Exception ex) {
        Logger.Warn($"HasNvidiaGpu failed: {ex.Message}");
        return false;
      }
    }

    /// <summary>
    /// 是否为 50 系及以上显卡。
    /// 若检测不到显卡，返回 true。
    /// </summary>
    public static bool IsAbove50Series() {
      var gpus = GetNvidiaGpuInfoList();
      if (gpus.Count == 0) return true;   // 检测不到时保守处理
      return gpus.All(g => g.ModelNum >= 5000);
    }

    public static float[] GetGpuPowerLimits() {
      // Returns [Current Limit, Max Limit]
      var limits = new float[2] { -2f, -2f };
      try {
        ProcessResult result = ExecuteCommand("nvidia-smi -q -d POWER", NvidiaQueryTimeoutMilliseconds);

        if (result.ExitCode == 0) {
          string currentPattern = @"Current Power Limit\s+:\s+([\d.]+)\s+W";
          string maxPattern = @"Max Power Limit\s+:\s+([\d.]+)\s+W";

          var currentMatch = Regex.Match(result.Output, currentPattern);
          var maxMatch = Regex.Match(result.Output, maxPattern);

          if (currentMatch.Success && maxMatch.Success &&
              float.TryParse(currentMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float currentLimit) &&
              float.TryParse(maxMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float maxLimit)) {
            limits[0] = currentLimit;
            limits[1] = maxLimit;
          } else {
            Logger.Warn("Unable to parse NVIDIA GPU power limits from nvidia-smi output.");
          }
        }
      } catch (Exception ex) {
        Logger.Warn($"GetGpuPowerLimits failed: {ex.Message}");
      }
      return limits;
    }

    // 设置显卡频率限制
    public static void SetGPUClockLimit(int freq) {
      ExecuteCommand("nvidia-smi --lock-gpu-clocks=0," + freq);
    }

    public static void SetGPUClockReset() {
      ExecuteCommand("nvidia-smi --reset-gpu-clocks");
    }

    public static void SetMemoryClockLimit(int freq) {
      ExecuteCommand("nvidia-smi --lock-memory-clocks=0," + freq);
    }

    public static void SetMemoryClockReset() {
      ExecuteCommand("nvidia-smi --reset-memory-clocks");
    }

    public static int GetGpuTemperatureTarget() {
      int limit = -2;
      try {
        ProcessResult result = ExecuteCommand("nvidia-smi -q -d TEMPERATURE", NvidiaQueryTimeoutMilliseconds);
        if (result.ExitCode == 0) {
          // 匹配形如 "GPU Target Temperature               : 87 C"
          string targetPattern = @"GPU Target Temperature\s+:\s+(\d+)\s+C";
          var targetMatch = Regex.Match(result.Output, targetPattern);
          if (targetMatch.Success &&
              int.TryParse(targetMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLimit)) {
            limit = parsedLimit;
          } else {
            Logger.Warn("Unable to parse NVIDIA GPU temperature target from nvidia-smi output.");
          }
        }
      } catch (Exception ex) {
        Logger.Warn($"GetGpuTemperatureTarget failed: {ex.Message}");
      }
      return limit;
    }

    public static bool CheckDBVersion(int kind) {
      ProcessResult result = ExecuteCommand("nvidia-smi", NvidiaQueryTimeoutMilliseconds);

      if (result.ExitCode == 0) {
        // 直接匹配第一行的 NVIDIA-SMI 版本号
        string pattern = @"NVIDIA-SMI\s+(\d+\.\d+)";
        Match match = Regex.Match(result.Output, pattern);
        string version = match.Success ? match.Groups[1].Value : null;

        if (version != null) {
          Version v1 = new Version(version);
          Version v2 = new Version("537.42");
          Version v3 = new Version("610.47");
          //if(kind == 2)
          //  v2 = new Version("555.99");
          if (v1 >= v2 && v1 < v3) {
            return true;
          } else {
            MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.DriverNotAllow + version, Strings.Hint, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
          }
        } else {
          MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.DriverNotFound, Strings.Hint, MessageBoxButtons.OK, MessageBoxIcon.Warning);
          return false;
        }
      } else {
        MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.CheckDriverFailed, Strings.Hint, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
      }
    }

    public static void ChangeDBVersion(int kind) {
      string infFileName = "nvpcf.inf";
      string currentPath = AppDomain.CurrentDomain.BaseDirectory;

      // 提取资源中的nvpcf文件到当前目录
      string extractedInfFilePath = Path.Combine(currentPath, "nvpcf.inf");
      string extractedSysFilePath = Path.Combine(currentPath, "nvpcf.sys");
      string extractedCatFilePath = Path.Combine(currentPath, "nvpcf.CAT");

      ExtractResourceToFile("OmenSuperHub.Resources.nvpcf_inf.inf", extractedInfFilePath);
      ExtractResourceToFile("OmenSuperHub.Resources.nvpcf_sys.sys", extractedSysFilePath);
      ExtractResourceToFile("OmenSuperHub.Resources.nvpcf_cat.CAT", extractedCatFilePath);

      string targetVersion = "08/28/2023 31.0.15.3730";
      string driverFile = Path.Combine(currentPath, "nvpcf.inf");
      //if (kind == 2) {
      //  targetVersion = "03/02/2024, 32.0.15.5546";
      //  driverFile = Path.Combine(currentPath, "nvpcf.inf_560.70", "nvpcf.inf");
      //}

      bool hasVersion = false;

      //string tempFilePath = Path.Combine(Path.GetTempPath(), "pnputil_output.txt");
      //string command = $"pnputil /enum-drivers > \"{tempFilePath}\"";
      //ExecuteCommand(command);
      //string output = File.ReadAllText(tempFilePath);
      //// 读取驱动程序列表文件
      //var lines = output.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

      string command = "pnputil /enum-drivers";
      var result = ExecuteCommand(command, DriverCommandTimeoutMilliseconds);
      if (result.ExitCode != 0) {
        Logger.Error($"Failed to enumerate installed drivers before DB change: {result.Error}");
        DeleteExtractedFiles(extractedInfFilePath);
        DeleteExtractedFiles(extractedSysFilePath);
        DeleteExtractedFiles(extractedCatFilePath);
        return;
      }
      string output = result.Output;

      // 读取驱动程序列表文件
      var lines = output.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
      //try {
      //  File.WriteAllLines(Path.Combine(currentPath, "driver.txt"), lines);
      //} catch (Exception ex) {
      //  Console.WriteLine($"Error: {ex.Message}");
      //}

      // 记录需要删除的 Published Name
      var namesToDelete = new List<string>();
      for (int i = 0; i < lines.Length; i++) {
        if (lines[i].Contains($":      {infFileName}")) {
          // 记录上一行的 Published Name
          if (i > 0 && lines[i - 1].Contains(":")) {
            string publishedName = lines[i - 1].Split(':')[1].Trim();

            // 记录 +4 行的 Driver Version
            if (i + 4 < lines.Length && lines[i + 4].Contains(":")) {
              string driverVersion = lines[i + 4].Split(':')[1].Trim();

              if (driverVersion != targetVersion) {
                //Console.WriteLine("发现其他版本: " + driverVersion);
                namesToDelete.Add(publishedName);
              } else {
                hasVersion = true;
                //Console.WriteLine("已经存在所需版本!");
              }
            }
          }
        }
      }

      if (!hasVersion) {
        var installResult = ExecuteCommand($"pnputil /add-driver \"{driverFile}\" /install /force", DriverCommandTimeoutMilliseconds);
        if (installResult.ExitCode != 0) {
          Logger.Error($"Failed to install target DB driver; existing driver packages will be preserved: {installResult.Error}");
          DeleteExtractedFiles(extractedInfFilePath);
          DeleteExtractedFiles(extractedSysFilePath);
          DeleteExtractedFiles(extractedCatFilePath);
          return;
        }
        //Console.WriteLine("成功更改DB版本!");
      }

      if (namesToDelete.Count > 0) {
        //Console.WriteLine("找到需要删除的驱动程序包:");
        foreach (var name in namesToDelete) {
          //Console.WriteLine($"删除驱动程序包: {name}");
          var deleteResult = ExecuteCommand($"pnputil /delete-driver \"{name}\" /uninstall /force", DriverCommandTimeoutMilliseconds);
          if (deleteResult.ExitCode != 0)
            Logger.Warn($"Failed to delete old DB driver package {name}: {deleteResult.Error}");
        }
      } else {
        //Console.WriteLine("没有需要删除的驱动程序包.");
      }

      // 清理临时文件
      //File.Delete(driversListFile);

      // 删除提取的nvpcf文件
      DeleteExtractedFiles(extractedInfFilePath);
      DeleteExtractedFiles(extractedSysFilePath);
      DeleteExtractedFiles(extractedCatFilePath);

      //Console.WriteLine("操作完成.");
    }

    public static void ChangeDBState(bool State) {
      ProcessResult result;
      if (State) {
        result = ExecuteCommand($"pnputil /enable-device \"ACPI\\NVDA0820\\NPCF\"");
      } else {
        result = ExecuteCommand($"pnputil /disable-device \"ACPI\\NVDA0820\\NPCF\"");
      }
      if (result.ExitCode != 0)
        Logger.Warn($"Failed to change DB device state to {(State ? "enabled" : "disabled")}: {result.Error}");
    }

    static void ExtractResourceToFile(string resourceName, string outputFilePath) {
      using (Stream resourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)) {
        if (resourceStream != null) {
          using (FileStream fileStream = new FileStream(outputFilePath, FileMode.Create)) {
            resourceStream.CopyTo(fileStream);
          }
          //Logger.Info($"资源文件已提取到: {outputFilePath}");
        } else {
          Logger.Error($"无法找到资源: {resourceName}");
        }
      }
    }

    static void DeleteExtractedFiles(string filePath) {
      // 删除提取的文件
      if (File.Exists(filePath)) {
        File.Delete(filePath);
        //Console.WriteLine($"删除临时文件:{filePath}");
      }
    }

    private const int DefaultCommandTimeoutMilliseconds = 60000;
    private const int NvidiaQueryTimeoutMilliseconds = 10000;
    private const int DriverCommandTimeoutMilliseconds = 120000;

    public static ProcessResult ExecuteCommand(string command, int timeoutMilliseconds = DefaultCommandTimeoutMilliseconds) {
      if (string.IsNullOrWhiteSpace(command)) {
        return new ProcessResult {
          ExitCode = -1,
          Output = "",
          Error = "Command is empty.",
          TimedOut = false,
          DurationMilliseconds = 0
        };
      }

      if (timeoutMilliseconds <= 0)
        timeoutMilliseconds = DefaultCommandTimeoutMilliseconds;

      var processStartInfo = new ProcessStartInfo {
        FileName = "cmd.exe",
        Arguments = $"/c {command}",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden
      };

      var output = new StringBuilder();
      var error = new StringBuilder();
      var stopwatch = Stopwatch.StartNew();

      using (var process = new Process { StartInfo = processStartInfo }) {
        process.OutputDataReceived += (s, e) => {
          if (e.Data == null) return;
          lock (output) output.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (s, e) => {
          if (e.Data == null) return;
          lock (error) error.AppendLine(e.Data);
        };

        try {
          process.Start();
          process.BeginOutputReadLine();
          process.BeginErrorReadLine();
        } catch (Exception ex) {
          stopwatch.Stop();
          return new ProcessResult {
            ExitCode = -1,
            Output = output.ToString(),
            Error = ex.Message,
            TimedOut = false,
            DurationMilliseconds = stopwatch.ElapsedMilliseconds
          };
        }

        bool exited = process.WaitForExit(timeoutMilliseconds);
        if (!exited) {
          try { process.Kill(); } catch { }
          try { process.WaitForExit(5000); } catch { }
          stopwatch.Stop();

          string timeoutMessage = $"Command timed out after {timeoutMilliseconds} ms: {command}";
          Logger.Warn(timeoutMessage);
          string capturedOutput;
          string capturedError;
          lock (output) capturedOutput = output.ToString();
          lock (error) capturedError = error.ToString();

          return new ProcessResult {
            ExitCode = -1,
            Output = capturedOutput,
            Error = string.IsNullOrWhiteSpace(capturedError)
                ? timeoutMessage
                : capturedError.TrimEnd() + Environment.NewLine + timeoutMessage,
            TimedOut = true,
            DurationMilliseconds = stopwatch.ElapsedMilliseconds
          };
        }

        // With asynchronous redirected streams, a second parameterless wait is
        // required to ensure the final OutputDataReceived/ErrorDataReceived
        // callbacks have drained before the result is returned.
        process.WaitForExit();
        stopwatch.Stop();

        string finalOutput;
        string finalError;
        lock (output) finalOutput = output.ToString();
        lock (error) finalError = error.ToString();

        return new ProcessResult {
          ExitCode = process.ExitCode,
          Output = finalOutput,
          Error = finalError,
          TimedOut = false,
          DurationMilliseconds = stopwatch.ElapsedMilliseconds
        };
      }
    }

    public class ProcessResult {
      public int ExitCode { get; set; }
      public string Output { get; set; }
      public string Error { get; set; }
      public bool TimedOut { get; set; }
      public long DurationMilliseconds { get; set; }
    }
  }
}
