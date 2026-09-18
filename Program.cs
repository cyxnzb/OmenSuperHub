using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Hp.Bridge.Client.SDKs.PerformanceControl.DataStructure;
using HP.Omen.Core.Common.NVidiaApi;
using HP.Omen.Core.Model.Device.Enums;
using HP.Omen.Core.Model.Device.Models;
using Microsoft.Win32;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;
using static HP.Omen.Core.Model.Device.Models.GraphicsSwitcherHelper;
using static OmenSuperHub.GpuAppManager;
using static OmenSuperHub.OmenHardware;
using static OmenSuperHub.OmenLighting;
using LibreComputer = LibreHardwareMonitor.Hardware.Computer;
using LibreHardwareType = LibreHardwareMonitor.Hardware.HardwareType;
using LibreIHardware = LibreHardwareMonitor.Hardware.IHardware;
using LibreISensor = LibreHardwareMonitor.Hardware.ISensor;
using LibreSensorType = LibreHardwareMonitor.Hardware.SensorType;

namespace OmenSuperHub {
  static partial class Program {
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    // ── 低级鼠标钩子（用于托盘图标滚轮切换预设）──────────────────────────
    delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern IntPtr GetModuleHandle(string lpModuleName);

    // Shell_NotifyIconGetRect：获取托盘图标的屏幕矩形
    [StructLayout(LayoutKind.Sequential)]
    struct NOTIFYICONIDENTIFIER {
      public uint cbSize;
      public IntPtr hWnd;
      public uint uID;
      public Guid guidItem;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconRect);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT {
      public Point pt;
      public int mouseData;
      public int flags;
      public int time;
      public IntPtr dwExtraInfo;
    }

    const int WH_MOUSE_LL = 14;
    const int WM_MOUSEWHEEL = 0x020A;

    static IntPtr _mouseHook = IntPtr.Zero;
    static LowLevelMouseProc _mouseHookProc; // 防止 GC 回收委托
    static Control _invokeTarget;            // 专用 marshal 控件（句柄在安装钩子前已创建）

    static byte currentAnimSpeed = 1, currentAnimDirection = 0, currentAnimTheme = 0, currentAnimEffect = 2;
    // 单键RGB当前选中状态（用于菜单勾选，null/-1 表示未选择）
    static string perKeyStaticColorSel = null;
    static string perKeyAnimationSel = null;
    static int perKeyBrightnessSel = -1;
    // 四分区/灯条颜色选择状态（key = device tag, value = 颜色名称；null 表示自定义或未选）
    static string zoneGlobalColorSel_Keyboard = null;
    static string zoneGlobalColorSel_LightBar = null;
    static string[] zoneColorSel_Keyboard = new string[4];
    static string[] zoneColorSel_LightBar = new string[4];
    // 四分区/灯条 WMI 协议选择（默认 BasicFourZone；用户可在菜单中切换并持久化）
    static LightingControlInterface kbControlInterface = LightingControlInterface.BasicFourZone;
    static LightingControlInterface lbControlInterface = LightingControlInterface.Dojo;
    static int DBVersion = 2, countDB = 0, countDBInit = 10, tryTimes = 0, maxRetry = 5, CPULimitDB = 20;
    static ToolStripMenuItem performanceControlMenu;
    static int textSize = 40;
    static int countRestore = 0, gpuClock = 0, gpuCoreOverclock = -1, gpuMemoryOverclock = -1, maxFrameRate = -1, graphicsBoostClock = 0;
    static int alreadyRead = 0, alreadyReadCode = 1000;
    static readonly string[] PresetOrder = { "PresetBalanced", "PresetExtreme", "PresetGpuPriority", "PresetLightUse", "PresetCustom1", "PresetCustom2", "PresetCustom3" };
    static string currentPreset = "PresetBalanced", presetCustom1Name = Strings.PresetCustom1, presetCustom2Name = Strings.PresetCustom2, presetCustom3Name = Strings.PresetCustom3;
    static string fanTable = "cool", fanControl = "auto", tempSensitivity = "high", tppPower = "null", iccMax = "null", acLoadline = "null", cpuPower = "null", tgpPower = "on", ppabPower = "on", dState = "normal", autoStart = "off", customIcon = "original", floatingBar = "off", floatingBarLoc = "left", floatingBarScreen = "", omenKey = OmenKeyActions.Default, omenKeyAppPath = "", omenKeyAppName = "", omenKeyShortcut = "", omenKeyPresetCandidates = "", dataLocalize = "off", appLanguage = "zh-CN", autoFanProtect = "on";
    static volatile bool monitorFan = false;
    static bool skipCheckedUpdate = false; // action 内拦截时置 true，阻止 CreateMenuItem 覆盖勾选
    static bool suppressPerformanceSliderEvents = false; // 程序同步滑块时不触发硬件写入/持久化
    static bool showCPUTemp = true, showCPUPower = true, showCPUFrequency = false, showGPUTemp = true, showGPUPower = true, showGPUFrequency = false;
    static bool powerOnline = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
    static bool monitorCPU = true, monitorGPU = true, isConnectedToNVIDIA = true, prevIsConnectedToNVIDIA = true, omenKeyTriggered = false; // isTwoBytePL4 = false;
    static bool hasNVIDIAGpu; // 启动时一次性检测，硬件状态不会改变
    static string monitorRefreshRate = "low"; // 刷新频率：low=1s, high=0.25s
    static readonly List<int> fanSpeedNow = new List<int> { 20, 20, 0 };
    static float respondSpeed = 0.4f;

    static int? maxCPUTemp = null;
    static int? maxGPUTemp = null;
    static float CPUTemp = 50, GPUTemp = 40, rawTempCPU = 50f, rawTempGPU = 40f;
    static float CPUPower = 0, GPUPower = 0, CPUFrequency = 0f, GPUFrequency = 0f, rawPowerCPU = 0f, rawPowerGPU = 0f, rawFrequencyCPU = 0f, rawFrequencyGPU = 0f;
    static bool rawGotGPU = false;
    static DateTime lastCpuTempSampleUtc = DateTime.MinValue, lastGpuTempSampleUtc = DateTime.MinValue;
    static DateTime lastCpuPowerSampleUtc = DateTime.MinValue, lastGpuPowerSampleUtc = DateTime.MinValue;
    static DateTime lastCpuSmoothedSampleUtc = DateTime.MinValue, lastGpuSmoothedSampleUtc = DateTime.MinValue;
    static readonly TimeSpan hardwareSampleTimeout = TimeSpan.FromSeconds(5);
    static readonly object deferredHardwareApplyLock = new object();
    static readonly Dictionary<string, int> deferredHardwareApplyVersions = new Dictionary<string, int>();
    static int deferredHardwareApplyGeneration = 0;
    const int AutoFanDeadband = 2;       // 200 RPM
    const int AutoFanRiseStep = 3;       // max +300 RPM per second
    const int AutoFanFallStep = 2;       // max -200 RPM per second
    static bool autoFanSensorFailsafeActive = false;
    static volatile bool tempReady = false;   // 子进程首次输出有效温度后置 true
    static volatile bool cpuTempReady = false; // CPU 温度已初始化给平滑值，允许参与风扇控制
    static volatile bool gpuTempReady = false; // GPU 温度已初始化给平滑值，允许参与风扇控制
    static volatile bool hwMonitorStopping = false; // 主动停止时置 true，阻止 Exited 自动重启
    static Process hwMonitorProcess;
    static StreamWriter hwMonitorIn;

    // Cache last written values to avoid unnecessary disk reads/writes
    static string lastCpuText = null, lastGpuText = null, lastFanText = null, pawnIOState = "";
    static int _isSyncingDataToTxt = 0;
    static int _floatingUpdatePending = 0;
    static string tempDisplayMode = "smoothed"; // 温度显示方式：smoothed=平滑值, raw=原始值
    static int? platformMaxFanSpeed = null; // 平台最大转速（RPM），由LoadDefaultFanConfig获取后缓存
    static SortedDictionary<float, int> CPUTempFanMap = new SortedDictionary<float, int>();
    static SortedDictionary<float, int> GPUTempFanMap = new SortedDictionary<float, int>();
    static System.Threading.Timer fanControlTimer;
    static System.Timers.Timer tooltipUpdateTimer; // Timer for updating tooltip
    static System.Windows.Forms.Timer checkFloatingTimer, optimiseTimer;
    static NotifyIcon trayIcon;
    static FloatingForm floatingForm;
    static ToolStripMenuItem irSensorMenu;
    static ToolStripMenuItem ambientSensorMenu;
    static ToolStripMenuItem pchSensorMenu;
    static ToolStripMenuItem vrSensorMenu;
    static ToolStripTrackBar fanTrackBar, cpuPowerTrackBar, tppTrackBar, gpuCoreOverclockTrackBar, gpuMemoryOverclockTrackBar, gpuClockTrackBar, maxFrameRateTrackBar, textSizeTrackBar;
    static ToolStripMenuItem fanValueLabel, cpuPowerValueLabel, tppValueLabel, gpuCoreOverclockValueLabel, gpuMemoryOverclockValueLabel, gpuClockValueLabel, maxFrameRateValueLabel, textSizeLabel;

    static bool Is3FanNb = false, isFanCleanSupported = false, isFanLegacyCleanSupported = false;
    static bool isSysInfoMenuOpen = false;
    static string systemSSID, sku, biosVersion;
    static bool supportAni = false, supportDojo = false, supportLightbar = false, supportHotSwitch = false;
    static bool isCPUPowerControlSupported = false, isAmbientSensorSupported = false;
    static DeviceEnums.DeviceType deviceType;
    static string deviceDisplayName;
    static int cycleNumber;
    static PlatformSettings platformSettings;
    static GraphicsSwitcherMode NvGraphicsMode;
    static NbKeyboardLightingType kbType;
    static SynchronizationContext uiContext;
    //static Stopwatch sw = Stopwatch.StartNew();

