from pathlib import Path

path = Path("Program.Config.cs")
raw = path.read_bytes()
had_bom = raw.startswith(b"\xef\xbb\xbf")
text = raw.decode("utf-8-sig")
use_crlf = "\r\n" in text
text = text.replace("\r\n", "\n")

old = '''      // TPP 延迟 1s 应用，避免与其他设置冲突。硬件写入留在线程池，
      // UI 更新必须 marshal 回 WinForms 线程，且不能再次触发滑块硬件写入。
      string tppSnapshot = tppPower;
      System.Threading.Tasks.Task.Delay(1000).ContinueWith(_ => {
        int? trackValue = null;
        string checkedText = null;

        if (tppSnapshot == "null") {
          checkedText = Strings.NotSet;
        } else if (tppSnapshot == "max") {
          SetConcurrentTdp(254);
          trackValue = 254;
          checkedText = Strings.SetTppSlider;
        } else if (tppSnapshot.Contains(" W")) {
          if (int.TryParse(tppSnapshot.Replace(" W", "").Trim(), out int value) && value >= 20 && value <= 254) {
            SetConcurrentTdp((byte)value);
            trackValue = value;
            checkedText = Strings.SetTppSlider;
          }
        }

        if (_invokeTarget != null && !_invokeTarget.IsDisposed && _invokeTarget.IsHandleCreated) {
          try {
            _invokeTarget.BeginInvoke(new System.Action(() => {
              bool previousSuppression = suppressPerformanceSliderEvents;
              suppressPerformanceSliderEvents = true;
              try {
                if (trackValue.HasValue && tppTrackBar != null)
                  tppTrackBar.Value = Math.Max(tppTrackBar.Minimum, Math.Min(tppTrackBar.Maximum, trackValue.Value));
                if (checkedText != null)
                  UpdateCheckedState("tppPowerGroup", checkedText);
              } finally {
                suppressPerformanceSliderEvents = previousSuppression;
              }
            }));
          } catch (InvalidOperationException) {
            // 应用退出/句柄销毁期间无需再刷新菜单。
          }
        }
      });
'''

new = '''      // TPP 延迟 1s 应用，避免与其他设置冲突。复用 keyed deferred apply，
      // 这样快速切换预设或随后拖动 TPP 滑块时，旧预设的延迟写入会自动失效，
      // 不会在新状态之后“迟到”并覆盖最终硬件值。
      string tppSnapshot = tppPower;
      ScheduleLatestHardwareApply("tppPower", () => {
        int? trackValue = null;
        string checkedText = null;

        if (tppSnapshot == "null") {
          checkedText = Strings.NotSet;
        } else if (tppSnapshot == "max") {
          SetConcurrentTdp(254);
          trackValue = 254;
          checkedText = Strings.SetTppSlider;
        } else if (tppSnapshot.Contains(" W")) {
          if (int.TryParse(tppSnapshot.Replace(" W", "").Trim(), out int value) && value >= 20 && value <= 254) {
            SetConcurrentTdp((byte)value);
            trackValue = value;
            checkedText = Strings.SetTppSlider;
          }
        }

        if (_invokeTarget != null && !_invokeTarget.IsDisposed && _invokeTarget.IsHandleCreated) {
          try {
            _invokeTarget.BeginInvoke(new System.Action(() => {
              bool previousSuppression = suppressPerformanceSliderEvents;
              suppressPerformanceSliderEvents = true;
              try {
                if (trackValue.HasValue && tppTrackBar != null)
                  tppTrackBar.Value = Math.Max(tppTrackBar.Minimum, Math.Min(tppTrackBar.Maximum, trackValue.Value));
                if (checkedText != null)
                  UpdateCheckedState("tppPowerGroup", checkedText);
              } finally {
                suppressPerformanceSliderEvents = previousSuppression;
              }
            }));
          } catch (InvalidOperationException) {
            // 应用退出/句柄销毁期间无需再刷新菜单。
          }
        }
      }, 1000);
'''

if old not in text:
    raise SystemExit("expected TPP delayed-apply block not found")
text = text.replace(old, new, 1)

if use_crlf:
    text = text.replace("\n", "\r\n")
data = text.encode("utf-8")
if had_bom:
    data = b"\xef\xbb\xbf" + data
path.write_bytes(data)
