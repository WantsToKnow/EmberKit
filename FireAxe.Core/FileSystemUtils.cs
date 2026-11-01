// Modified for EmberKit, based on ktxiaok/FireAxe 0.7.3; see MODIFICATIONS.md.
using System;
using System.Collections.Frozen;
using System.Runtime.InteropServices;

namespace FireAxe;

public static class FileSystemUtils
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string name, string existing, IntPtr reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, out FileIdentity info);

    public static void CreateHardLink(string name, string body)
    {
        if (!CreateHardLinkW(name, body, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法创建游戏 VPK 硬链接：" + name);
        if (!IsSameFile(name, body)) throw new IOException("硬链接创建后校验失败：" + name);
    }

    public static bool IsSameFile(string first, string second)
    {
        if (!File.Exists(first) || !File.Exists(second)) return false;
        using var a = File.OpenHandle(first, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var b = File.OpenHandle(second, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(a, out var x) || !GetFileInformationByHandle(b, out var y))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return x.Volume == y.Volume && x.IndexHigh == y.IndexHigh && x.IndexLow == y.IndexLow;
    }
    private static readonly FrozenSet<char> s_invalidPathChars = FrozenSet.ToFrozenSet(Path.GetInvalidPathChars());

    private const int FO_DELETE = 0x0003;
    private const int FOF_ALLOWUNDO = 0x0040;           // Preserve undo information, if possible. 
    private const int FOF_NOCONFIRMATION = 0x0010;      // Show no confirmation dialog box to the user

    // Struct which contains information that the SHFileOperation function uses to perform file operations. 
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.U4)]
        public int wFunc;
        public string pFrom;
        public string pTo;
        public short fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT FileOp);

    public static void MoveToRecycleBin(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (OperatingSystem.IsWindows())
        {
            var fileop = new SHFILEOPSTRUCT();
            fileop.wFunc = FO_DELETE;
            fileop.pFrom = path + '\0' + '\0';
            fileop.fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION;
            var result = SHFileOperation(ref fileop);
            if (result != 0)
            {
                throw new IOException($"Failed to move the file \"{path}\" to Recycle Bin. Error code: {result}");
            }
        }
        else
        {
            Delete(path);
        }
    }

    public static void Delete(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (File.Exists(path))
        {
            File.Delete(path);
        }
        else if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }

    public static void Move(string sourcePath, string targetPath)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(targetPath);

        if (IsSamePath(sourcePath, targetPath))
        {
            return;
        }
        
        Directory.Move(sourcePath, targetPath);
    }

    public static bool Exists(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return File.Exists(path) || Directory.Exists(path);
    }

    // Read the content length through the file handle, including symbolic-link targets.
    public static long GetContentLength(string path)
    {
        using var file = File.OpenRead(path);
        return file.Length;
    }

    public static bool IsValidPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        foreach (char c in path)
        {
            if (s_invalidPathChars.Contains(c))
            {
                return false;
            }
        }

        return true;
    }

    public static void ThrowIfPathInvalid(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!IsValidPath(path))
        {
            throw new InvalidFilePathException(path);
        }
    }

    public static string GetUniqueFileName(string name, string dirPath)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(dirPath);

        if (!Exists(Path.Join(dirPath, name)))
        {
            return name;
        }
        string extension = Path.GetExtension(name);
        string nameNoExt = Path.GetFileNameWithoutExtension(name);
        int i = 1;
        while (true)
        {
            string nameTry = nameNoExt + $"({i})" + extension;
            if (!Exists(Path.Join(dirPath, nameTry)))
            {
                return nameTry;
            }
            checked
            {
                i++;
            }
        }
    }

    public static string GetUniquePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        path = Path.GetFullPath(path);
        var dirPath = Path.GetDirectoryName(path) ?? throw new IOException("Failed to get the directory path.");
        var name = Path.GetFileName(path);
        var newName = GetUniqueFileName(name, dirPath);
        if (newName != name)
        {
            path = Path.Join(dirPath, newName);
        }
        return path;
    }

    private static readonly FrozenSet<char> s_reservedFileNameChars = FrozenSet.ToFrozenSet(['/', '?', '<', '>', '\\', ':', '*', '|', '"']);
    private static readonly FrozenSet<string> s_reservedUppercaseFileNames = FrozenSet.ToFrozenSet(["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"]);

    // may return empty string
    public static string SanitizeFileName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var chars = new char[name.Length];
        int writePos = 0;
        bool checkStart = true;

        foreach (char c in name)
        {
            int cInt = c;

            // starting spaces
            if (checkStart)
            {
                if (c == ' ')
                {
                    continue;
                }
                else
                {
                    checkStart = false;
                }
            }

            // control characters
            if ((cInt >= 0x00 && cInt <= 0x1f) || (cInt >= 0x80 && cInt <= 0x9f))
            {
                continue;
            }
            
            // reserved characters
            if (s_reservedFileNameChars.Contains(c))
            {
                continue;
            }

            chars[writePos] = c;
            writePos++;
        }

        // trailing periods and spaces
        while (writePos > 0)
        {
            char c = chars[writePos - 1];
            if (c == '.' || c == ' ')
            {
                writePos--;
            }
            else
            {
                break;
            }
        }

        string result = new string(chars.AsSpan(0, writePos));
        // reserved file name
        if (s_reservedUppercaseFileNames.Contains(result.ToUpperInvariant()))
        {
            result = "";
        }

        return result;
    }

    public static string NormalizePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return path.Replace('\\', '/');
    }

    public static bool IsSamePath(string path1, string path2)
    {
        ArgumentNullException.ThrowIfNull(path1);
        ArgumentNullException.ThrowIfNull(path2);

        return NormalizePath(path1) == NormalizePath(path2);
    }

    public static string GetFullAndNormalizedPath(string path) => NormalizePath(Path.GetFullPath(path));

    public static bool IsDirectoryEmpty(string dirPath)
    {
        ArgumentNullException.ThrowIfNull(dirPath);

        return !Directory.EnumerateFileSystemEntries(dirPath).Any();
    }

    public static IEnumerable<string> FindEmptyDirectories(string dirPath)
    {
        ArgumentNullException.ThrowIfNull(dirPath);

        var dirQueue = new Queue<string>();
        foreach (string subDir in Directory.EnumerateDirectories(dirPath))
        {
            dirQueue.Enqueue(subDir);
        }

        while (dirQueue.Count > 0)
        {
            string subDir = dirQueue.Dequeue();
            if (IsDirectoryEmpty(subDir))
            {
                yield return subDir;
                continue;
            }
            foreach (string subDir2 in Directory.EnumerateDirectories(subDir))
            {
                dirQueue.Enqueue(subDir2);
            }
        }
    }
}
