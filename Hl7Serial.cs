using System.IO.Ports;

namespace Hl7Receiver;

/// <summary>串口参数。串口没有“客户端/服务端”之分，收发都走同一个 COM 口。</summary>
public sealed class SerialSettings
{
    public string PortName { get; set; } = "";
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 8;
    public Parity Parity { get; set; } = Parity.None;
    public StopBits StopBits { get; set; } = StopBits.One;
    public Handshake Handshake { get; set; } = Handshake.None;
    public bool DtrEnable { get; set; } = true;
    public bool RtsEnable { get; set; } = true;

    public string Describe()
    {
        string parity = Parity switch
        {
            Parity.Odd => "O",
            Parity.Even => "E",
            Parity.Mark => "M",
            Parity.Space => "S",
            _ => "N"
        };

        string stopBits = StopBits switch
        {
            StopBits.OnePointFive => "1.5",
            StopBits.Two => "2",
            _ => "1"
        };

        string flow = Handshake switch
        {
            Handshake.XOnXOff => "XON/XOFF",
            Handshake.RequestToSend => "RTS/CTS",
            Handshake.RequestToSendXOnXOff => "RTS/CTS+XON/XOFF",
            _ => "无"
        };

        return $"{PortName} {BaudRate},{DataBits},{parity},{stopBits} 流控={flow} DTR={(DtrEnable ? "开" : "关")} RTS={(RtsEnable ? "开" : "关")}";
    }

    /// <summary>按当前参数创建并配置 SerialPort（尚未打开）。</summary>
    internal SerialPort CreatePort()
    {
        var port = new SerialPort(PortName, BaudRate, Parity, DataBits, StopBits)
        {
            Handshake = Handshake,
            DtrEnable = DtrEnable,
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 5000
        };

        // RTS/CTS 流控由驱动控制 RTS 线，此时再设置 RtsEnable 会抛 InvalidOperationException。
        if (Handshake is not (Handshake.RequestToSend or Handshake.RequestToSendXOnXOff))
            port.RtsEnable = RtsEnable;

        return port;
    }
}

/// <summary>把 SerialPort 包装成与 TCP 一致的字节通道。</summary>
internal sealed class SerialTransport : IHl7Transport
{
    private readonly SerialPort _port;

    public SerialTransport(SerialPort port, string description)
    {
        _port = port;
        Description = description;
    }

    public string Description { get; }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        => _port.BaseStream.ReadAsync(buffer, ct);

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        => _port.BaseStream.WriteAsync(data, ct);

    public void Dispose()
    {
        // 先 Close 让阻塞中的 ReadAsync 立即返回异常，再 Dispose。
        try { _port.Close(); } catch { /* ignore */ }
        try { _port.Dispose(); } catch { /* ignore */ }
    }
}
