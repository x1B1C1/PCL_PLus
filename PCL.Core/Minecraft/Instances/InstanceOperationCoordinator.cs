using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PCL.Core.Minecraft.Instances;

/// <summary>
/// Coordinates long-running instance operations inside this launcher and against other launcher windows.
/// Only the roots given are locked; an externally launched game is not touched.
/// </summary>
public static class InstanceOperationCoordinator
{
    public static IDisposable Acquire(string coordinationDirectory, params string[] roots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coordinationDirectory);
        ArgumentNullException.ThrowIfNull(roots);
        if (roots.Length == 0) throw new ArgumentException("At least one operation root is required.", nameof(roots));

        string[] identities = roots.Select(NormalizeRoot)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Directory.CreateDirectory(coordinationDirectory);
        var locks = new List<FileStream>(identities.Length);
        try
        {
            foreach (string identity in identities)
            {
                string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
                string path = Path.Combine(coordinationDirectory, "op-" + hash[..16] + ".lock");
                locks.Add(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1, FileOptions.DeleteOnClose));
            }

            return new Releaser(locks);
        }
        catch (IOException)
        {
            foreach (FileStream stream in locks) stream.Dispose();
            throw new InvalidOperationException("这个游戏目录正在被另一个实例操作占用，请等它完成后再试。");
        }
        catch
        {
            foreach (FileStream stream in locks) stream.Dispose();
            throw;
        }
    }

    private static string NormalizeRoot(string root) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant();

    private sealed class Releaser(IReadOnlyList<FileStream> locks) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (FileStream stream in locks) stream.Dispose();
        }
    }
}