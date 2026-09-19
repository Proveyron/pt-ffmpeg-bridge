using System.IO.Pipes;

namespace Bridge.Protocol;

/// <summary>
/// Server end of the DIPC named-pipe pair.
///
/// Pro Tools creates the inbound pipe \\.\pipe\DIPC_active_{parentPid} before launching us with
/// the parent PID as the only argument. We create our own inbound pipe DIPC_passive_{ourPid},
/// open the parent's pipe for writing, and wait for the parent to connect to ours. Both pipes
/// are message-mode, so every WriteFile is exactly one frame.
/// </summary>
public sealed class DipcTransport : IDisposable
{
    const int MaxMessage = 0x10000;

    readonly NamedPipeServerStream _inbound;
    readonly NamedPipeClientStream _outbound;
    readonly object _writeLock = new();

    public DipcTransport(int parentPid, TimeSpan connectTimeout)
    {
        int ownPid = Environment.ProcessId;
        _inbound = new NamedPipeServerStream($"DIPC_passive_{ownPid}", PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Message, PipeOptions.None, MaxMessage, 0);
        _outbound = new NamedPipeClientStream(".", $"DIPC_active_{parentPid}", PipeDirection.Out);
        _outbound.Connect((int)connectTimeout.TotalMilliseconds);
        var wait = _inbound.WaitForConnectionAsync();
        if (!wait.Wait(connectTimeout))
            throw new TimeoutException($"parent {parentPid} never connected to DIPC_passive_{ownPid}");
    }

    /// <summary>Blocks for the next frame; returns null when the parent disconnects.</summary>
    public DipcFrame? Receive()
    {
        var buffer = new byte[MaxMessage];
        using var message = new MemoryStream();
        do
        {
            int n = _inbound.Read(buffer, 0, buffer.Length);
            if (n == 0)
                return null;
            message.Write(buffer, 0, n);
        } while (!_inbound.IsMessageComplete);
        return DipcFrame.Parse(message.GetBuffer().AsSpan(0, (int)message.Length));
    }

    public void Send(DipcFrame frame)
    {
        var bytes = frame.Serialize();
        lock (_writeLock)
        {
            _outbound.Write(bytes, 0, bytes.Length);
            _outbound.Flush();
        }
    }

    public void Dispose()
    {
        _inbound.Dispose();
        _outbound.Dispose();
    }
}
