from pathlib import Path

path = Path("App/GpuAppManager.cs")
raw = path.read_bytes()
had_bom = raw.startswith(b"\xef\xbb\xbf")
text = raw.decode("utf-8-sig")
use_crlf = "\r\n" in text
text = text.replace("\r\n", "\n")

def replace_once(old, new, label):
    global text
    if old not in text:
        raise SystemExit(label + " anchor not found")
    text = text.replace(old, new, 1)

replace_once(
    "using System.IO;\n",
    "using System.IO;\nusing System.Globalization;\n",
    "culture using")

replace_once(
'''    public static void SetMemoryClockOffset(int offsetMHz) {
      NVIDIA.Initialize();
      try {
        PhysicalGPU gpu = PhysicalGPU.GetPhysicalGPUs()[0];
''',
'''    public static void SetMemoryClockOffset(int offsetMHz) {
      NVIDIA.Initialize();
      try {
        PhysicalGPU[] gpus = PhysicalGPU.GetPhysicalGPUs();
        if (gpus.Length == 0)
          throw new InvalidOperationException("未找到 GPU");
        PhysicalGPU gpu = gpus[0];
''',
"memory offset GPU guard")

replace_once(
'''    public static int GetCoreClockOffset() {
      NVIDIA.Initialize();
      try {
        PhysicalGPU gpu = PhysicalGPU.GetPhysicalGPUs()[0];
''',
'''    public static int GetCoreClockOffset() {
      NVIDIA.Initialize();
      try {
        PhysicalGPU[] gpus = PhysicalGPU.GetPhysicalGPUs();
        if (gpus.Length == 0) return 0;
        PhysicalGPU gpu = gpus[0];
''',
"core offset GPU guard")

replace_once(
'''    public static int GetMemoryClockOffset() {
      NVIDIA.Initialize();
      try {
        PhysicalGPU gpu = PhysicalGPU.GetPhysicalGPUs()[0];
''',
'''    public static int GetMemoryClockOffset() {
      NVIDIA.Initialize();
      try {
        PhysicalGPU[] gpus = PhysicalGPU.GetPhysicalGPUs();
        if (gpus.Length == 0) return 0;
        PhysicalGPU gpu = gpus[0];
''',
"memory getter GPU guard")

replace_once(
'''      } catch {
      }

      return 0;
    }

    public static int GetMemoryBoostClock()''',
'''      } catch (Exception ex) {
        Logger.Warn($"GetGraphicsBoostClock failed: {ex.Message}");
      }

      return 0;
    }

    public static int GetMemoryBoostClock()''',
"graphics boost logging")

replace_once(
'''      } catch {
      }

      return 0;
    }

    public static List<GpuAppInfo> GetGpuApps()''',
'''      } catch (Exception ex) {
        Logger.Warn($"GetMemoryBoostClock failed: {ex.Message}");
      }

      return 0;
    }

    public static List<GpuAppInfo> GetGpuApps()''',
"memory boost logging")

replace_once(
'        ProcessResult result = ExecuteCommand(command);\n',
'        ProcessResult result = ExecuteCommand(command, NvidiaQueryTimeoutMilliseconds);\n',
"gpu apps query timeout")

replace_once(
'''      } catch { }
      return apps;
''',
'''      } catch (Exception ex) {
        Logger.Warn($"GetGpuApps failed: {ex.Message}");
      }
      return apps;
''',
"gpu apps logging")

replace_once(
'''      } catch {
        MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.RestartGPUFailed, Strings.Hint, MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
''',
'''      } catch (Exception ex) {
        Logger.Error($"RestartGpu failed: {ex.Message}");
        MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.RestartGPUFailed, Strings.Hint, MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
''',
"restart GPU logging")

replace_once(
'''      } catch {
        return false;
      }
''',
'''      } catch (Exception ex) {
        Logger.Warn($"HasNvidiaGpu failed: {ex.Message}");
        return false;
      }
''',
"has GPU logging")

replace_once(
'      var result = ExecuteCommand("nvidia-smi -L");\n',
'      var result = ExecuteCommand("nvidia-smi -L", NvidiaQueryTimeoutMilliseconds);\n',
"GPU model timeout")

replace_once(
'        ProcessResult result = ExecuteCommand("nvidia-smi -q -d POWER");\n',
'        ProcessResult result = ExecuteCommand("nvidia-smi -q -d POWER", NvidiaQueryTimeoutMilliseconds);\n',
"power query timeout")

replace_once(
'''          if (currentMatch.Success && maxMatch.Success) {
            limits[0] = float.Parse(currentMatch.Groups[1].Value);
            limits[1] = float.Parse(maxMatch.Groups[1].Value);
          }
''',
'''          if (currentMatch.Success && maxMatch.Success &&
              float.TryParse(currentMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float currentLimit) &&
              float.TryParse(maxMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float maxLimit)) {
            limits[0] = currentLimit;
            limits[1] = maxLimit;
          } else {
            Logger.Warn("Unable to parse NVIDIA GPU power limits from nvidia-smi output.");
          }
''',
"invariant power parse")

