using System;

namespace OmenSuperHub {
  // Deterministic and hardware-independent timestamp validation.
  public static class TelemetryFreshness {
    public static bool IsFresh(DateTime sampleUtc, DateTime nowUtc, TimeSpan timeout) {
      if (sampleUtc == DateTime.MinValue || timeout < TimeSpan.Zero)
        return false;
      TimeSpan age = nowUtc - sampleUtc;
      return age >= TimeSpan.Zero && age <= timeout;
    }
  }
}
