using System.Diagnostics;

namespace ZapretRemote.Services;

public readonly record struct ZapretStatus(bool IsRunning, int[] Pids);

/// <summary>
/// Strictly read-only monitor for zapret (winws.exe) process status.
/// </summary>
public static class ZapretMonitor
{
    public static ZapretStatus GetStatus()
    {
        Process[] processes = [];
        try
        {
            processes = Process.GetProcessesByName("winws");
            if (processes.Length == 0)
                return new ZapretStatus(false, []);

            var pids = new List<int>(processes.Length);
            foreach (var proc in processes)
            {
                try
                {
                    pids.Add(proc.Id);
                }
                catch
                {
                    // Process may have exited between enumeration and Id access.
                }
            }

            return new ZapretStatus(pids.Count > 0, [.. pids]);
        }
        catch
        {
            return new ZapretStatus(false, []);
        }
        finally
        {
            foreach (var proc in processes)
            {
                proc.Dispose();
            }
        }
    }
}