    [STAThread]
    static void Main(string[] args) {
      //Console.WriteLine($"0.1: {sw.ElapsedMilliseconds}ms");
      if (args.Length > 0 && args[0] == "--hwmonitor") {
        RunHardwareMonitor();
        return;
      }

      // ── 静默重启模式：由任务计划登录触发器调用
      if (args.Length > 0 && args[0] == "--relaunch") {
        // 终止其他已有实例
        var currentId = Process.GetCurrentProcess().Id;
        foreach (var proc in Process.GetProcessesByName("OmenSuperHub")) {
          if (proc.Id == currentId) continue;
          try { proc.Kill(); proc.WaitForExit(3000); } catch { }
        }
        // 启动新实例（不带参数，走正常流程）
        Process.Start(new ProcessStartInfo {
          FileName = Application.ExecutablePath,
          UseShellExecute = true
        });
        return; // 本实例立即退出，不做任何初始化
      }

      bool isNewInstance;
      using (Mutex mutex = new Mutex(true, "MyUniqueAppMutex", out isNewInstance)) {
        if (!isNewInstance) {
          return;
        }

        if (Environment.OSVersion.Version.Major >= 6) {
          SetProcessDPIAware();
        }

        AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(CurrentDomain_UnhandledException);
        Application.ThreadException += new ThreadExceptionEventHandler(Application_ThreadException);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbeddedAssembly;

        Version version = Assembly.GetExecutingAssembly().GetName().Version;
        string versionString = version.ToString().Replace(".", "");
        alreadyReadCode = new Random(int.Parse(versionString)).Next(1000, 10000);

        // 读取 deviceDisplayName / cycleNumber / deviceType / supportDojo / systemSSID / alreadyRead
        LoadDeviceInfoFromRegistry();
        //Console.WriteLine($"0.2: {sw.ElapsedMilliseconds}ms");
        // 每版本仅显示一次
        if (alreadyRead != alreadyReadCode) {
          string validationResult = Validation(deviceDisplayName);
          if (validationResult == Strings.ValidationUnsupported) {
            var result = MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.ProductUnsupported, Strings.Warning, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (result != DialogResult.OK)
              return; // 退出程序
          } else if (validationResult == Strings.ValidationUnsupportedHPProduct) {
            var result = MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.ProductUnsupportedHP, Strings.Warning, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (result != DialogResult.OK)
              return; // 退出程序
          } else if (validationResult == Strings.ValidationOldOmenProduct) {
            var result = MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.ProductOldOmen, Strings.Warning, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (result != DialogResult.OK)
              return; // 退出程序
          }
        }

        var t1 = Task.Run(() => {
          //Console.WriteLine($"1.1: {sw.ElapsedMilliseconds}ms");
          platformSettings = PerformanceControlHelper.GetPlatformSettings(deviceType.ToString(), sku);
          //Console.WriteLine($"1.2: {sw.ElapsedMilliseconds}ms");

          if (platformSettings != null) {
            currentPreset = "PresetBalanced";
            isCPUPowerControlSupported = true;
          }
          //isCPUPowerControlSupported = IsPowerControlSupported(deviceType); // 似乎不准确
          InitPlatformMaxFanSpeed();
        });
        var t2 = Task.Run(() => {
          biosVersion = GetBiosVersion();
        });
        var t3 = Task.Run(() => {
          hasNVIDIAGpu = HasNvidiaGpu();
          if (hasNVIDIAGpu) {
            ExtractAndPreloadNativeDll("NvidiaApi.dll");
            maxFrameRate = NvApiWrapper.NVAPI_GetMaxFrameRate();
            graphicsBoostClock = GetGraphicsBoostClock();
          }
        });
        var t4 = Task.Run(() => kbType = GetKeyboardType());
        var t5 = Task.Run(() => NvGraphicsMode = GetGfxMode());
        var t6 = Task.Run(() => {
          // 启动阶段只探测能力。实际性能模式由 RestoreConfig/当前预设统一应用，
          // 避免应用一启动就无条件切到 Unleash。
          Is3FanNb = IsThreeFanSupported();
        });
        var t7 = Task.Run(() => {
          isFanCleanSupported = IsCleanCreekSupported();
          isFanLegacyCleanSupported = IsLegacyCleanCreekSupported();
        });
        var t8 = Task.Run(() => {
          getOmenKeyTask();
          monitorQuery();
          if (supportDojo && IsLightBarPlatform())
            supportLightbar = true;
        });
        var t9 = Task.Run(() => {
          SetBrowserEmulationForWebBrowser();
          int irTemp = GetSensorTemperature(0);
          int ambientTemp = GetSensorTemperature(1);
          isAmbientSensorSupported = ambientTemp > 1 && irTemp != ambientTemp;
        });
        //var t10 = Task.Run(() => isTwoBytePL4 = IsTwoBytePL4Supported());

        //Console.WriteLine($"1: {sw.ElapsedMilliseconds}ms");
        Task.WaitAll(t1, t2, t3, t4, t5, t6, t7, t8, t9);
        // 平台/NVIDIA 探测完成后再读取温限，避免 hasNVIDIAGpu 的启动竞态。
        InitMaxTemp();
        //Console.WriteLine($"2: {sw.ElapsedMilliseconds}ms");

        if (FourZoneSupportHelper.IsAnimationSupported(kbType, deviceType, cycleNumber)) {
          supportAni = true;
        }

        LoadLanguageSetting();  // 必须在 InitTrayIcon 之前，使菜单使用正确语言
        InitTrayIcon();
        uiContext = SynchronizationContext.Current;
        //Console.WriteLine($"3: {sw.ElapsedMilliseconds}ms");

        optimiseTimer = new System.Windows.Forms.Timer();
        optimiseTimer.Interval = 30000;
        optimiseTimer.Tick += (s, e) => optimiseSchedule();
        optimiseTimer.Start();

        // 立即执行一次
        optimiseSchedule();

        // Main loop to query CPU and GPU temperature every second
        fanControlTimer = new System.Threading.Timer((e) => {
          try {
            ApplyAutomaticFanControl();
          } catch (Exception ex) {
            Logger.Error($"Automatic fan control failed: {ex.Message}");
          }
        }, null, 100, 1000);

        checkFloatingTimer = new System.Windows.Forms.Timer();
        checkFloatingTimer.Interval = 100;
        checkFloatingTimer.Tick += (s, e) => HandleOmenKeyAction();
        checkFloatingTimer.Start();

        RestoreConfig();
        //Console.WriteLine($"4: {sw.ElapsedMilliseconds}ms");

        if (alreadyRead != alreadyReadCode) {
          HelpForm.Instance.Show();
          alreadyRead = alreadyReadCode;
          SaveConfig("AlreadyRead");
        }

        SystemEvents.PowerModeChanged += new PowerModeChangedEventHandler(OnPowerChange);
        //PrintSystemDesignData();

        //MessageBox.Show($"消息测试", Strings.Hint, MessageBoxButtons.OK, MessageBoxIcon.Warning);

        //trayIcon.BalloonTipTitle = "消息测试";
        //trayIcon.BalloonTipText = $"消息测试";
        //trayIcon.BalloonTipIcon = ToolTipIcon.Warning;
        //trayIcon.ShowBalloonTip(3000);

        //Stopwatch sw = Stopwatch.StartNew();
        //Console.WriteLine($"1: {sw.ElapsedMilliseconds}ms");
        //Console.Error.WriteLine("CRASH: " + $"1: {sw.ElapsedMilliseconds}ms");

        //Platform omenPlatform = DeviceModel.OmenPlatform;
        //Console.WriteLine($"Platform Name: {omenPlatform.Name}");
        //Console.WriteLine($"Display Name: {omenPlatform.DisplayName}");
        //Console.WriteLine($"Features: {string.Join(", ", omenPlatform.Feature ?? new List<string>())}");
        //Console.WriteLine($"IsNVdGPU: {OmenHsaClient.IsNVdGPU}");
        //Console.WriteLine($"IsIntelGPU: {OmenHsaClient.IsIntelGPU}");
        //Console.WriteLine($"IsGpuSupport: {OmenHsaClient.IsGpuSupport}");
        //Console.WriteLine($"IsIntelGraphics: {OmenHsaClient.IsIntelGraphics()}");

        Logger.Info($"version: {version}");
        Application.Run();
      }
    }

    private static void SetBrowserEmulationForWebBrowser() {
      string appName = Process.GetCurrentProcess().ProcessName + ".exe";
      using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION", true)) {
        if (key == null) {
          // 如果键不存在则创建
          Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION");
          using (var newKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION", true)) {
            newKey?.SetValue(appName, 11001, RegistryValueKind.DWord);
          }
        } else {
          key.SetValue(appName, 11001, RegistryValueKind.DWord);
        }
      }
    }

