from pathlib import Path

path = Path("App/GpuAppManager.cs")
raw = path.read_bytes()
had_bom = raw.startswith(b"\xef\xbb\xbf")
text = raw.decode("utf-8-sig")
use_crlf = "\r\n" in text
text = text.replace("\r\n", "\n")

old = "    private const int DefaultCommandTimeoutMilliseconds = 60000;\n"
new = (
    "    private const int DefaultCommandTimeoutMilliseconds = 60000;\n"
    "    private const int DriverCommandTimeoutMilliseconds = 120000;\n"
)
if old not in text:
    raise SystemExit("timeout constant anchor not found")
text = text.replace(old, new, 1)

old = """          string capturedError;
          lock (error) capturedError = error.ToString();

          return new ProcessResult {
            ExitCode = -1,
            Output = output.ToString(),
            Error = string.IsNullOrWhiteSpace(capturedError)
"""
new = """          string capturedOutput;
          string capturedError;
          lock (output) capturedOutput = output.ToString();
          lock (error) capturedError = error.ToString();

          return new ProcessResult {
            ExitCode = -1,
            Output = capturedOutput,
            Error = string.IsNullOrWhiteSpace(capturedError)
"""
if old not in text:
    raise SystemExit("timeout snapshot anchor not found")
text = text.replace(old, new, 1)

old = """        process.WaitForExit();
        stopwatch.Stop();

        return new ProcessResult {
          ExitCode = process.ExitCode,
          Output = output.ToString(),
          Error = error.ToString(),
"""
new = """        process.WaitForExit();
        stopwatch.Stop();

        string finalOutput;
        string finalError;
        lock (output) finalOutput = output.ToString();
        lock (error) finalError = error.ToString();

        return new ProcessResult {
          ExitCode = process.ExitCode,
          Output = finalOutput,
          Error = finalError,
"""
if old not in text:
    raise SystemExit("normal snapshot anchor not found")
text = text.replace(old, new, 1)

old = '        ExecuteCommand($"pnputil /add-driver \\"{driverFile}\\" /install /force");\n'
new = '        ExecuteCommand($"pnputil /add-driver \\"{driverFile}\\" /install /force", DriverCommandTimeoutMilliseconds);\n'
if old not in text:
    raise SystemExit("pnputil add anchor not found")
text = text.replace(old, new, 1)

old = '          ExecuteCommand($"pnputil /delete-driver \\"{name}\\" /uninstall /force");\n'
new = '          ExecuteCommand($"pnputil /delete-driver \\"{name}\\" /uninstall /force", DriverCommandTimeoutMilliseconds);\n'
if old not in text:
    raise SystemExit("pnputil delete anchor not found")
text = text.replace(old, new, 1)

if use_crlf:
    text = text.replace("\n", "\r\n")
data = text.encode("utf-8")
if had_bom:
    data = b"\xef\xbb\xbf" + data
path.write_bytes(data)
