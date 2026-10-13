using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LaserficheReports.Setup
{
    internal static class InstallerFileSafety
    {
        internal static string FullPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
                throw new IOException("An absolute path is required.");
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        internal static void RequireSamePath(string actual, string expected)
        {
            if (!string.Equals(FullPath(actual), FullPath(expected), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Refused a path outside the dedicated Reports location: " + actual);
            EnsureNoReparsePoints(actual);
        }

        // Check the file AND every ancestor, including ancestors of a missing target.
        // Never follow junctions/symlinks into Laserfiche or another application's tree.
        internal static void EnsureNoReparsePoints(string path)
        {
            string current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                try
                {
                    FileAttributes attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Refused a reparse point: " + current);
                    if ((attributes & FileAttributes.Directory) == 0 &&
                        Environment.OSVersion.Platform == PlatformID.Win32NT)
                    {
                        using (var stream = new FileStream(current, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete))
                        {
                            HandleInformation information;
                            if (!GetFileInformationByHandle(stream.SafeFileHandle, out information))
                                throw new IOException("Could not verify file ownership isolation: " + current);
                            if (information.NumberOfLinks > 1)
                                throw new IOException("Refused a hard-linked file: " + current);
                        }
                    }
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                string? parent = Path.GetDirectoryName(current);
                if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
                current = parent ?? "";
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HandleInformation
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Creation;
            public System.Runtime.InteropServices.ComTypes.FILETIME Access;
            public System.Runtime.InteropServices.ComTypes.FILETIME Write;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInformation information);

        internal static void WriteBytesAtomic(string path, byte[] bytes)
        {
            EnsureNoReparsePoints(path);
            string temporary = path + ".reports-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                }
                EnsureNoReparsePoints(path);
                // Replace the directory entry rather than modifying an existing
                // hard-linked file in place.
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}

