// Uruchamianie badanych programów i odczyt zasobów procesu (jednakowo dla C++, C# i Javy).
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Runner;

/// <summary>Wynik uruchomienia procesu.</summary>
/// <param name="StartTicks">Znacznik Stopwatch (QPC) tuż przed Process.Start – początek pomiaru czasu startu.</param>
internal sealed record ProcessResult(
    int ExitCode, string Stdout, string Stderr, long StartTicks, double WallMs,
    double PeakWorkingSetMb, double PeakPrivateMb, double CpuMs, int MaxThreads, bool TimedOut);

internal static class ProcessRunner
{
    public static ProcessResult Run(LaunchSpec spec, IEnumerable<string> args, TimeSpan timeout, bool pollThreads = false)
    {
        var psi = new ProcessStartInfo(spec.Exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in spec.PrefixArgs.Concat(args))
            psi.ArgumentList.Add(a);

        long start = Stopwatch.GetTimestamp();
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Nie udało się uruchomić {spec.Exe}");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        int maxThreads = 0;
        bool timedOut = false;
        long deadline = start + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (!process.WaitForExit(pollThreads ? 50 : 500))
        {
            if (pollThreads)
            {
                try
                {
                    process.Refresh();
                    maxThreads = Math.Max(maxThreads, process.Threads.Count);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // proces właśnie się zakończył
                }
            }
            if (Stopwatch.GetTimestamp() > deadline)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
                timedOut = true;
                break;
            }
        }
        process.WaitForExit(); // czeka też na koniec odczytu stdout/stderr
        double wallMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        // Liczniki procesu są dostępne po jego zakończeniu, dopóki uchwyt jest otwarty.
        var (peakWs, peakPrivate) = Native.PeakMemoryMb(process.SafeHandle);
        double cpuMs = Native.CpuMs(process.SafeHandle);
        return new ProcessResult(process.ExitCode, stdout.Result, stderr.Result, start, wallMs,
            peakWs, peakPrivate, cpuMs, maxThreads, timedOut);
    }

    /// <summary>Znacznik Stopwatch (QPC) w ns – ta sama skala co w programach GUI.</summary>
    public static long TicksToNs(long ticks) =>
        ticks / Stopwatch.Frequency * 1_000_000_000L + ticks % Stopwatch.Frequency * 1_000_000_000L / Stopwatch.Frequency;
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Cb, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
    }

    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(SafeProcessHandle process, out ProcessMemoryCounters counters, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process,
        out long creation, out long exit, out long kernel, out long user);

    /// <summary>Szczytowy zestaw roboczy i szczytowa pamięć prywatna (commit) procesu w MB.</summary>
    public static (double WorkingSet, double Private) PeakMemoryMb(SafeProcessHandle process)
    {
        if (!GetProcessMemoryInfo(process, out var c, (uint)Marshal.SizeOf<ProcessMemoryCounters>()))
            return (double.NaN, double.NaN);
        const double mb = 1024.0 * 1024.0;
        return (c.PeakWorkingSetSize / mb, c.PeakPagefileUsage / mb);
    }

    /// <summary>Łączny czas CPU procesu (user + kernel) w ms.</summary>
    public static double CpuMs(SafeProcessHandle process) =>
        GetProcessTimes(process, out _, out _, out long kernel, out long user)
            ? (kernel + user) / 10_000.0
            : double.NaN;
}