replace_once(
'''      } catch { }
      return limits;
''',
'''      } catch (Exception ex) {
        Logger.Warn($"GetGpuPowerLimits failed: {ex.Message}");
      }
      return limits;
''',
"power query logging")

replace_once(
'        ProcessResult result = ExecuteCommand("nvidia-smi -q -d TEMPERATURE");\n',
'        ProcessResult result = ExecuteCommand("nvidia-smi -q -d TEMPERATURE", NvidiaQueryTimeoutMilliseconds);\n',
"temperature query timeout")

replace_once(
'''          if (targetMatch.Success) {
            limit = int.Parse(targetMatch.Groups[1].Value);
          }
''',
'''          if (targetMatch.Success &&
              int.TryParse(targetMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLimit)) {
            limit = parsedLimit;
          } else {
            Logger.Warn("Unable to parse NVIDIA GPU temperature target from nvidia-smi output.");
          }
''',
"temperature parse")

replace_once(
'''      } catch { }
      return limit;
''',
'''      } catch (Exception ex) {
        Logger.Warn($"GetGpuTemperatureTarget failed: {ex.Message}");
      }
      return limit;
''',
"temperature query logging")

replace_once(
'      ProcessResult result = ExecuteCommand("nvidia-smi");\n',
'      ProcessResult result = ExecuteCommand("nvidia-smi", NvidiaQueryTimeoutMilliseconds);\n',
"driver version timeout")

replace_once(
'''      string command = "pnputil /enum-drivers";
      var result = ExecuteCommand(command);
      string output = result.Output;
''',
'''      string command = "pnputil /enum-drivers";
      var result = ExecuteCommand(command, DriverCommandTimeoutMilliseconds);
      if (result.ExitCode != 0) {
        Logger.Error($"Failed to enumerate installed drivers before DB change: {result.Error}");
        DeleteExtractedFiles(extractedInfFilePath);
        DeleteExtractedFiles(extractedSysFilePath);
        DeleteExtractedFiles(extractedCatFilePath);
        return;
      }
      string output = result.Output;
''',
"driver enumeration guard")

replace_once(
'''      if (!hasVersion) {
        ExecuteCommand($"pnputil /add-driver \\"{driverFile}\\" /install /force", DriverCommandTimeoutMilliseconds);
        //Console.WriteLine("成功更改DB版本!");
      }
''',
'''      if (!hasVersion) {
        var installResult = ExecuteCommand($"pnputil /add-driver \\"{driverFile}\\" /install /force", DriverCommandTimeoutMilliseconds);
        if (installResult.ExitCode != 0) {
          Logger.Error($"Failed to install target DB driver; existing driver packages will be preserved: {installResult.Error}");
          DeleteExtractedFiles(extractedInfFilePath);
          DeleteExtractedFiles(extractedSysFilePath);
          DeleteExtractedFiles(extractedCatFilePath);
          return;
        }
        //Console.WriteLine("成功更改DB版本!");
      }
''',
"driver install guard")

replace_once(
'''          ExecuteCommand($"pnputil /delete-driver \\"{name}\\" /uninstall /force", DriverCommandTimeoutMilliseconds);
''',
'''          var deleteResult = ExecuteCommand($"pnputil /delete-driver \\"{name}\\" /uninstall /force", DriverCommandTimeoutMilliseconds);
          if (deleteResult.ExitCode != 0)
            Logger.Warn($"Failed to delete old DB driver package {name}: {deleteResult.Error}");
''',
"driver delete logging")

replace_once(
'''    public static void ChangeDBState(bool State) {
      if (State) {
        ExecuteCommand($"pnputil /enable-device \\"ACPI\\\\NVDA0820\\\\NPCF\\"");
      } else {
        ExecuteCommand($"pnputil /disable-device \\"ACPI\\\\NVDA0820\\\\NPCF\\"");
      }
    }
''',
'''    public static void ChangeDBState(bool State) {
      ProcessResult result;
      if (State) {
        result = ExecuteCommand($"pnputil /enable-device \\"ACPI\\\\NVDA0820\\\\NPCF\\"");
      } else {
        result = ExecuteCommand($"pnputil /disable-device \\"ACPI\\\\NVDA0820\\\\NPCF\\"");
      }
      if (result.ExitCode != 0)
        Logger.Warn($"Failed to change DB device state to {(State ? "enabled" : "disabled")}: {result.Error}");
    }
''',
"DB state logging")

replace_once(
'''    private const int DefaultCommandTimeoutMilliseconds = 60000;
    private const int DriverCommandTimeoutMilliseconds = 120000;
''',
'''    private const int DefaultCommandTimeoutMilliseconds = 60000;
    private const int NvidiaQueryTimeoutMilliseconds = 10000;
    private const int DriverCommandTimeoutMilliseconds = 120000;
''',
"timeout constants")

if use_crlf:
    text = text.replace("\n", "\r\n")
data = text.encode("utf-8")
if had_bom:
    data = b"\xef\xbb\xbf" + data
path.write_bytes(data)
