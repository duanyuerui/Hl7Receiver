using System.IO.Ports;
using System.Net;
using System.Net.Sockets;

namespace Hl7Receiver;

/// <summary>底层字节通道，TCP 与串口共用同一套收包逻辑。</summary>
public interface IHl7Transport : IDisposable
{
    string Description { get; }
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct);
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
}

internal sealed class TcpTransport : IHl7Transport
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;

    public TcpTransport(TcpClient client, string description)
    {
        _client = client;
        _stream = client.GetStream();
        Description = description;
    }

    public string Description { get; }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        => _stream.ReadAsync(buffer, ct);

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        => _stream.WriteAsync(data, ct);

    public void Dispose()
    {
        try { _stream.Dispose(); } catch { /* ignore */ }
        try { _client.Close(); } catch { /* ignore */ }
    }
}

public sealed class Hl7Connection : IDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public Hl7Connection(IHl7Transport transport, string remote, bool outbound, string role)
    {
        Transport = transport;
        Remote = remote;
        Outbound = outbound;
        Role = role;
    }

    public IHl7Transport Transport { get; }
    public string Remote { get; }
    public bool Outbound { get; }

    /// <summary>“仪器连我” / “我连仪器” / “串口”。</summary>
    public string Role { get; }

    public MllpFrameExtractor Extractor { get; } = new();
    public DateTime LastDataUtc { get; set; } = DateTime.UtcNow;
    public long BytesReceived { get; set; }

    public Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        => Transport.ReadAsync(buffer, ct).AsTask();

    public async Task SendAsync(byte[] data, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            await Transport.WriteAsync(data.AsMemory(), ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Dispose()
    {
        try { Transport.Dispose(); } catch { /* ignore */ }
        _sendLock.Dispose();
    }
}

/// <summary>
/// 支持三种模式：
///  1) 监听：仪器作为客户端连到本机端口并推送数据；
///  2) 主动连接：本程序作为客户端连到仪器 IP:端口；
///  3) 串口：本程序打开 COM 口，仪器通过串口推送数据，ACK 也从同一串口回。
/// 三种模式共用同一套字节累积 + MLLP 拆包逻辑。
/// </summary>
public sealed class Hl7ReceiverService : IDisposable
{
    private readonly object _gate = new();
    private readonly List<Hl7Connection> _connections = new();
    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private SerialPort? _serialPort;
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
    public bool IsSerial => _serialPort != null;
    public string? SerialPortName => _serialPort?.PortName;
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

    /// <summary>
    /// 打开串口并开始接收。Open 是同步的，打不开会直接抛异常给调用方。
    /// 打开成功后若串口断开，autoReconnect 为真时每 3 秒尝试重新打开。
    /// </summary>
    public void StartSerial(SerialSettings settings, bool autoReconnect)
    {
        EnsureToken();

        var port = settings.CreatePort();
        try
        {
            port.Open(); // 失败直接抛给 UI
        }
        catch
        {
            try { port.Dispose(); } catch { /* ignore */ }
            throw;
        }

        _serialPort = port;
        Log?.Invoke($"串口已打开: {settings.Describe()}");

        var connection = AttachSerial(port, settings.PortName);
        _ = SerialLoopAsync(connection, settings, autoReconnect, _cts!.Token);
    }

    private Hl7Connection AttachSerial(SerialPort port, string portName)
    {
        var connection = new Hl7Connection(new SerialTransport(port, portName), portName, outbound: false, role: "串口");
        connection.Extractor.Lenient = Lenient;

        lock (_gate) _connections.Add(connection);
        ConnectionOpened?.Invoke(connection);
        return connection;
    }

    private async Task SerialLoopAsync(Hl7Connection connection, SerialSettings settings, bool autoReconnect, CancellationToken ct)
    {
        await ReadLoopAsync(connection, ct);
        _serialPort = null;

        while (autoReconnect && !ct.IsCancellationRequested)
        {
            Log?.Invoke("串口已断开，3 秒后尝试重新打开...");

            try { await Task.Delay(3000, ct); }
            catch (OperationCanceledException) { break; }

            SerialPort? port = null;
            try
            {
                port = settings.CreatePort();
                port.Open();
                _serialPort = port;
                Log?.Invoke($"串口已重新打开: {settings.PortName}");

                connection = AttachSerial(port, settings.PortName);
                await ReadLoopAsync(connection, ct);
                _serialPort = null;
            }
            catch (OperationCanceledException)
            {
                try { port?.Dispose(); } catch { /* ignore */ }
                break;
            }
            catch (Exception ex)
            {
                try { port?.Dispose(); } catch { /* ignore */ }
                if (!ct.IsCancellationRequested) Log?.Invoke("重新打开串口失败: " + ex.Message);
            }
        }
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
                var connection = new Hl7Connection(new TcpTransport(client, remote), remote, outbound: false, role: "仪器连我");
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

                var connection = new Hl7Connection(new TcpTransport(client, $"{host}:{port}"), $"{host}:{port}", outbound: true, role: "我连仪器");
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
                int count = await connection.ReadAsync(buffer.AsMemory(), ct);
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

        try { _serialPort?.Close(); } catch { /* ignore */ }
        _serialPort = null;

        foreach (var connection in Connections)
        {
            try { connection.Dispose(); } catch { /* ignore */ }
        }
        lock (_gate) _connections.Clear();
    }

    private void EnsureToken()
    {
        if (_cts is { IsCancellationRequested: false }) return;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _cts = null;
    }
}
