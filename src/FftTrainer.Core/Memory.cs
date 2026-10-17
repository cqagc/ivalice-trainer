using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FftTrainer.Core;

public readonly record struct MemoryRegion(ulong Address, ulong Size);

public interface IMemorySource
{
    IEnumerable<MemoryRegion> GetRegions(bool privateOnly = false, MemoryRegion? range = null);
    byte[] ReadBytes(ulong address, int length);
}

public sealed class GameProcess : IMemorySource, IDisposable
{
    private readonly SafeProcessHandle handle;
    public int ProcessId { get; }
    public string ModulePath { get; }
    public ulong ModuleBase { get; }
    public ulong ModuleSize { get; }

    public GameProcess(string name = "FFT_enhanced")
    {
        if (!Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("A 64-bit probe is required.");
        using var process = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name))
            .OrderBy(p => p.Id).FirstOrDefault()
            ?? throw new InvalidOperationException($"Process '{name}' is not running.");
        ProcessId = process.Id;
        handle = Native.OpenProcess(0x0010 | 0x0020 | 0x0008 | 0x0400 | 0x00100000, false, process.Id);  // + SYNCHRONIZE，用于检测进程退出
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcess failed.");
        }
        try
        {
            var module = process.MainModule ?? throw new InvalidOperationException("Main module unavailable.");
            ModulePath = module.FileName;
            ModuleBase = (ulong)module.BaseAddress;
            ModuleSize = (ulong)module.ModuleMemorySize;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public byte[] ReadBytes(ulong address, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var bytes = new byte[length];
        if (length != 0 && (!Native.ReadProcessMemory(handle, (nuint)address, bytes,
            (nuint)length, out var read) || read != (nuint)length))
            throw new IOException($"Read failed at 0x{address:X} (Win32 {Marshal.GetLastWin32Error()}).");
        return bytes;
    }

    public T Read<T>(ulong address) where T : unmanaged =>
        MemoryMarshal.Read<T>(ReadBytes(address, System.Runtime.CompilerServices.Unsafe.SizeOf<T>()));

    public void Write<T>(ulong address, T value) where T : unmanaged
    {
        var bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1)).ToArray();
        WriteBytes(address, bytes);
    }

    public void WriteBytes(ulong address, byte[] bytes)
    {
        // Protect each queried region separately, preserving differing page protections.
        var offset = 0;
        while (offset < bytes.Length)
        {
            var current = checked(address + (ulong)offset);
            if (Native.VirtualQueryEx(handle, (nuint)current, out var info,
                (nuint)Marshal.SizeOf<Native.MemoryInfo>()) == 0)
                throw new IOException($"Query failed at 0x{current:X}.");
            if (info.State != 0x1000 || (info.Protect & 0x101) != 0)
                throw new IOException($"Inaccessible page at 0x{current:X}.");
            var count = (int)Math.Min((ulong)(bytes.Length - offset),
                checked((ulong)info.BaseAddress + (ulong)info.RegionSize) - current);
            var changed = !Writable(info.Protect);
            uint old = 0;
            if (changed && !Native.VirtualProtectEx(handle, (nuint)current, (nuint)count, 0x40, out old))
                throw new IOException($"Protect failed at 0x{current:X}.");
            try
            {
                var chunk = bytes.AsSpan(offset, count).ToArray();
                if (!Native.WriteProcessMemory(handle, (nuint)current, chunk, (nuint)count, out var written)
                    || written != (nuint)count)
                    throw new IOException($"Write failed at 0x{current:X}.");
                if (!Native.FlushInstructionCache(handle, (nuint)current, (nuint)count))
                    throw new IOException("FlushInstructionCache failed.");
            }
            finally
            {
                if (changed && !Native.VirtualProtectEx(handle, (nuint)current, (nuint)count, old, out _))
                    throw new IOException($"Protection restore failed at 0x{current:X}.");
            }
            offset += count;
        }
    }

    private static bool Writable(uint protection) =>
        (protection & 0x101) == 0 && (protection & 0xFF) is 0x04 or 0x08 or 0x40 or 0x80;

    public IEnumerable<MemoryRegion> GetRegions(bool privateOnly = false, MemoryRegion? range = null)
    {
        var address = range?.Address ?? 0;
        var end = range is { } r ? checked(r.Address + r.Size) : 0x0000800000000000UL;
        while (address < end && Native.VirtualQueryEx(handle, (nuint)address, out var info,
            (nuint)Marshal.SizeOf<Native.MemoryInfo>()) != 0)
        {
            var next = checked((ulong)info.BaseAddress + (ulong)info.RegionSize);
            if (next <= address)
                yield break;
            if (info.State == 0x1000 && Writable(info.Protect) && (!privateOnly || info.Type == 0x20000))
                yield return new MemoryRegion(address, Math.Min(next, end) - address);
            address = next;
        }
    }

    public ulong ResolveRipRelative(ulong instructionAddress, int displacementOffset, int instructionLength) =>
        checked((ulong)(checked((long)instructionAddress + instructionLength)
            + Read<int>(checked(instructionAddress + (ulong)displacementOffset))));

    // 游戏退出后旧句柄上的读取不一定会报错（2026-10-17：调试器附加导致游戏退出后，修改器一直停留在旧数据上），
    // 所以显式查询进程是否已结束。
    public bool HasExited => Native.WaitForSingleObject(handle, 0) == 0;

    public void Dispose() => handle.Dispose();

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct MemoryInfo
        {
            internal nuint BaseAddress;
            internal nuint AllocationBase;
            internal uint AllocationProtect;
            internal ushort PartitionId;
            internal nuint RegionSize;
            internal uint State;
            internal uint Protect;
            internal uint Type;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool ReadProcessMemory(SafeProcessHandle process, nuint address,
            byte[] buffer, nuint size, out nuint read);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool WriteProcessMemory(SafeProcessHandle process, nuint address,
            byte[] buffer, nuint size, out nuint written);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern nuint VirtualQueryEx(SafeProcessHandle process, nuint address,
            out MemoryInfo info, nuint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool VirtualProtectEx(SafeProcessHandle process, nuint address,
            nuint size, uint protect, out uint old);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool FlushInstructionCache(SafeProcessHandle process, nuint address, nuint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    }
}

public readonly record struct MemoryBlock(ulong Address, byte[] Bytes, int CandidateCount);

public static class MemoryChunks
{
    public static IEnumerable<MemoryBlock> Read(IMemorySource source, IEnumerable<MemoryRegion> regions,
        int width, int blockSize = 1024 * 1024)
    {
        if (width < 1 || blockSize < 1)
            throw new ArgumentOutOfRangeException(nameof(width));
        foreach (var region in regions)
        {
            for (ulong offset = 0; offset < region.Size;)
            {
                var candidates = (int)Math.Min((ulong)blockSize, region.Size - offset);
                var length = (int)Math.Min((ulong)checked(candidates + width - 1), region.Size - offset);
                byte[]? bytes = null;
                try
                {
                    bytes = source.ReadBytes(region.Address + offset, length);
                }
                catch (IOException)
                {
                    // A region can change while scanning. Retry page-sized pieces.
                }
                if (bytes != null)
                    yield return new MemoryBlock(region.Address + offset, bytes,
                        Math.Min(candidates, Math.Max(0, bytes.Length - width + 1)));
                else
                {
                    for (var part = 0; part < candidates;)
                    {
                        var pageAddress = region.Address + offset + (ulong)part;
                        var count = Math.Min(candidates - part, 4096 - (int)(pageAddress % 4096));
                        var size = (int)Math.Min((ulong)checked(count + width - 1),
                            region.Size - offset - (ulong)part);
                        byte[]? page = null;
                        try
                        {
                            page = source.ReadBytes(pageAddress, size);
                        }
                        catch (IOException)
                        {
                            // Skip only unreadable candidates.
                        }
                        if (page != null)
                            yield return new MemoryBlock(pageAddress, page,
                                Math.Min(count, Math.Max(0, page.Length - width + 1)));
                        part += count;
                    }
                }
                offset += (ulong)candidates;
            }
        }
    }
}
