using System;

namespace OmenSuperHub {
  // Pure fan ramp policy. This class performs no IO and cannot write to the EC/BIOS.
  // Values are in 100-RPM units, matching SetFanLevel and fanSpeedNow.
  public static class FanControlPolicy {
    public const int Deadband = 2;
    public const int FallStep = 2;

    // Returns -1 to skip a write when the non-emergency change is inside the deadband.
    // Emergency paths always return the full target, including when decreasing.
    public static int CalculateNextOrSkip(int target, int current, bool emergency) {
      target = Math.Max(0, Math.Min(255, target));
      current = Math.Max(0, Math.Min(255, current));
      int delta = target - current;

      if (!emergency && Math.Abs(delta) <= Deadband)
        return -1;
      if (!emergency && delta < 0)
        return current - Math.Min(Math.Abs(delta), FallStep);
      return target;
    }
  }
}