    static string GetBiosVersion() {
      using (var searcher = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion FROM Win32_BIOS"))
      using (var collection = searcher.Get())
        foreach (ManagementObject obj in collection)
          return obj["SMBIOSBIOSVersion"]?.ToString() ?? "未知";
      return "未知";
    }

    static string GetCpuModel() {
      using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
      using (var collection = searcher.Get())
        foreach (ManagementObject obj in collection)
          return obj["Name"]?.ToString()?.Trim() ?? "未知";
      return "未知";
    }

    public static bool HasIntelCpu() {
      try {
        using (var searcher = new ManagementObjectSearcher(
            "root\\CIMV2", "SELECT Manufacturer, Name FROM Win32_Processor")) {
          foreach (var obj in searcher.Get()) {
            string manufacturer = obj["Manufacturer"]?.ToString() ?? "";
            string name = obj["Name"]?.ToString() ?? "";

            // GenuineIntel 是 Intel CPU 的标准制造商字符串
            if (manufacturer.IndexOf("GenuineIntel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0) {
              return true;
            }
          }
        }
      } catch { }
      return false;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    private static void ExtractAndPreloadNativeDll(string dllName) {
      var currentAssembly = Assembly.GetExecutingAssembly();

      // 在嵌入资源中查找（资源名通常是 "命名空间.文件名"）
      var resourceName = currentAssembly
          .GetManifestResourceNames()
          .FirstOrDefault(r => r.EndsWith(dllName, StringComparison.OrdinalIgnoreCase));

      if (resourceName == null) {
        throw new FileNotFoundException($"嵌入资源中找不到 {dllName}");
      }

      // 释放到程序目录（或 Temp 目录）
      string outputPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dllName);

      if (!File.Exists(outputPath)) {
        using (var stream = currentAssembly.GetManifestResourceStream(resourceName))
        using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write)) {
          stream.CopyTo(fs);
        }
      }

      // 提前加载，之后 DllImport 会自动复用
      IntPtr handle = LoadLibrary(outputPath);
      if (handle == IntPtr.Zero) {
        Logger.Error($"LoadLibrary 失败，错误码: {Marshal.GetLastWin32Error()}");
      } else {
        supportHotSwitch = true;
      }
    }

    private static Assembly ResolveEmbeddedAssembly(object sender, ResolveEventArgs args) {
      var assemblyName = new AssemblyName(args.Name).Name + ".dll";

      var currentAssembly = Assembly.GetExecutingAssembly();

      var resourceName = currentAssembly
          .GetManifestResourceNames()
          .FirstOrDefault(r => r.EndsWith(assemblyName, StringComparison.OrdinalIgnoreCase));

      if (resourceName == null)
        return null;

      using (var stream = currentAssembly.GetManifestResourceStream(resourceName)) {
        if (stream == null)
          return null;

        var buffer = new byte[stream.Length];
        stream.Read(buffer, 0, buffer.Length);

        return Assembly.Load(buffer);
      }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct DISPLAY_DEVICE {
      [MarshalAs(UnmanagedType.U4)]
      public int cb;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
      public string DeviceName;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
      public string DeviceString;
      [MarshalAs(UnmanagedType.U4)]
      public DisplayDeviceStateFlags StateFlags;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
      public string DeviceID;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
      public string DeviceKey;
    }

    [Flags()]
    enum DisplayDeviceStateFlags : int {
      /// <summary>The device is part of the desktop.</summary>
      AttachedToDesktop = 0x1,
      MultiDriver = 0x2,
      /// <summary>The device is part of the desktop.</summary>
      PrimaryDevice = 0x4,
      /// <summary>Represents a pseudo device used to mirror application drawing for remoting or other purposes.</summary>
      MirroringDriver = 0x8,
      /// <summary>The device is VGA compatible.</summary>
      VGACompatible = 0x10,
      /// <summary>The device is removable; it cannot be the primary display.</summary>
      Removable = 0x20,
      /// <summary>The device has more display devices.</summary>
      ModesPruned = 0x8000000,
      Remote = 0x4000000,
      Disconnect = 0x2000000
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern bool EnumDisplayDevices(
        string lpDevice,
        uint iDevNum,
        ref DISPLAY_DEVICE lpDisplayDevice,
        uint dwFlags);

    // 判断独显未工作条件
    static void monitorQuery() {
      if (Screen.AllScreens.Length != 1)
        return;
      DISPLAY_DEVICE d = new DISPLAY_DEVICE();
      d.cb = Marshal.SizeOf(d);
      uint deviceNum = 0;

      while (EnumDisplayDevices(null, deviceNum, ref d, 0)) {
        if (d.StateFlags.HasFlag(DisplayDeviceStateFlags.AttachedToDesktop)) {
          if (d.DeviceString.Contains("Intel") || d.DeviceString.Contains("AMD")) {
            isConnectedToNVIDIA = false;
            return;
          }
        }
        deviceNum++;
      }

      isConnectedToNVIDIA = true;
    }

    static bool IsPlausibleTemperature(float value) {
      return !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f && value < 130f;
    }

    static bool IsPlausiblePower(float value) {
      return !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0f && value < 9999f;
    }

    [HandleProcessCorruptedStateExceptions]
    static void RunHardwareMonitor() {
      bool isEnabled = false;
      //Console.Error.WriteLine("CRASH: " + $"1: {sw.ElapsedMilliseconds}ms");
      var computer = new LibreComputer() { };
      //Console.Error.WriteLine("CRASH: " + $"2: {sw.ElapsedMilliseconds}ms");
      try {
        computer.Open();
      } catch (Exception ex) {
        Console.Error.WriteLine("CRASH: Open failed - " + ex.Message);
        Environment.Exit(1);
      }
      //Console.Error.WriteLine("CRASH: " + $"3: {sw.ElapsedMilliseconds}ms");
      int sleepMs = 1000;
      var computerLock = new object();

      var readThread = new Thread(() => {
        while (true) {
          string line = Console.ReadLine();
          if (line == null) Environment.Exit(0);
          if (line == "GPU:ON") {
            lock (computerLock) {
              Volatile.Write(ref isEnabled, false);
              computer.IsGpuEnabled = true;
              Volatile.Write(ref isEnabled, true);
            }
          }
          if (line == "GPU:OFF") {
            lock (computerLock) {
              computer.IsGpuEnabled = false;
            }
          }
          if (line == "CPU:ON") {
            lock (computerLock) {
              Volatile.Write(ref isEnabled, false);
              computer.IsCpuEnabled = true;
              Volatile.Write(ref isEnabled, true);
            }
          }
          if (line == "CPU:OFF") {
            lock (computerLock) {
              computer.IsCpuEnabled = false;
            }
          }
          if (line.StartsWith("INTERVAL:") && int.TryParse(line.Substring(9), out int ms) && ms > 0)
            sleepMs = ms;
        }
      });
      readThread.IsBackground = true;
      readThread.Start();

      while (!Volatile.Read(ref isEnabled)) {
        Thread.Sleep(1);
      }
      //Console.Error.WriteLine("CRASH: " + $"4: {sw.ElapsedMilliseconds}ms");
      while (true) {
        bool gGpu = false;
        bool exactCpuClockFound = false;
        int cpuTempPriority = 0;
        float cpuFallbackTempSum = 0f;
        int cpuFallbackTempCount = 0;
        float? tCpuSample = null, tGpuSample = null;
        float pCpu = -1f, pGpu = -1f;
        float fCpu = 0, fGpu = 0;
        try {
          lock (computerLock) {
          foreach (LibreIHardware hw in computer.Hardware) {
            if (hw.HardwareType != LibreHardwareType.Cpu && hw.HardwareType != LibreHardwareType.GpuNvidia && hw.HardwareType != LibreHardwareType.GpuAmd) continue;

            // 如果底层驱动对象因为驱动更新导致句柄无效，Update会抛出异常。
            // 此时我们直接让子进程退出，父进程会重新启动一个新的子进程来进行初始化。
            try {
              hw.Update();
            } catch (Exception ex) {
              Console.Error.WriteLine("CRASH: Update failed - " + ex.Message);
              Environment.Exit(1);
            }
            //Console.Error.WriteLine("CRASH: " + $"5: {sw.ElapsedMilliseconds}ms");
            foreach (LibreISensor sensor in hw.Sensors) {
              try {
                if (hw.HardwareType == LibreHardwareType.Cpu) {
                  if (sensor.SensorType == LibreSensorType.Temperature && sensor.Value.HasValue) {
                    float value = sensor.Value.Value;
                    if (IsPlausibleTemperature(value)) {
                      int priority = string.Equals(sensor.Name, "CPU Package", StringComparison.OrdinalIgnoreCase) ? 3
                          : sensor.Name.IndexOf("Tctl/Tdie", StringComparison.OrdinalIgnoreCase) >= 0 ? 2
                          : sensor.Name.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0;
                      if (priority > cpuTempPriority) {
                        tCpuSample = value;
                        cpuTempPriority = priority;
                      } else if (priority == 0 &&
                                 sensor.Name.IndexOf("Distance", StringComparison.OrdinalIgnoreCase) < 0) {
                        // 没有标准 Package/Tctl 命名时，使用其余真实 CPU 温度的平均值兜底。
                        // 相比随便挑单核心温度，这样更不容易被短时尖峰带着风扇乱跳。
                        cpuFallbackTempSum += value;
                        cpuFallbackTempCount++;
                      }
                    }
                  }
                  if (sensor.SensorType == LibreSensorType.Power && sensor.Name.Contains("Package") && sensor.Value.HasValue) {
                    float value = sensor.Value.Value;
                    if (IsPlausiblePower(value)) pCpu = value;
                  }
                  if (sensor.SensorType == LibreSensorType.Clock && sensor.Value.HasValue) {
                    if (sensor.Name == "CPU Core" || sensor.Name == "CPU Core #1") {
                      fCpu = sensor.Value.GetValueOrDefault();
                      exactCpuClockFound = true;
                    } else if (!exactCpuClockFound && fCpu <= 0 && sensor.Name != "Bus Speed") {
                      fCpu = sensor.Value.GetValueOrDefault();
                    }
                  }
                } else if (hw.HardwareType == LibreHardwareType.GpuNvidia || hw.HardwareType == LibreHardwareType.GpuAmd) {
                  if (sensor.SensorType == LibreSensorType.Temperature && sensor.Name == "GPU Core" && sensor.Value.HasValue) {
                    float value = sensor.Value.Value;
                    if (IsPlausibleTemperature(value)) tGpuSample = value;
                  }
                  if (sensor.SensorType == LibreSensorType.Power && sensor.Name == "GPU Package" && sensor.Value.HasValue) {
                    float value = sensor.Value.Value;
                    if (IsPlausiblePower(value)) pGpu = value;
                  }
                  if (sensor.SensorType == LibreSensorType.Clock && sensor.Name == "GPU Core" && sensor.Value.HasValue)
                    fGpu = sensor.Value.GetValueOrDefault();
                }
              } catch { }
            }
          }
          }
          if (!tCpuSample.HasValue && cpuFallbackTempCount > 0)
            tCpuSample = cpuFallbackTempSum / cpuFallbackTempCount;
          gGpu = tGpuSample.HasValue;
          float outCpuTemp = tCpuSample ?? -1f;
          float outGpuTemp = tGpuSample ?? -1f;
          Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0:F2};{1:F2};{2:F2};{3:F2};{4};{5:F2};{6:F2}", outCpuTemp, pCpu, outGpuTemp, pGpu, gGpu ? 1 : 0, fCpu, fGpu));
        } catch (Exception ex) {
          Console.Error.WriteLine("CRASH: " + ex.Message);
          Environment.Exit(1);
        }
        Thread.Sleep(sleepMs);
      }
    }

    static void StartHardwareMonitor() {
      if (hwMonitorProcess != null && !hwMonitorProcess.HasExited) return;

      hwMonitorProcess = new Process {
        StartInfo = new ProcessStartInfo {
          FileName = Application.ExecutablePath,
          Arguments = "--hwmonitor",
          UseShellExecute = false,
          RedirectStandardInput = true,
          RedirectStandardOutput = true,
          RedirectStandardError = true,
          CreateNoWindow = true,
          WindowStyle = ProcessWindowStyle.Hidden
        }
      };

      hwMonitorProcess.OutputDataReceived += (s, e) => {
        if (string.IsNullOrEmpty(e.Data)) return;
        //Debug.WriteLine("[HWMonitor OUT] " + e.Data); // 将子进程输出重定向到VS的输出窗口
        if (e.Data.StartsWith("CRASH:")) return;
        var parts = e.Data.Split(';');
        if (parts.Length == 5 || parts.Length == 7) {
          DateTime sampleUtc = DateTime.UtcNow;

          bool cpuTempValid = float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float tc) && IsPlausibleTemperature(tc);
          bool cpuPowerValid = float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float pc) && IsPlausiblePower(pc);
          float tg = 0f;
          bool gpuTempValid = parts[4] == "1" &&
              float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out tg) && IsPlausibleTemperature(tg);
          bool gpuPowerValid = float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float pg) && IsPlausiblePower(pg);

