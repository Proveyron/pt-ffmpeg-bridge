using System.IO.MemoryMappedFiles;
using Bridge.Protocol.Messages;

namespace Bridge.Protocol;

/// <summary>
/// Shared-memory pool the client reads extracted audio from.
///
/// Replies carry MemoryDescriptor{id, address, size}: the client opens Local\DIPC_Shm_{id:X8},
/// reads at offset <c>address</c>, then releases the block with a one-way SH_RemoteFreeMem.
/// QuickTime's server always handed out offset 0 of a single 128 MB mapping; we bump-allocate and
/// wrap so a late free from the client can never race a newer block.
/// </summary>
public sealed class SharedMemoryPool : IDisposable
{
    public const long DefaultSize = 128L * 1024 * 1024;
    const int Alignment = 64;

    readonly MemoryMappedFile _file;
    readonly MemoryMappedViewAccessor _view;
    readonly object _lock = new();
    long _next;

    public uint Id { get; }
    public long Size { get; }

    public SharedMemoryPool(long size = DefaultSize)
    {
        Size = size;
        // Random id like the original (it becomes part of the mapping name); retry on collision.
        for (int attempt = 0; ; attempt++)
        {
            Id = (uint)Random.Shared.NextInt64(0x10000000, 0xFFFFFFFF);
            try
            {
                _file = MemoryMappedFile.CreateNew($"Local\\DIPC_Shm_{Id:X8}", size, MemoryMappedFileAccess.ReadWrite);
                break;
            }
            catch (IOException) when (attempt < 8) { }
        }
        _view = _file.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite);
    }

    /// <summary>Copies <paramref name="data"/> into the pool and returns the descriptor to send.</summary>
    public MemoryDescriptor Write(ReadOnlySpan<byte> data)
    {
        if (data.Length > Size)
            throw new ArgumentException($"block of {data.Length} bytes exceeds pool size {Size}");
        long offset;
        lock (_lock)
        {
            if (_next + data.Length > Size)
                _next = 0;
            offset = _next;
            _next = (offset + data.Length + Alignment - 1) / Alignment * Alignment;
        }
        unsafe
        {
            byte* ptr = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            try
            {
                data.CopyTo(new Span<byte>(ptr + _view.PointerOffset + offset, data.Length));
            }
            finally
            {
                _view.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
        return new MemoryDescriptor { Id = Id, Address = (uint)offset, Size = (uint)Size };
    }

    public void Dispose()
    {
        _view.Dispose();
        _file.Dispose();
    }
}
