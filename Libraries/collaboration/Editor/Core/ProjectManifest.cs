using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace TeamCreate;

public static class ProjectManifest
{
    private static readonly string[] IncludeRoots = new string[5] { "Assets", "Code", "Libraries", "ProjectSettings", "Editor" };

    private static readonly string[] ExcludedDirectories = new string[7] { "obj", "bin", ".sbox", "Properties", "TestResults", ".collaboration", ".git" };

    private static readonly string[] ExcludedExtensions = new string[6] { ".user", ".csproj", ".dll", ".pdb", ".cache", ".tmp" };

    public static IEnumerable<string> EnumerateInputs(string projectRoot)
    {
        bool incomplete;
        return EnumerateInputs(projectRoot, null, out incomplete);
    }

    private static IEnumerable<string> EnumerateInputs(string projectRoot, Func<string, string[]> listDirectories, out bool incomplete)
    {
        List<string> list = new List<string>();
        incomplete = false;
        if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
        {
            return list;
        }
        list.AddRange(Walk(projectRoot, listDirectories, out incomplete));
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    public static string Compute(string projectRoot)
    {
        bool incomplete;
        return Compute(projectRoot, null, out incomplete);
    }

    internal static string Compute(string projectRoot, Func<string, string[]> listDirectories, out bool incomplete)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
        {
            incomplete = true;
            return "";
        }
        IEnumerable<string> enumerable = EnumerateInputs(projectRoot, listDirectories, out incomplete);
        if (incomplete)
        {
            return "";
        }
        string fullPath = Path.GetFullPath(projectRoot);
        List<string> list = new List<string>();
        foreach (string item in enumerable)
        {
            byte[] array = ReadBytesOrNull(Path.Combine(fullPath, item.Replace('/', Path.DirectorySeparatorChar)));
            if (array == null)
            {
                incomplete = true;
                return "";
            }
            list.Add(item + "\n" + Sha256Hex(MetaFileRules.Canonical(item, array)));
        }
        return HashLines(list);
    }

    public static string HashLines(IEnumerable<string> lines)
    {
        StringBuilder stringBuilder = new StringBuilder();
        if (lines != null)
        {
            foreach (string line in lines)
            {
                stringBuilder.Append(line).Append('\n');
            }
        }
        return TruncateHex(SHA256.HashData(Encoding.UTF8.GetBytes(stringBuilder.ToString())));
    }

    private static List<string> Walk(string projectRoot, Func<string, string[]> listDirectories, out bool incomplete)
    {
        List<string> list = new List<string>();
        string fullPath = Path.GetFullPath(projectRoot);
        incomplete = false;
        HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        hashSet.Add(PhysicalPath(fullPath));
        Stack<(string, string)> stack = new Stack<(string, string)>();
        stack.Push((fullPath, ""));
        while (stack.Count > 0)
        {
            var (text, text2) = stack.Pop();
            string[] files;
            string[] array;
            try
            {
                files = Directory.GetFiles(text);
                array = ((listDirectories == null) ? Directory.GetDirectories(text) : listDirectories(text));
            }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
            {
                incomplete = true;
                continue;
            }
            string[] array2 = files;
            foreach (string path in array2)
            {
                string fileName = Path.GetFileName(path);
                if (!IsExcludedFile(fileName) && (text2.Length != 0 || fileName.EndsWith(".sbproj", StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add((text2.Length == 0) ? fileName : (text2 + "/" + fileName));
                }
            }
            List<(string, string)> list2 = new List<(string, string)>();
            string[] array3 = array;
            foreach (string text3 in array3)
            {
                string fileName2 = Path.GetFileName(text3);
                if (!IsExcludedDirectory(fileName2) && (text2.Length != 0 || IncludeRoots.Contains(fileName2, StringComparer.OrdinalIgnoreCase)))
                {
                    string item = PhysicalPath(text3);
                    if (hashSet.Add(item))
                    {
                        list2.Add((text3, (text2.Length == 0) ? fileName2 : (text2 + "/" + fileName2)));
                    }
                }
            }
            foreach (var item2 in list2.OrderBy(((string Full, string Relative) x) => x.Relative, StringComparer.Ordinal))
            {
                stack.Push(item2);
            }
        }
        return list;
    }

    private static bool IsExcludedFile(string name)
    {
        string[] excludedExtensions = ExcludedExtensions;
        foreach (string value in excludedExtensions)
        {
            if (name.EndsWith(value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsExcludedDirectory(string name)
    {
        string[] excludedDirectories = ExcludedDirectories;
        foreach (string b in excludedDirectories)
        {
            if (string.Equals(name, b, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string PhysicalPath(string directory)
    {
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                FileSystemInfo fileSystemInfo = Directory.ResolveLinkTarget(directory, returnFinalTarget: true);
                if (fileSystemInfo != null)
                {
                    return Path.GetFullPath(fileSystemInfo.FullName);
                }
            }
        }
        catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
        {
        }
        return Path.GetFullPath(directory);
    }

    private static byte[] ReadBytesOrNull(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
        {
            return null;
        }
    }

    private static string Sha256Hex(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string TruncateHex(byte[] hash)
    {
        return Convert.ToHexString(hash).ToLowerInvariant().Substring(0, 8);
    }
}
