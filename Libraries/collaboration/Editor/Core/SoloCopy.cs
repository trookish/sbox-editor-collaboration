using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace TeamCreate;

public sealed record SoloCopyResult(bool Ok, string Path, string Error, int Files, long Bytes);

/// <summary>
/// Builds the disposable copy of a project in which the solo test runs. A copy is a folder under
/// <c>.collaboration/test-copies/</c> that carries a marker file; the session honours the test identity only inside such a folder, so the
/// copy is what makes the test safe to run next to real work. Nothing here is engine code, so CoreTests runs it against temporary folders.
/// </summary>
public static class SoloCopy
{
    public const string FolderName = "Solo";

    public const long DefaultMaxBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>The project folders that make a runnable copy. Everything else (build output, caches, the real project's journals) stays behind.</summary>
    public static readonly string[] Folders = { "Assets", "Code", "Libraries", "ProjectSettings" };

    private static readonly Regex Skipped = new Regex(@"[\\/](obj|bin|Properties|\.sbox)[\\/]|\.csproj$|\.slnx?$|_c$|_d$|\.generated\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A second editor holds about 3.6 GB. Below this much AVAILABLE memory (free plus the standby cache) the test is refused.</summary>
    public const long MinAvailableBytes = 3L * 1024 * 1024 * 1024;

    /// <summary>
    /// The sentence to show when there is not room for a second editor, or null when there is (or when the reading is unavailable, because a
    /// guess must not block a person who has plenty of room).
    /// </summary>
    public static string MemoryAdvice(long? availableBytes)
    {
        if (!availableBytes.HasValue || availableBytes.Value < 0 || availableBytes.Value >= MinAvailableBytes)
        {
            return null;
        }
        double gb = availableBytes.Value / (1024.0 * 1024 * 1024);
        return "Only " + gb.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB of memory is available. The solo test opens a second editor, which needs about 3 GB, so close some programs first. To run it anyway, use Tools/Run-SoloTest.ps1 -Force.";
    }

    public static string CopyRoot(string projectRoot)
    {
        return Path.Combine(projectRoot, ".collaboration", "test-copies", FolderName);
    }

    /// <summary>True for generated or per-machine files that a copy must not carry. The path is relative to the project root.</summary>
    public static bool ShouldSkip(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return true;
        }
        return Skipped.IsMatch("/" + relativePath.Replace('\\', '/'));
    }

    public static string MarkerPath(string copyRoot)
    {
        return Path.Combine(copyRoot, ".collaboration", TestHarnessRules.MarkerFile);
    }

    public static bool HasMarker(string copyRoot)
    {
        try
        {
            return !string.IsNullOrEmpty(copyRoot) && File.Exists(MarkerPath(copyRoot));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The marker's first line is <c>autorun</c> once, right after the copy is built, so the test starts by itself; then <c>manual</c>.</summary>
    public static bool WantsAutorun(string copyRoot)
    {
        try
        {
            string path = MarkerPath(copyRoot);
            return File.Exists(path) && File.ReadLines(path).FirstOrDefault()?.Trim() == "autorun";
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Called as the test starts, so a restart of the editor does not run it again unasked.</summary>
    public static void ConsumeAutorun(string copyRoot)
    {
        try
        {
            string path = MarkerPath(copyRoot);
            if (File.Exists(path))
            {
                string[] lines = File.ReadAllLines(path);
                if (lines.Length > 0 && lines[0].Trim() == "autorun")
                {
                    lines[0] = "manual";
                    File.WriteAllLines(path, lines);
                }
            }
        }
        catch (Exception)
        {
        }
    }

    public static SoloCopyResult Build(string sourceRoot, bool autorun, long maxBytes = DefaultMaxBytes)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
        {
            return Fail("The project folder could not be found.");
        }
        if (TestHarnessRules.IsDisposableCopyPath(sourceRoot))
        {
            return Fail("This is already a solo test copy. Run the test from here instead of copying it again.");
        }
        string projectFile = ProjectFile.Find(sourceRoot);
        if (projectFile == null)
        {
            return Fail("No .sbproj file was found in the project folder.");
        }
        string copyRoot = CopyRoot(sourceRoot);
        if (!TestHarnessRules.IsDisposableCopyPath(copyRoot))
        {
            return Fail("The copy location is not inside .collaboration/test-copies/, so it was not created.");
        }

        List<(string From, string Rel, long Size)> files = new List<(string, string, long)>();
        long total = 0;
        try
        {
            foreach (string folder in Folders)
            {
                string from = Path.Combine(sourceRoot, folder);
                if (!Directory.Exists(from))
                {
                    continue;
                }
                foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(sourceRoot, file).Replace('\\', '/');
                    if (ShouldSkip(rel))
                    {
                        continue;
                    }
                    long size = new FileInfo(file).Length;
                    files.Add((file, rel, size));
                    total += size;
                    if (total > maxBytes)
                    {
                        return Fail("The project is larger than " + Megabytes(maxBytes) + " MB, which is more than a solo test copy will hold. Use a smaller project, or test with two Steam accounts.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            return Fail("The project could not be read: " + ex.Message);
        }

        if (Directory.Exists(copyRoot))
        {
            if (!HasMarker(copyRoot) && !HoldsNoFiles(copyRoot))
            {
                return Fail("A folder already exists at " + copyRoot + " that is not a solo test copy, so it was left alone. Move or delete it and try again.");
            }
            string cleared = Clear(copyRoot);
            if (cleared != null)
            {
                return Fail(cleared);
            }
        }

        try
        {
            foreach ((string from, string rel, long _) in files)
            {
                string to = Path.Combine(copyRoot, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                File.Copy(from, to, true);
            }
            File.Copy(projectFile, Path.Combine(copyRoot, Path.GetFileName(projectFile)), true);
            Directory.CreateDirectory(Path.Combine(copyRoot, ".collaboration"));
            File.WriteAllLines(MarkerPath(copyRoot), new[]
            {
                autorun ? "autorun" : "manual",
                "Disposable copy made for the solo test. Safe to delete. It is never used for real work."
            });
        }
        catch (Exception ex)
        {
            return Fail("The solo test copy could not be written: " + ex.Message);
        }
        return new SoloCopyResult(true, copyRoot, null, files.Count + 1, total);

        static SoloCopyResult Fail(string message)
        {
            return new SoloCopyResult(false, null, message, 0, 0);
        }
    }

    /// <summary>True when the folder holds directories only. Nothing there can be somebody's work, so it is safe to reclaim.</summary>
    private static bool HoldsNoFiles(string folder)
    {
        try
        {
            return !Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any();
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Empties the copy folder. The marker is removed LAST: if a file is held open (a terminal or editor still inside the old copy) the delete stops
    /// partway, and a folder that has lost its marker but keeps files would be refused as "not a solo test copy" from then on. Keeping the marker until
    /// everything else is gone means a failed replacement leaves a copy that is still recognised, and the next attempt simply tries again.
    /// </summary>
    private static string Clear(string copyRoot)
    {
        string marker = MarkerPath(copyRoot);
        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(copyRoot))
            {
                if (string.Equals(Path.GetFullPath(entry), Path.GetFullPath(Path.Combine(copyRoot, ".collaboration")), StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string inner in Directory.EnumerateFileSystemEntries(entry).Where((string x) => !string.Equals(Path.GetFullPath(x), Path.GetFullPath(marker), StringComparison.OrdinalIgnoreCase)))
                    {
                        Remove(inner);
                    }
                }
                else
                {
                    Remove(entry);
                }
            }
            // The folders themselves are kept, only their contents go. A terminal or editor whose working directory is inside the copy holds that
            // DIRECTORY open, which blocks deleting it but not the files in it; the copy is refilled in place, so it does not matter.
            return null;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return "The previous solo test copy still has a file open, so it could not be replaced. Close the solo test editor window and any terminal or program open inside " + copyRoot + ", then try again. (" + ex.Message + ")";
        }

        static void Remove(string path)
        {
            if (Directory.Exists(path))
            {
                foreach (string inner in Directory.EnumerateFileSystemEntries(path))
                {
                    Remove(inner);
                }
                try
                {
                    Directory.Delete(path, false);
                }
                catch (IOException)
                {
                    // Held open as some process's working directory. It is empty now, so leaving it is harmless.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
            else
            {
                File.Delete(path);
            }
        }
    }

    private static long Megabytes(long bytes)
    {
        return bytes / (1024 * 1024);
    }
}
