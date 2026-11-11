using System.Net;
using System.Net.Sockets;

namespace Hl7Receiver;

public sealed class Hl7Connection : IDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public Hl7Connection(TcpClient client, string remote, bool outbound)
    {
        Client = client;
        Remote = remote;
        Outbound = outbound;
    }

    public TcpClient Client { get; }
    public string Remote { get; }
    public bool Outbound { get; }
    public MllpFrameExtractor Extractor { get; } = new();
    public DateTime LastDataUtc { get; set; } = DateTime.UtcNow;
    public long BytesReceived { get; set; }
    public string Role => Outbound ? "我连仪器" : "仪器连我";

    public async Task SendAsync(byte[] data, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            await Client.GetStream().WriteAsync(data.AsMemory(), ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Dispose()
    {
        try { Client.Close(); } catch { /* ignore */ }
        _sendLock.Dispose();
    }
}

/// <summary>
/// 支持两种模式：
///  1) 监听：仪器作为客户端连到本机端口并推送数据；
///  2) 主动连接：本程序作为客户端连到仪器 IP:端口。
/// 两种模式共用同一套字节累积 + MLLP 拆包逻辑。
/// </summary>
public sealed class Hl7ReceiverService : IDisposable
{
    private readonly object _gate = new();
    private readonly List<Hl7Connection> _connections = new();
    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private long _totalBytes;
    private bool _lenient;

    public bool Lenient
    {
        get => _lenient;
        set
        {
            _lenient = value;
            foreach (var connection in Connections)
                connection.Extractor.Lenient = value;
        }
    }

    public event Action<Hl7Connection>? ConnectionOpened;
    public event Action<Hl7Connection, string>? ConnectionClosed;
    public event Action<Hl7Connection, byte[]>? ChunkReceived;
    public event Action<Hl7Connection, byte[], bool>? FrameReceived;
    public event Action<string>? Log;

    public bool IsListening => _listener != null;
    public long TotalBytesReceived => Interlocked.Read(ref _totalBytes);

    public int ConnectionCount
    {
        get { lock (_gate) return _connections.Count; }
    }

    public IReadOnlyList<Hl7Connection> Connections
    {
        get { lock (_gate) return _connections.ToArray(); }
    }

    public int PendingBufferBytes
    {
        get { lock (_gate) return _connections.Sum(c => c.Extractor.PendingCount); }
    }

    public void StartServer(int port)
    {
        EnsureToken();
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        _listener = listener;
        _ = AcceptLoopAsync(listener, _cts!.Token);
        Log?.Invoke($"开始监听 0.0.0.0:{port}（等仪器来连）");
    }

    public void StartClient(string host, int port, bool autoReconnect)
    {
        EnsureToken();
        _ = ClientLoopAsync(host, port, autoReconnect, _cts!.Token);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(ct);
                client.NoDelay = true;

                string remote = client.Client.RemoteEndPoint?.ToString() ?? "未知";
                var connection = new Hl7Connection(client, remote, outbound: false);
                connection.Extractor.Lenient = Lenient;

                lock (_gate) _connections.Add(connection);
                ConnectionOpened?.Invoke(connection);
                _ = ReadLoopAsync(connection, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) Log?.Invoke("监听异常: " + ex.Message);
        }
    }

    private async Task ClientLoopAsync(string host, int port, bool autoReconnect, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = new TcpClient();
                await client.ConnectAsync(host, port, ct);
                client.NoDelay = true;

                var connection = new Hl7Connection(client, $"{host}:{port}", outbound: true);
                connection.Extractor.Lenient = Lenient;

                lock (_gate) _connections.Add(connection);
                ConnectionOpened?.Invoke(connection);
                Log?.Invoke($"已连接 {host}:{port}");

                await ReadLoopAsync(connection, ct);
            }
            catch (OperationCanceledException)
            {
                client?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                client?.Dispose();
                if (!ct.IsCancellationRequested) Log?.Invoke($"连接 {host}:{port} 失败: {ex.Message}");
            }

            if (!autoReconnect || ct.IsCancellationRequested) break;

            try { await Task.Delay(3000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReadLoopAsync(Hl7Connection connection, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int count = await connection.Client.GetStream().ReadAsync(buffer.AsMemory(), ct);
                if (count <= 0) break; // 对端正常关闭

                Interlocked.Add(ref _totalBytes, count);
                connection.LastDataUtc = DateTime.UtcNow;
                connection.BytesReceived += count;

                byte[] chunk = buffer.AsSpan(0, count).ToArray();

                List<byte[]> frames;
                try
                {
                    frames = connection.Extractor.Append(chunk);
                }
                catch (Exception ex)
                {
                    Log?.Invoke("拆包异常: " + ex.Message);
                    continue;
                }

                ChunkReceived?.Invoke(connection, chunk);

                foreach (var payload in frames)
                    FrameReceived?.Invoke(connection, payload, false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) Log?.Invoke($"{connection.Remote} 连接关闭: {ex.Message}");
        }
        finally
        {
            lock (_gate) _connections.Remove(connection);
            connection.Dispose();
            ConnectionClosed?.Invoke(connection, "连接断开");
        }
    }

    /// <summary>宽松模式下，把长时间没有后续数据、又未出现 0x1C 0x0D 的缓存强制交出来。</summary>
    public void FlushIdle(TimeSpan idle)
    {
        foreach (var connection in Connections)
        {
            if (connection.Extractor.PendingCount == 0) continue;
            if (DateTime.UtcNow - connection.LastDataUtc < idle) continue;

            byte[] pending = connection.Extractor.TakePending();
            if (pending.Length == 0) continue;

            Log?.Invoke($"{connection.Remote} 空闲 {idle.TotalMilliseconds:0}ms，强制拆出 {pending.Length} 字节");
            FrameReceived?.Invoke(connection, pending, true);
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }

        try { _listener?.Stop(); } catch { /* ignore */ }
        _listener = null;

        foreach (var connection in Connections)
        {
            try { connection.Dispose(); } catch { /* ignore */ }
        }
        lock (_gate) _connections.Clear();
    }

    private void EnsureToken()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _cts = null;
    }
}