          if (cpuTempValid) {
            rawTempCPU = tc;
            lastCpuTempSampleUtc = sampleUtc;
            if (!cpuTempReady) {
              smoothedCPUTemp = rawTempCPU;
              cpuTempReady = true;
            }
          }
          if (cpuPowerValid) {
            rawPowerCPU = pc;
            lastCpuPowerSampleUtc = sampleUtc;
          }

          if (gpuTempValid) {
            rawTempGPU = tg;
            lastGpuTempSampleUtc = sampleUtc;
            rawGotGPU = true;
            if (!gpuTempReady) {
              smoothedGPUTemp = rawTempGPU;
              gpuTempReady = true;
            }
          }
          if (gpuPowerValid) {
            rawPowerGPU = pg;
            lastGpuPowerSampleUtc = sampleUtc;
          }

          if (cpuTempReady && sampleUtc - lastCpuTempSampleUtc > hardwareSampleTimeout)
            cpuTempReady = false;

          if (gpuTempReady && sampleUtc - lastGpuTempSampleUtc > hardwareSampleTimeout) {
            gpuTempReady = false;
            rawGotGPU = false;
            GPUTemp = 40;
            GPUPower = 0;
            rawFrequencyGPU = 0f;
            GPUFrequency = 0f;
          }

          rawFrequencyCPU = 0f;
          rawFrequencyGPU = 0f;
          if (parts.Length == 7) {
            if (float.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out float fc) && !float.IsNaN(fc) && !float.IsInfinity(fc) && fc >= 0f)
              rawFrequencyCPU = fc;
            if (float.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out float fg) && !float.IsNaN(fg) && !float.IsInfinity(fg) && fg >= 0f)
              rawFrequencyGPU = fg;
          }

          if (!tempReady && (cpuTempReady || gpuTempReady)) {
            tempReady = true;
            // 首次获取到有效数据立即刷新
            try {
              QueryHardware();
            } catch (Exception ex) {
              Logger.Error($"[UpdateTooltip] QueryHardware 异常: {ex.Message}");
            }
            UpdateFloatingText();
            UpdateTrayIconText();

            if (customIcon == "dynamic")
              UpdateDynamicIcon();
          }
        }
      };

      hwMonitorProcess.ErrorDataReceived += (s, e) => {
        if (string.IsNullOrEmpty(e.Data)) return;
        Logger.Error("HardwareMonitor [HWMonitor ERR] " + e.Data);
      };

      hwMonitorProcess.EnableRaisingEvents = true;
      hwMonitorProcess.Exited += (s, e) => {
        if (hwMonitorStopping) {
          hwMonitorStopping = false;
          return;
        }
        cpuTempReady = false;
        gpuTempReady = false;
        rawGotGPU = false;
        tempReady = false;
        lastCpuSmoothedSampleUtc = DateTime.MinValue;
        lastGpuSmoothedSampleUtc = DateTime.MinValue;
        //Logger.Info("StartHardwareMonitor [HWMonitor] 进程退出，准备重启...");
        System.Threading.Tasks.Task.Delay(3000).ContinueWith(_ => {
          try { StartHardwareMonitor(); } catch { }
        });
      };

      try {
        hwMonitorProcess.Start();
        hwMonitorIn = hwMonitorProcess.StandardInput;
        hwMonitorProcess.BeginOutputReadLine();
        hwMonitorProcess.BeginErrorReadLine(); // 必须读取错误流避免死锁
        SetGpuMonitorState(monitorGPU);
        SetCpuMonitorState(monitorCPU);
        SetMonitorInterval(monitorRefreshRate == "high" ? 250 : 1000);
      } catch (Exception) { }
    }

    static void SetGpuMonitorState(bool enable) {
      if (hwMonitorIn != null && hwMonitorProcess != null && !hwMonitorProcess.HasExited) {
        try { hwMonitorIn.WriteLine(enable ? "GPU:ON" : "GPU:OFF"); } catch { }
      }
    }

    static void SetCpuMonitorState(bool enable) {
      if (hwMonitorIn != null && hwMonitorProcess != null && !hwMonitorProcess.HasExited) {
        try { hwMonitorIn.WriteLine(enable ? "CPU:ON" : "CPU:OFF"); } catch { }
      }
    }

    static void SetMonitorInterval(int ms) {
      if (hwMonitorIn != null && hwMonitorProcess != null && !hwMonitorProcess.HasExited) {
        try { hwMonitorIn.WriteLine($"INTERVAL:{ms}"); } catch { }
      }
    }

    static void StopHardwareMonitor() {
      if (hwMonitorProcess != null && !hwMonitorProcess.HasExited) {
        hwMonitorStopping = true;
        try { hwMonitorProcess.Kill(); } catch { hwMonitorStopping = false; }
      }
    }

    static int flagStart = 0;
    static void optimiseSchedule() {
      // 延时等待风扇恢复响应
      if (flagStart < 5) {
        flagStart++;
        if (fanControl.Contains("max")) {
          SetMaxFanSpeedOn();
        } else if (TryParseFanRpmSetting(fanControl, out int rpmValue)) {
          SetMaxFanSpeedOff();
          SetFanLevel(rpmValue / 100, rpmValue / 100, Is3FanNb);
        } else if (fanControl.Contains("RPM")) {
          fanControl = "auto";
          SetMaxFanSpeedOff();
          fanControlTimer?.Change(0, 1000);
        }
      }

      //定时通信避免功耗锁定
      if (GetFanCount(out bool ocp, out bool otp)) {
        if (ocp || otp) {
          Logger.Info($"BIOS 保护状态 - 过流: {ocp}, 过温: {otp}");
        }
      } else {
        Logger.Error("无法读取 BIOS 保护状态");
      }

      //更新显示器连接到显卡状态
      monitorQuery();
    }

    static void OnPowerChange(object s, PowerModeChangedEventArgs e) {
      // 休眠重新启动
      if (e.Mode == PowerModes.Resume) {
        Logger.Info("系统已恢复启动。");
        GetFanCount(out bool ocp, out bool otp);

        tooltipUpdateTimer.Start();
        countRestore = 3;
      }

      // 检查电源模式是否发生变化
      if (e.Mode == PowerModes.StatusChange) {
        // 获取当前电源连接状态
        var powerLineStatus = SystemInformation.PowerStatus.PowerLineStatus;
        if (powerOnline != (powerLineStatus == PowerLineStatus.Online)) {
          powerOnline = powerLineStatus == PowerLineStatus.Online;
          if (powerOnline) {
            Logger.Info("笔记本已连接到电源。");
            RestorePowerConfig();
          } else {
            Logger.Info("笔记本已断开电源。");
          }
        }
      }
    }

    // ── 托盘图标滚轮钩子 ─────────────────────────────────────────────────
    static void InstallTrayScrollHook() {
      if (_mouseHook != IntPtr.Zero) return;

      // 创建专用 marshal 控件并强制创建窗口句柄，确保 BeginInvoke 可用
      _invokeTarget = new Control();
      _invokeTarget.CreateControl(); // 强制创建 HWND

      _mouseHookProc = TrayScrollHookProc;
      _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookProc, GetModuleHandle(null), 0);
    }

    static void UninstallTrayScrollHook() {
      if (_mouseHook == IntPtr.Zero) return;
      UnhookWindowsHookEx(_mouseHook);
      _mouseHook = IntPtr.Zero;
      _invokeTarget?.Dispose();
      _invokeTarget = null;
    }

    // 获取托盘图标屏幕矩形；失败时返回 Rectangle.Empty
    static Rectangle GetTrayIconRect() {
      try {
        // NotifyIcon 内部 hWnd 通过反射取得
        var windowField = typeof(NotifyIcon).GetField("window",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (windowField == null) return Rectangle.Empty;
        var nativeWindow = windowField.GetValue(trayIcon) as System.Windows.Forms.NativeWindow;
        if (nativeWindow == null) return Rectangle.Empty;
        IntPtr hWnd = nativeWindow.Handle;

        var idField = typeof(NotifyIcon).GetField("id",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        uint id = idField != null ? (uint)(int)idField.GetValue(trayIcon) : 1u;

        var nid = new NOTIFYICONIDENTIFIER {
          cbSize = (uint)Marshal.SizeOf(typeof(NOTIFYICONIDENTIFIER)),
          hWnd = hWnd,
          uID = id
        };
        RECT rc;
        if (Shell_NotifyIconGetRect(ref nid, out rc) == 0) {
          return Rectangle.FromLTRB(rc.left, rc.top, rc.right, rc.bottom);
        }
      } catch { }
      return Rectangle.Empty;
    }

    static IntPtr TrayScrollHookProc(int nCode, IntPtr wParam, IntPtr lParam) {
      if (nCode >= 0 && (int)wParam == WM_MOUSEWHEEL) {
        var info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
        Rectangle iconRect = GetTrayIconRect();

        bool isOverTray = !iconRect.IsEmpty && iconRect.Contains(info.pt);

        if (isOverTray) {
          // mouseData 高 16 位为滚轮增量，向上为正
          int delta = (short)((info.mouseData >> 16) & 0xFFFF);
          // 用专用 marshal 控件切回 UI 线程（ContextMenuStrip 在首次打开前没有 HWND）
          bool up = delta > 0;
          _invokeTarget?.BeginInvoke(new Action(() => CyclePresetByScroll(up)));
        }
      }
      return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    // scrollUp=true 向上滚（候选列表向前）；false 向下滚（向后）
    static void CyclePresetByScroll(bool scrollUp) {
      // 右键菜单打开时不切换，避免误操作
      if (trayIcon?.ContextMenuStrip != null && trayIcon.ContextMenuStrip.Visible) return;

      var candidates = GetOmenKeyPresetCandidateKeys();
      if (candidates.Count == 0) return;

      int idx = candidates.IndexOf(currentPreset);
      if (idx < 0) idx = 0;
      int next = scrollUp
          ? (idx - 1 + candidates.Count) % candidates.Count
          : (idx + 1) % candidates.Count;
      string targetPreset = candidates[next];
      if (targetPreset != currentPreset) {
        applyPresetLogic(targetPreset);
      } else {
        UpdateTrayIconText();
      }
    }

    static void TrayIcon_MouseClick(object sender, MouseEventArgs e) {
      if (e.Button == MouseButtons.Left) {
        ToggleFloatingBar();
      }
    }

    static bool CheckCustomIcon() {
      string currentPath = AppDomain.CurrentDomain.BaseDirectory;
      string iconPath = Path.Combine(currentPath, "custom.ico");
      // 检查图标文件是否存在
      if (File.Exists(iconPath)) {
        return true;
      } else {
        MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.NoCustomIcon, Strings.Hint, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
      }
    }

    static void SetCustomIcon() {
      string currentPath = AppDomain.CurrentDomain.BaseDirectory;
      string iconPath = Path.Combine(currentPath, "custom.ico");
      // 检查图标文件是否存在
      if (File.Exists(iconPath)) {
        trayIcon.Icon = new Icon(iconPath);
      } else {
        MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.NoCustomIcon, Strings.Hint, MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
    }

    // 根据当前监控状态决定动态图标显示内容：
    // CPU监控开 → CPU温度；CPU关GPU开 → GPU温度；均关 → 原版图标（不改 customIcon 设置）
    static void UpdateDynamicIcon() {
      if (customIcon != "dynamic") return;
      if (trayIcon?.ContextMenuStrip != null && trayIcon.ContextMenuStrip.Visible) return;
      if (monitorCPU) {
        GenerateDynamicIcon((int)CPUTemp);
      } else if (monitorGPU) {
        GenerateDynamicIcon((int)GPUTemp);
      } else {
        trayIcon.Icon = Properties.Resources.smallfan;
      }
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    extern static bool DestroyIcon(IntPtr handle);
    static void GenerateDynamicIcon(int number) {
      // 获取系统推荐的图标尺寸（已适配 DPI）
      Size iconSize = SystemInformation.IconSize;
      int width = iconSize.Width * 2;
      int height = iconSize.Height * 2;

      using (Bitmap bitmap = new Bitmap(width, height)) {
        using (Graphics graphics = Graphics.FromImage(bitmap)) {
          graphics.Clear(Color.Transparent);
          graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

          string text = number.ToString("00");

          using (Font font = new Font("Arial", 45.5f, FontStyle.Bold)) {
            // 测量文本大小
            SizeF textSize = graphics.MeasureString(text, font);

            // 计算居中位置
            float x = (width - textSize.Width) / 2;
            float y = (height - textSize.Height) / 8;

            // 绘制文本
            graphics.DrawString(text, font, Brushes.Tan, x, y);
          }

          // 转换为图标
          IntPtr hIcon = bitmap.GetHicon();
          Icon newIcon;
          using (Icon borrowedIcon = Icon.FromHandle(hIcon)) {
            // Icon.FromHandle does not own the native handle. Clone it before DestroyIcon.
            newIcon = (Icon)borrowedIcon.Clone();
          }
          DestroyIcon(hIcon);

          // 替换托盘图标
          Icon oldIcon = trayIcon.Icon;
          trayIcon.Icon = newIcon;
          // 如果旧图标不是默认图标，则显式释放
          if (oldIcon != null && oldIcon != Properties.Resources.smallfan) {
            oldIcon.Dispose();
          }
        }
      }
    }


    // 状态栏定时更新任务+硬件查询+DB解锁
    static void UpdateTooltip() {
      try {
        QueryHardware();
      } catch (Exception ex) {
        Logger.Error($"[UpdateTooltip] QueryHardware 异常: {ex.Message}");
      }

      if (monitorFan) {
        var latestFanSpeed = GetFanLevel();
        if (latestFanSpeed != null && latestFanSpeed.Count >= 3) {
          lock (fanSpeedNow) {
            fanSpeedNow[0] = latestFanSpeed[0];
            fanSpeedNow[1] = latestFanSpeed[1];
            fanSpeedNow[2] = latestFanSpeed[2];
          }
        }
      }

      UpdateTrayIconText();
      //Console.WriteLine("UpdateTooltip");

      // 同步数据到本地txt
      SyncDataToTxt();

      UpdateFloatingText();

      if (customIcon == "dynamic")
        UpdateDynamicIcon();

      // Debug/Release模式下可能不支持在非UI线程直接修改MenuItem.Text，因此使用Invoke
      // 同时只有当 SysInfo 菜单处于展开状态时，才去进行耗时的查询和更新操作，以节省资源
      ToolStrip parentStrip = irSensorMenu?.GetCurrentParent();
      if (isSysInfoMenuOpen && parentStrip != null) {
        int irTemp = GetSensorTemperature(0);
        int ambientTemp = GetSensorTemperature(1);
        int pchTemp = GetSensorTemperature(2);
        int vrTemp = GetSensorTemperature(3);
        if (parentStrip.InvokeRequired) {
          parentStrip.Invoke(new System.Action(() => {
            if (irSensorMenu != null) irSensorMenu.Text = $"{Strings.SysIRSensor}: {FormatSensorTemperature(irTemp)}";
            if (ambientSensorMenu != null) ambientSensorMenu.Text = $"{Strings.SysAmbient}: {FormatSensorTemperature(ambientTemp)}";
            if (pchSensorMenu != null) pchSensorMenu.Text = $"{Strings.SysPCH}: {FormatSensorTemperature(pchTemp)}";
            if (vrSensorMenu != null) vrSensorMenu.Text = $"{Strings.SysVR}: {FormatSensorTemperature(vrTemp)}";
          }));
        } else {
          if (irSensorMenu != null) irSensorMenu.Text = $"{Strings.SysIRSensor}: {FormatSensorTemperature(irTemp)}";
          if (ambientSensorMenu != null) ambientSensorMenu.Text = $"{Strings.SysAmbient}: {FormatSensorTemperature(ambientTemp)}";
          if (pchSensorMenu != null) pchSensorMenu.Text = $"{Strings.SysPCH}: {FormatSensorTemperature(pchTemp)}";
          if (vrSensorMenu != null) vrSensorMenu.Text = $"{Strings.SysVR}: {FormatSensorTemperature(vrTemp)}";
        }
      }

      // 启用再禁用DB驱动
      if (countDB > 0) {
        countDB--;
        // 提前判断是否符合条件
        if (CPUPower > 0 && CPUPower < CPULimitDB) {
          float[] limits = GetGpuPowerLimits();   // limits[0] = Current, limits[1] = Max
          if (!powerOnline || Math.Abs(limits[1] - limits[0]) < 1f)
            countDB = 0;
        }
        if (tryTimes == 0)
          performanceControlMenu.ToolTipText = Strings.UnavailableReasonTip(countDB + 1);
        else
          performanceControlMenu.ToolTipText = Strings.UnavailableRetryTip(countDB + 1, tryTimes, maxRetry);
        if (countDB == 0) {
          ChangeDBState(false);

          float[] limits = GetGpuPowerLimits();   // limits[0] = Current, limits[1] = Max
          // 检查显卡当前功耗限制，离电时当作解锁成功
          if (powerOnline && Math.Abs(limits[1] - limits[0]) > 1f) {
            tryTimes++;
            // 失败时重试maxRetry次
            if (tryTimes > maxRetry) {
              tryTimes = 0;
              if (CPUPower > CPULimitDB + 10)
                MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.DbUnlockCpuHighWarning, Strings.Hint, MessageBoxButtons.OK, MessageBoxIcon.Warning);
              else
                MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.DbUnlockFailed(limits[0]), Strings.Hint, MessageBoxButtons.OK, MessageBoxIcon.Warning);
              ChangeDBState(true);
              DBVersion = 2;
              countDB = 0;
              performanceControlMenu.Enabled = true;
              performanceControlMenu.ToolTipText = "";
              SaveConfig("DBVersion");
              UpdateCheckedState("DBGroup", Strings.DbNormal);
            } else {
              countDB = countDBInit;
              // 启用DB驱动
              ChangeDBState(true);
              SetGpuPowerState(true, true);
            }
          } else {
            tryTimes = 0;
            performanceControlMenu.Enabled = true;
            performanceControlMenu.ToolTipText = "";
            if (autoStart == "off") {
              MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.DbUnlockSuccessNoAutoStart, Strings.Hint, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            //MessageBox.Show($"解锁成功！\n当前最大显卡功耗锁定为：{-powerLimits:F2} W ！", Strings.Hint, MessageBoxButtons.OK, MessageBoxIcon.Information);
          }
          if (tryTimes == 0) {
            // 恢复CPU功耗设定
            RestoreCPUPower();
            // 恢复GPU功耗设定
            SetGpuPowerState(tgpPower == "on", ppabPower == "on", dState == "normal" ? 1 : 2);
          }
        } else if (countDB == countDBInit - 1) {
          if (isCPUPowerControlSupported) SetCpuPowerLimit((byte)CPULimitDB);
        }
      }

      // 从休眠中启动后恢复配置
      if (countRestore > 0) {
        countRestore--;
        if (countRestore == 0) {
          RestoreConfig();
        }
      }
    }

    static void SyncDataToTxt() {
      if (dataLocalize != "on") return;
      if (Interlocked.CompareExchange(ref _isSyncingDataToTxt, 1, 0) != 0) return;

      string cpuText = ((int)Math.Round(CPUTemp)).ToString();
      string gpuText = ((int)Math.Round(GPUTemp)).ToString();
      string fanText;
      lock (fanSpeedNow) {
        fanText = ((fanSpeedNow[0] + fanSpeedNow[1]) * 50).ToString();
      }

      System.Threading.Tasks.Task.Run(() => {
        try {
          string basePath = AppDomain.CurrentDomain.BaseDirectory;

          try {
            if (lastCpuText == null || lastCpuText != cpuText) {
              File.WriteAllText(Path.Combine(basePath, "cpu_temp.txt"), cpuText);
              lastCpuText = cpuText;
            }
          } catch (Exception ex) {
            Logger.Error($"Sync error when writing cpu_temp.txt: {ex.Message}");
          }

          try {
            if (lastGpuText == null || lastGpuText != gpuText) {
              File.WriteAllText(Path.Combine(basePath, "gpu_temp.txt"), gpuText);
              lastGpuText = gpuText;
            }
          } catch (Exception ex) {
            Logger.Error($"Sync error when writing gpu_temp.txt: {ex.Message}");
          }

          try {
            if (lastFanText == null || lastFanText != fanText) {
              File.WriteAllText(Path.Combine(basePath, "fan_rpm.txt"), fanText);
              lastFanText = fanText;
            }
          } catch (Exception ex) {
            Logger.Error($"Sync error when writing fan_rpm.txt: {ex.Message}");
          }
        } catch (Exception ex) {
          Logger.Error("Sync error: " + ex.Message);
        } finally {
          Interlocked.Exchange(ref _isSyncingDataToTxt, 0);
        }
      });
    }

    static void CancelPendingHardwareApplies() {
      lock (deferredHardwareApplyLock) {
        deferredHardwareApplyGeneration++;
      }
    }

    static void ScheduleLatestHardwareApply(string key, Action action, int delayMs = 200) {
      int version;
      int generation;
      lock (deferredHardwareApplyLock) {
        deferredHardwareApplyVersions.TryGetValue(key, out int currentVersion);
        version = currentVersion + 1;
        deferredHardwareApplyVersions[key] = version;
        generation = deferredHardwareApplyGeneration;
      }

      System.Threading.Tasks.Task.Run(async () => {
        await System.Threading.Tasks.Task.Delay(delayMs);
        lock (deferredHardwareApplyLock) {
          if (generation != deferredHardwareApplyGeneration ||
              !deferredHardwareApplyVersions.TryGetValue(key, out int currentVersion) ||
              currentVersion != version)
            return;
        }

        try {
          action();
        } catch (Exception ex) {
          Logger.Error($"Deferred hardware apply ({key}) failed: {ex.Message}");
        }
      });
    }

    static void ApplyHardwareSettingNow(string key, Action action) {
      lock (deferredHardwareApplyLock) {
        deferredHardwareApplyVersions.TryGetValue(key, out int currentVersion);
        deferredHardwareApplyVersions[key] = currentVersion + 1;
      }

      try {
        action();
      } catch (Exception ex) {
        Logger.Error($"Hardware apply ({key}) failed: {ex.Message}");
      }
    }

    // 硬件传感器查询
    private static int _isQuerying = 0; // 防重入标志，支持 Interlocked 原子操作
    static int countQuery = 0;
    static bool autoStartMonitorGPU = true, autoStopMonitorGPU = true;//是否自动根据情况开/关GPU温度监测以节约能源
    static bool hasStartAuto = false, hasStopAuto = false;//是否已经自动开/关过GPU温度监测，在手动开/关时重置
    // 用于风扇查表的平滑温度（受高中低档影响）
    static float smoothedCPUTemp = 50f;
    static float smoothedGPUTemp = 40f;

    static float SmoothTemperatureSample(float rawValue, float previousValue, float response,
                                         DateTime sampleUtc, ref DateTime lastSmoothedSampleUtc) {
      if (sampleUtc == DateTime.MinValue || sampleUtc <= lastSmoothedSampleUtc)
        return previousValue;

      if (lastSmoothedSampleUtc == DateTime.MinValue || response >= 0.999f) {
        lastSmoothedSampleUtc = sampleUtc;
        return rawValue;
      }

      double elapsedSeconds = Math.Max(0.05, Math.Min(5.0, (sampleUtc - lastSmoothedSampleUtc).TotalSeconds));
      double baseRetention = Math.Max(0.0, Math.Min(0.999999, 1.0 - response));
      float alpha = (float)(1.0 - Math.Pow(baseRetention, elapsedSeconds));
      lastSmoothedSampleUtc = sampleUtc;
      return rawValue * alpha + previousValue * (1.0f - alpha);
    }

    static void QueryHardware() {
      // 防止定时器重入：上次查询未完成时直接跳过本次
      if (Interlocked.CompareExchange(ref _isQuerying, 1, 0) != 0)
        return;

      try {
      float tempCPU = rawTempCPU;
      bool getGPU = false;

      DateTime queryUtc = DateTime.UtcNow;
      if (monitorCPU && cpuTempReady) {
        CPUPower = queryUtc - lastCpuPowerSampleUtc <= hardwareSampleTimeout ? rawPowerCPU : 0f;
        CPUFrequency = rawFrequencyCPU;
      }
      if (monitorGPU) {
        getGPU = rawGotGPU;
        if (getGPU) {
          if (queryUtc - lastGpuPowerSampleUtc > hardwareSampleTimeout || (int)(rawPowerGPU * 10) == 5900)
            GPUPower = 0;
          else
            GPUPower = rawPowerGPU;
          GPUFrequency = rawFrequencyGPU;
        } else {
          GPUFrequency = 0f;
        }
      }

      // 只在拿到新的温度样本时平滑，并按真实采样间隔换算 alpha。
      // 这样 250ms / 1s 刷新率不会改变“高/中/低响应”的实际热响应速度。
      if (monitorCPU && cpuTempReady) {
        smoothedCPUTemp = SmoothTemperatureSample(
            tempCPU, smoothedCPUTemp, respondSpeed, lastCpuTempSampleUtc, ref lastCpuSmoothedSampleUtc);
      }
      if (monitorGPU && gpuTempReady) {
        smoothedGPUTemp = SmoothTemperatureSample(
            rawTempGPU, smoothedGPUTemp, respondSpeed, lastGpuTempSampleUtc, ref lastGpuSmoothedSampleUtc);
      }

      // 根据显示方式决定展示原始值或平滑值
      if (monitorCPU && cpuTempReady)
        CPUTemp = (tempDisplayMode == "raw") ? tempCPU : smoothedCPUTemp;
      if (monitorGPU && gpuTempReady)
        GPUTemp = (tempDisplayMode == "raw") ? rawTempGPU : smoothedGPUTemp;

      int currentMaxCPUTemp = maxCPUTemp ?? 97;
      int currentMaxGPUTemp = maxGPUTemp ?? 87;
      bool cpuProtectionTriggered = monitorCPU && cpuTempReady && IsFresh(lastCpuTempSampleUtc) &&
                                    rawTempCPU > currentMaxCPUTemp - 2;
      bool gpuProtectionTriggered = monitorGPU && gpuTempReady && IsFresh(lastGpuTempSampleUtc) &&
                                    rawTempGPU > currentMaxGPUTemp - 2;
      if (autoFanProtect == "on" && platformMaxFanSpeed.HasValue &&
          (cpuProtectionTriggered || gpuProtectionTriggered) && fanControl.Contains(" RPM")) {
        // 检查是否满足转速低于平台最大转速80%的条件
        bool fanSpeedCondition = true;
        if (platformMaxFanSpeed.Value > 0) {
          int currentFanSpeed;
          lock (fanSpeedNow) { currentFanSpeed = (fanSpeedNow[0] + fanSpeedNow[1]) * 50; }
          fanSpeedCondition = currentFanSpeed < platformMaxFanSpeed.Value * 0.8;
        }

        if (fanSpeedCondition) {
          // 先切换为降温模式（cool配置）
          fanTable = "cool";
          LoadFanConfig("cool.txt");
          UpdateCheckedState("fanTableGroup", Strings.FanCoolMode);
          SaveConfig("FanTable");

          // 再切换为自动风扇控制
          fanControl = "auto";
          SetMaxFanSpeedOff();
          fanControlTimer.Change(0, 1000);
          UpdateCheckedState("fanControlGroup", Strings.FanAuto);
          SaveConfig("FanControl");

          int protectionLimit = gpuProtectionTriggered ? currentMaxGPUTemp : currentMaxCPUTemp;
          float protectionTemp = gpuProtectionTriggered ? rawTempGPU : rawTempCPU;
          trayIcon.BalloonTipTitle = Strings.HighTempBalloonTitle;
          trayIcon.BalloonTipText = Strings.HighTempBalloonText(protectionLimit, protectionTemp);
          trayIcon.BalloonTipIcon = ToolTipIcon.Warning;
          trayIcon.ShowBalloonTip(3000);
        }
      }

      //通过countQuery延时来确保温度正常读取
      if (countQuery <= 5 && monitorGPU)
        countQuery++;
      ////自动关闭GPU监控
      //if (countQuery > 5 && autoStopMonitorGPU && !isConnectedToNVIDIA && monitorGPU && ((GPUPower >= 0 && GPUPower <= 1.3) || !getGPU)) {
      //  // 如果是NVIDIAGpu平台，进一步检查是否有程序占用GPU
      //  bool isGpuIdle = true;
      //  if (hasNVIDIAGpu) {
      //    var gpuApps = GetGpuApps();
      //    if (gpuApps != null && gpuApps.Count > 0) {
      //      isGpuIdle = false;
      //    }
      //  }

      //  if (isGpuIdle) {
      //    GPUPower = 0;
      //    rawPowerGPU = 0f;
      //    getGPU = false;
      //    hasStopAuto = true;
      //    countQuery = 0;
      //    monitorGPU = false;
      //    gpuTempReady = false; // 关闭后温度不再有效
      //                          //重置自动开启标志
      //    hasStartAuto = false;
      //    autoStartMonitorGPU = true;
      //    SetGpuMonitorState(false);
      //    UpdateCheckedState("monitorGPUGroup", Strings.MonitorGpuOff);
      //    SaveConfig("MonitorGPU");

      //    // 设置通知的文本和标题
      //    trayIcon.BalloonTipTitle = Strings.GpuAutoStopTitle;
      //    trayIcon.BalloonTipText = Strings.GpuAutoStopText;
      //    trayIcon.BalloonTipIcon = ToolTipIcon.Info; // 图标类型
      //    trayIcon.ShowBalloonTip(3000); // 显示气泡通知，持续时间为 3 秒
      //  }
      //}
      ////自动开启GPU监控：需为自动转速控制且从"未连接显示器"切换为"已连接"时才触发
      //if (autoStartMonitorGPU && isConnectedToNVIDIA && !prevIsConnectedToNVIDIA && !monitorGPU && fanControl == "auto") {
      //  GPUPower = 0;
      //  rawPowerGPU = 0f;
      //  hasStartAuto = true;
      //  countQuery = 0;
      //  monitorGPU = true;
      //  gpuTempReady = false; // 等待获取到温度后再参与风扇控制
      //  //重置自动关闭标志
      //  hasStopAuto = false;
      //  autoStopMonitorGPU = true;
      //  SetGpuMonitorState(true);
      //  UpdateCheckedState("monitorGPUGroup", Strings.MonitorGpuOn);
      //  SaveConfig("MonitorGPU");

      //  // 设置通知的文本和标题
      //  trayIcon.BalloonTipTitle = Strings.GpuAutoStopTitle;
      //  trayIcon.BalloonTipText = Strings.GpuAutoStartText;
      //  trayIcon.BalloonTipIcon = ToolTipIcon.Info; // 图标类型
      //  trayIcon.ShowBalloonTip(3000); // 显示气泡通知，持续时间为 3 秒
      //}

      // 似乎无法一次性关闭GPU监控及选项
      //if (!monitorGPU) {
      //  SetGpuMonitorState(false);
      //  UpdateCheckedState("monitorGPUGroup", Strings.MonitorGpuOff);
      //}

      prevIsConnectedToNVIDIA = isConnectedToNVIDIA;
      } finally {
        // Never leave the query path permanently disabled after a transient UI/WMI exception.
        Interlocked.Exchange(ref _isQuerying, 0);
      }
    }

    // Helper function to calculate fan speed for a specific temperature map
    static int GetFanSpeedForSpecificTemperature(float temperature, SortedDictionary<float, int> tempFanMap) {
      // 字典已按键升序排列，直接线性扫描，O(n) 但 n 极小（通常 3~6 个点）
      float lowerKey = tempFanMap.Keys.First();
      float upperKey = lowerKey;

      foreach (float key in tempFanMap.Keys) {
        if (key <= temperature) lowerKey = key;
        else { upperKey = key; break; }
        upperKey = key; // 如果循环完也没 break，upper == lower == 最大键
      }

      if (lowerKey == upperKey)
        return tempFanMap[lowerKey];

      int lowerSpeed = tempFanMap[lowerKey];
      int upperSpeed = tempFanMap[upperKey];
      float interpolated = lowerSpeed + (upperSpeed - lowerSpeed) * (temperature - lowerKey) / (upperKey - lowerKey);
      return (int)interpolated;
    }

    static bool IsFresh(DateTime sampleUtc) {
      return sampleUtc != DateTime.MinValue && DateTime.UtcNow - sampleUtc <= hardwareSampleTimeout;
    }

    static bool IsEmergencyThermalState() {
      int cpuLimit = maxCPUTemp ?? 97;
      int gpuLimit = maxGPUTemp ?? 87;
      bool cpuHot = monitorCPU && cpuTempReady && IsFresh(lastCpuTempSampleUtc) && rawTempCPU >= cpuLimit - 3;
      bool gpuHot = monitorGPU && gpuTempReady && IsFresh(lastGpuTempSampleUtc) && rawTempGPU >= gpuLimit - 3;
      return cpuHot || gpuHot;
    }

    static void ApplyAutomaticFanControl() {
      if (fanControl != "auto") return;

      int targetRpm = GetFanSpeedForTemperature();
      if (targetRpm < 0) {
        bool hadValidTemperature = lastCpuTempSampleUtc != DateTime.MinValue || lastGpuTempSampleUtc != DateTime.MinValue;
        bool hasFreshTemperature =
            (monitorCPU && cpuTempReady && IsFresh(lastCpuTempSampleUtc)) ||
            (monitorGPU && gpuTempReady && IsFresh(lastGpuTempSampleUtc));

        // 启动阶段尚未拿到首个样本时不制造满转噪音；一旦曾经正常工作后温度源持续失效，
        // 则优先安全，交给 BIOS 最大风扇模式，直到有效温度恢复。
        if (hadValidTemperature && !hasFreshTemperature && !autoFanSensorFailsafeActive) {
          SetMaxFanSpeedOn();
          autoFanSensorFailsafeActive = true;
          Logger.Error("Automatic fan control lost all fresh temperature sources; max-fan failsafe enabled.");
        }
        return;
      }

      if (autoFanSensorFailsafeActive) {
        SetMaxFanSpeedOff();
        autoFanSensorFailsafeActive = false;
        Logger.Info("Automatic fan temperature source recovered; max-fan failsafe disabled.");
      }

      int target = Math.Max(0, Math.Min(255, targetRpm / 100));
      int current;
      lock (fanSpeedNow) {
        current = Math.Max(0, Math.Min(255, (fanSpeedNow[0] + fanSpeedNow[1]) / 2));
      }

      bool emergency = IsEmergencyThermalState();
      if (emergency && platformMaxFanSpeed.HasValue)
        target = Math.Max(target, Math.Min(255, platformMaxFanSpeed.Value / 100));

      int delta = target - current;
      if (!emergency && Math.Abs(delta) <= AutoFanDeadband) return;

      int next = target;
      if (!emergency) {
        int maxStep = delta > 0 ? AutoFanRiseStep : AutoFanFallStep;
        next = current + Math.Sign(delta) * Math.Min(Math.Abs(delta), maxStep);
      }

      SetFanLevel(next, next, Is3FanNb);
      if (!monitorFan) {
        lock (fanSpeedNow) {
          fanSpeedNow[0] = next;
          fanSpeedNow[1] = next;
        }
      }
    }

    // 根据 floatingBarScreen 获取目标显示器，找不到时回退主屏幕
    static Screen GetFloatingScreen() {
      if (!string.IsNullOrEmpty(floatingBarScreen)) {
        foreach (var s in Screen.AllScreens) {
          if (s.DeviceName == floatingBarScreen) return s;
        }
      }
      return Screen.PrimaryScreen;
    }

    static readonly object _floatingLock = new object();
    // 显示浮窗
    static void ShowFloatingForm() {
      lock (_floatingLock) {
        if (floatingForm == null || floatingForm.IsDisposed) {
          floatingForm = new FloatingForm(monitorText(), textSize, floatingBarLoc, GetFloatingScreen());
          floatingForm.Show();
        } else {
          floatingForm.BringToFront();
        }
      }
    }

    // 关闭浮窗
    static void CloseFloatingForm() {
      lock (_floatingLock) {
        if (floatingForm != null && !floatingForm.IsDisposed) {
          floatingForm.Close();
          floatingForm.Dispose();
          floatingForm = null;
        }
      }
    }

    // 更新浮窗的文字内容
    static void UpdateFloatingText() {
      FloatingForm form;
      lock (_floatingLock) {
        form = floatingForm;
      }

      if (form == null || form.IsDisposed) return;

      string text = monitorText();
      int size = textSize;
      string location = floatingBarLoc;
      Screen screen = GetFloatingScreen();

      if (form.InvokeRequired) {
        if (Interlocked.CompareExchange(ref _floatingUpdatePending, 1, 0) != 0) return;
        try {
          form.BeginInvoke(new Action(() => {
            try {
              if (form.IsDisposed) return;
              form.TopMost = true;
              form.SetText(text, size, location, screen);
            } finally {
              Interlocked.Exchange(ref _floatingUpdatePending, 0);
            }
          }));
        } catch (InvalidOperationException) {
          Interlocked.Exchange(ref _floatingUpdatePending, 0);
        }
        return;
      }

      form.TopMost = true;
      form.SetText(text, size, location, screen);
    }

    //生成监控信息
    static string monitorText() {
      string str = "";
      if (monitorCPU && (showCPUTemp || showCPUPower || showCPUFrequency)) {
        if (cpuTempReady || CPUPower > 0 || CPUFrequency > 0) {
          var cpuParts = new List<string>();
          if (showCPUTemp && cpuTempReady) cpuParts.Add($"{CPUTemp:F1}°C");
          if (showCPUPower && CPUPower > 0) cpuParts.Add($"{CPUPower:F1}W");
          if (showCPUFrequency && CPUFrequency > 0) cpuParts.Add($"{CPUFrequency / 1000f:F1}GHz");
          if (cpuParts.Count > 0) str += $"CPU: {string.Join(", ", cpuParts)}";
          else if (pawnIOState == "RUNNING") str += $"CPU: {Strings.MonitorPrepareLabel}";
        } else {
          if (pawnIOState == "RUNNING")
            str += $"CPU: {Strings.MonitorPrepareLabel}";
          else if (pawnIOState.Length > 0)
            str += $"CPU: PawnIO {pawnIOState}";
        }
      }
      if (monitorGPU && (showGPUTemp || showGPUPower || showGPUFrequency)) {
        if (str.Length > 0) str += "\n";
        if (pawnIOState == "RUNNING" && !gpuTempReady) {
          if (rawPowerGPU < 0)
            str += $"GPU: {Strings.GpuPoweredOff}";
          else
            str += $"GPU: {Strings.MonitorPrepareLabel}";
        }
        else if (pawnIOState.Length > 0)
        {
          var gpuParts = new List<string>();
          if (showGPUTemp && gpuTempReady) gpuParts.Add($"{GPUTemp:F1}°C");
          if (showGPUPower) gpuParts.Add($"{GPUPower:F1}W");
          if (showGPUFrequency && GPUFrequency > 0) gpuParts.Add($"{GPUFrequency:F0}MHz");
          if (gpuParts.Count > 0) str += $"GPU: {string.Join(", ", gpuParts)}";
          else if (pawnIOState == "RUNNING") str += $"GPU: {Strings.MonitorPrepareLabel}";
        }
      }
      if (monitorFan) {
        if (str.Length > 0) str += "\n";
        if (Is3FanNb)
          str += $"Fan:  {fanSpeedNow[0] * 100}, {fanSpeedNow[1] * 100}, {fanSpeedNow[2] * 100}";
        else
          str += $"Fan:  {fanSpeedNow[0] * 100}, {fanSpeedNow[1] * 100}";
      }
      if (str.Length == 0) str = Strings.MonitorClosed;
      return str;
    }

    static void RefreshMonitorDisplay() {
      UpdateTrayIconText();
      if (floatingForm != null) {
        floatingForm.SetText(monitorText(), textSize, floatingBarLoc, GetFloatingScreen());
      }
    }

    static string GetPresetDisplayName(string presetKey) {
      switch (presetKey) {
        case "PresetBalanced": return Strings.PresetBalanced;
        case "PresetExtreme": return Strings.PresetExtreme;
        case "PresetGpuPriority": return Strings.PresetGpuPriority;
        case "PresetLightUse": return Strings.PresetLightUse;
        case "PresetCustom1": return presetCustom1Name;
        case "PresetCustom2": return presetCustom2Name;
        case "PresetCustom3": return presetCustom3Name;
        default: return presetKey;
      }
    }

    static string GetCurrentPresetDisplayName() {
      return GetPresetDisplayName(currentPreset);
    }

    const int NotifyIconTextLimit = 63;

    static List<string> BuildTrayMonitorLines() {
      var lines = new List<string>();

      if (monitorCPU && (showCPUTemp || showCPUPower || showCPUFrequency)) {
        if (cpuTempReady || CPUPower > 0 || CPUFrequency > 0) {
          var cpuParts = new List<string>();
          if (showCPUTemp && cpuTempReady) cpuParts.Add($"{CPUTemp:F0}°C");
          if (showCPUPower && CPUPower > 0) cpuParts.Add($"{CPUPower:F0}W");
          if (showCPUFrequency && CPUFrequency > 0) cpuParts.Add($"{CPUFrequency / 1000f:F1}G");
          if (cpuParts.Count > 0) lines.Add($"CPU {string.Join(" ", cpuParts)}");
          else if (pawnIOState == "RUNNING") lines.Add($"CPU {Strings.MonitorPrepareLabel}");
        } else if (pawnIOState == "RUNNING") {
          lines.Add($"CPU {Strings.MonitorPrepareLabel}");
        } else if (pawnIOState.Length > 0) {
          lines.Add($"CPU PawnIO {pawnIOState}");
        }
      }

      if (monitorGPU && (showGPUTemp || showGPUPower || showGPUFrequency)) {
        if (pawnIOState == "RUNNING" && !gpuTempReady) {
          lines.Add(rawPowerGPU < 0
            ? $"GPU {Strings.GpuPoweredOff}"
            : $"GPU {Strings.MonitorPrepareLabel}");
        } else if (pawnIOState.Length > 0) {
          var gpuParts = new List<string>();
          if (showGPUTemp && gpuTempReady) gpuParts.Add($"{GPUTemp:F0}°C");
          if (showGPUPower) gpuParts.Add($"{GPUPower:F0}W");
          if (showGPUFrequency && GPUFrequency > 0) gpuParts.Add($"{GPUFrequency / 1000f:F1}G");
          if (gpuParts.Count > 0) lines.Add($"GPU {string.Join(" ", gpuParts)}");
          else if (pawnIOState == "RUNNING") lines.Add($"GPU {Strings.MonitorPrepareLabel}");
        }
      }

      if (monitorFan) {
        int fanCount = Is3FanNb ? 3 : 2;
        var fanParts = new List<string>();
        for (int i = 0; i < fanCount; i++)
          fanParts.Add((fanSpeedNow[i] * 100).ToString(CultureInfo.CurrentCulture));
        lines.Add($"Fan {string.Join(" ", fanParts)}");
      }

      if (lines.Count == 0) lines.Add(Strings.MonitorClosed);
      return lines;
    }

    static string EllipsizeUnicode(string value, int maxLength) {
      if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value ?? "";
      if (maxLength <= 0) return "";
      if (maxLength == 1) return "…";

      int safeEnd = 0;
      int[] elementIndexes = StringInfo.ParseCombiningCharacters(value);
      for (int i = 0; i < elementIndexes.Length; i++) {
        int elementEnd = i + 1 < elementIndexes.Length ? elementIndexes[i + 1] : value.Length;
        if (elementEnd > maxLength - 1) break;
        safeEnd = elementEnd;
      }
      return value.Substring(0, safeEnd) + "…";
    }

    static string FormatTrayIconText(string activePresetLabel, string presetName,
        IList<string> monitorLines, int maxLength) {
      string monitor = string.Join("\n", monitorLines ?? new List<string>());
      if (monitor.Length > maxLength)
        return EllipsizeUnicode(monitor, maxLength);

      string presetLabel = activePresetLabel ?? "";
      string name = presetName ?? "";
      int firstLineMaxLength = maxLength - monitor.Length - 1;

      // 至少需要容纳标签和省略号；否则整行预设信息都省略。
      if (firstLineMaxLength < presetLabel.Length + 1)
        return monitor;

      string fullPresetLine = presetLabel + name;
      if (fullPresetLine.Length <= firstLineMaxLength)
        return fullPresetLine + "\n" + monitor;

      int availableNameLength = firstLineMaxLength - presetLabel.Length;
      string shortenedName = EllipsizeUnicode(name, availableNameLength);
      return presetLabel + shortenedName + "\n" + monitor;
    }

    static void UpdateTrayIconText() {
      if (trayIcon == null) return;

      string presetName = GetCurrentPresetDisplayName();
      string text = FormatTrayIconText(
        Strings.ActivePreset, presetName, BuildTrayMonitorLines(), NotifyIconTextLimit);

      // 最终防线：即使未来格式化逻辑发生变化，也不允许 NotifyIcon.Text 超限。
      text = EllipsizeUnicode(text, NotifyIconTextLimit);
      trayIcon.Text = text;
    }

    static void Exit() {
      _pipeCts?.Cancel();
      if (OmenKeyActions.UsesPipe(omenKey)) {
        OmenKeyOff();
      }
      UninstallTrayScrollHook(); // 卸载鼠标钩子
      tooltipUpdateTimer.Stop(); // 停止定时器

      //openComputer.Close();
      StopHardwareMonitor();
      Application.Exit();
    }

    static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e) {
      Logger.Error($"CurrentDomain_UnhandledException: {e.ExceptionObject}");
      MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.CrashMessage);
    }

    static void Application_ThreadException(object sender, ThreadExceptionEventArgs e) {
      Logger.Error($"Application_ThreadException: {e.Exception}");
      MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.CrashMessage);
    }
  }
}
