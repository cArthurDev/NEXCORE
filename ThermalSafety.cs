namespace NEXCORE;

internal static class ThermalSafety
{
    public static bool MustStop(bool cpuRequired, bool gpuRequired, float? cpu, float? gpu, double cpuLimit, double gpuLimit) =>
        (cpuRequired && (!cpu.HasValue || !float.IsFinite(cpu.Value) || cpu <= 0 || cpu >= cpuLimit)) ||
        (gpuRequired && (!gpu.HasValue || !float.IsFinite(gpu.Value) || gpu <= 0 || gpu >= gpuLimit));
}
