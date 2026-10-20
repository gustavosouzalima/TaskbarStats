using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarStats;

/// <summary>Uso total de CPU via PDH (mesmo contador do Gerenciador de Tarefas).</summary>
sealed class CpuSensor : IDisposable
{
    const uint PDH_FMT_DOUBLE = 0x00000200;
    const uint PDH_FMT_NOCAP100 = 0x00008000;

    IntPtr query;
    IntPtr counter;

    public CpuSensor()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0) { query = IntPtr.Zero; return; }
        if (PdhAddEnglishCounterW(query, @"\Processor Information(_Total)\% Processor Utility", IntPtr.Zero, out counter) != 0 &&
            PdhAddEnglishCounterW(query, @"\Processor(_Total)\% Processor Time", IntPtr.Zero, out counter) != 0)
        {
            PdhCloseQuery(query);
            query = IntPtr.Zero;
            return;
        }
        PdhCollectQueryData(query);
    }

    /// <returns>0-100, ou -1 se indisponível.</returns>
    public double Read()
    {
        if (query == IntPtr.Zero || PdhCollectQueryData(query) != 0) return -1;
        if (PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, IntPtr.Zero, out var v) != 0) return -1;
        return Math.Clamp(v.doubleValue, 0, 100);
    }

    public void Dispose()
    {
        if (query != IntPtr.Zero) PdhCloseQuery(query);
        query = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PDH_FMT_COUNTERVALUE { public uint CStatus; public double doubleValue; }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] static extern int PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out PDH_FMT_COUNTERVALUE value);
    [DllImport("pdh.dll")] static extern int PdhCloseQuery(IntPtr query);
}

/// <summary>
/// Uso de GPU calculado como o Gerenciador de Tarefas: soma a utilização de todos os processos
/// em cada motor da GPU (3D, Copy, Video Decode...) e retorna o motor mais ocupado.
/// </summary>
sealed class GpuEngineSensor : IDisposable
{
    const uint PDH_FMT_DOUBLE = 0x00000200;
    const uint PDH_FMT_NOCAP100 = 0x00008000;
    const int PDH_MORE_DATA = unchecked((int)0x800007D2);

    IntPtr query;
    IntPtr counter;
    readonly Dictionary<string, double> perEngine = new();

    public GpuEngineSensor()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0) { query = IntPtr.Zero; return; }
        if (PdhAddEnglishCounterW(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out counter) != 0)
        {
            PdhCloseQuery(query);
            query = IntPtr.Zero;
            return;
        }
        PdhCollectQueryData(query);
    }

    /// <returns>0-100, ou -1 se indisponível.</returns>
    public int Read()
    {
        if (query == IntPtr.Zero || PdhCollectQueryData(query) != 0) return -1;

        uint size = 0;
        int rc = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out uint count, IntPtr.Zero);
        if (rc != PDH_MORE_DATA || size == 0) return -1;

        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, buf) != 0) return -1;

            // Instância: pid_1234_luid_0x..._0x..._phys_0_eng_3_engtype_3D -> motor = "luid_..._eng_3"
            perEngine.Clear();
            int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buf + i * itemSize);
                if (item.FmtValue.CStatus > 1) continue;   // PDH_CSTATUS_VALID_DATA / NEW_DATA
                string name = Marshal.PtrToStringUni(item.szName) ?? "";
                int start = name.IndexOf("luid_", StringComparison.Ordinal);
                int end = name.IndexOf("_engtype_", StringComparison.Ordinal);
                if (start < 0 || end < start) continue;
                string engine = name[start..end];
                perEngine[engine] = perEngine.GetValueOrDefault(engine) + item.FmtValue.doubleValue;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }

        double max = perEngine.Count == 0 ? 0 : perEngine.Values.Max();
        return (int)Math.Round(Math.Clamp(max, 0, 100));
    }

    public void Dispose()
    {
        if (query != IntPtr.Zero) PdhCloseQuery(query);
        query = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PDH_FMT_COUNTERVALUE { public uint CStatus; public double doubleValue; }

    [StructLayout(LayoutKind.Sequential)]
    struct PDH_FMT_COUNTERVALUE_ITEM { public IntPtr szName; public PDH_FMT_COUNTERVALUE FmtValue; }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);
    [DllImport("pdh.dll")] static extern int PdhCloseQuery(IntPtr query);
}

static class MemorySensor
{
    public static (int percent, double usedGb, double totalGb) Read()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return (-1, 0, 0);
        const double GB = 1024.0 * 1024 * 1024;
        // Mesmo cálculo do Gerenciador de Tarefas: "Em uso" = total - disponível.
        double used = m.ullTotalPhys - m.ullAvailPhys;
        return ((int)Math.Round(100.0 * used / m.ullTotalPhys), used / GB, m.ullTotalPhys / GB);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
}

/// <summary>GPU NVIDIA via NVML (nvml.dll do driver). Não exige administrador.</summary>
sealed class GpuSensor
{
    IntPtr device;
    public string Name { get; private set; } = "GPU";

    public static GpuSensor? TryOpen()
    {
        try
        {
            if (nvmlInit_v2() != 0) return null;
            if (nvmlDeviceGetHandleByIndex_v2(0, out var dev) != 0) return null;
            var gpu = new GpuSensor { device = dev };
            var buf = new byte[96];
            if (nvmlDeviceGetName(dev, buf, (uint)buf.Length) == 0)
            {
                int end = Array.IndexOf(buf, (byte)0);
                gpu.Name = Encoding.ASCII.GetString(buf, 0, end < 0 ? buf.Length : end);
            }
            return gpu;
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    /// <returns>Campos -1 quando indisponíveis.</returns>
    public (int util, int temp, double vramUsedGb, double vramTotalGb) Read()
    {
        int util = nvmlDeviceGetUtilizationRates(device, out var u) == 0 ? (int)u.gpu : -1;
        int temp = nvmlDeviceGetTemperature(device, 0, out var t) == 0 ? (int)t : -1;
        const double GB = 1024.0 * 1024 * 1024;
        return nvmlDeviceGetMemoryInfo(device, out var m) == 0
            ? (util, temp, m.used / GB, m.total / GB)
            : (util, temp, -1, -1);
    }

    [StructLayout(LayoutKind.Sequential)] struct NvmlUtilization { public uint gpu; public uint memory; }
    [StructLayout(LayoutKind.Sequential)] struct NvmlMemory { public ulong total; public ulong free; public ulong used; }

    [DllImport("nvml.dll")] static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetName(IntPtr device, byte[] name, uint length);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetTemperature(IntPtr device, int sensorType, out uint temp);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);
}
