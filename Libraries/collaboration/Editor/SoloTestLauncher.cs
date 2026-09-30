using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Editor;

namespace TeamCreate;

/// <summary>Opens the disposable copy in a second editor window. It only ever launches a folder that <see cref="SoloCopy"/> made.</summary>
public static class SoloTestLauncher
{
    private static string ExecutablePath()
    {
        try
        {
            string self = Process.GetCurrentProcess().MainModule?.FileName;
            return !string.IsNullOrEmpty(self) && File.Exists(self) ? self : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Memory the system can hand out right now: physical memory not in use, including the standby cache. Read from the runtime's own view of system
    /// memory load, so it needs no native call. Null when the runtime cannot say.
    /// </summary>
    public static long? AvailableMemoryBytes()
    {
        try
        {
            GCMemoryInfo info = GC.GetGCMemoryInfo();
            if (info.TotalAvailableMemoryBytes <= 0 || info.MemoryLoadBytes <= 0)
            {
                return null;
            }
            return Math.Max(0, info.TotalAvailableMemoryBytes - info.MemoryLoadBytes);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public const string HeartbeatFile = "solo-alive.txt";

    /// <summary>
    /// True when the copy's own editor has reported in recently. The copy writes its process id and the time every few seconds
    /// (<see cref="Beat"/>); a stale file, or an id that is no longer a running process, means the window is closed.
    /// </summary>
    public static bool IsRunning(string copyRoot)
    {
        try
        {
            string path = Path.Combine(copyRoot, ".collaboration", HeartbeatFile);
            if (!File.Exists(path))
            {
                return false;
            }
            string[] lines = File.ReadAllLines(path);
            if (lines.Length < 2 || !int.TryParse(lines[0], out int pid) || !DateTime.TryParse(lines[1], null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime at))
            {
                return false;
            }
            if ((DateTime.UtcNow - at).TotalSeconds > 20)
            {
                return false;
            }
            using (Process process = Process.GetProcessById(pid))
            {
                return !process.HasExited;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static DateTime nextBeat;

    /// <summary>Called from the copy's frame tick.</summary>
    public static void Beat(string root)
    {
        if (DateTime.UtcNow < nextBeat)
        {
            return;
        }
        nextBeat = DateTime.UtcNow.AddSeconds(5);
        try
        {
            File.WriteAllLines(Path.Combine(root, ".collaboration", HeartbeatFile), new[] { Environment.ProcessId.ToString(), DateTime.UtcNow.ToString("o") });
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Returns null on success, or a sentence saying what went wrong.</summary>
    public static string Launch(string copyRoot)
    {
        if (!TestHarnessRules.IsDisposableCopy(copyRoot, SoloCopy.HasMarker(copyRoot)))
        {
            return "That folder is not a solo test copy, so it was not opened.";
        }
        string exe = ExecutablePath();
        if (exe == null)
        {
            return "The editor's program file could not be found, so the copy could not be opened. Open " + copyRoot + " from the s&box launcher instead; the test starts by itself.";
        }
        string project = Directory.EnumerateFiles(copyRoot, "*.sbproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (project == null)
        {
            return "The copy has no project file.";
        }
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "-project \"" + project + "\" -novid",
                UseShellExecute = false
            });
            return null;
        }
        catch (Exception ex)
        {
            return "The solo test window could not be opened: " + ex.Message + " Open " + copyRoot + " from the s&box launcher instead; the test starts by itself.";
        }
    }
}
