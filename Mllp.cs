using System.Text;

namespace Hl7Receiver;

/// <summary>
/// HL7 MLLP 最小底层协议：0x0B + 报文 + 0x1C 0x0D
/// </summary>
public static class Mllp
{
    public const byte SB = 0x0B; // 起始符 <VT>
    public const byte EB = 0x1C; // 结束符 <FS>
    public const byte CR = 0x0D; // 回车

    public static byte[] Wrap(ReadOnlySpan<byte> payload)
    {
        var result = new byte[payload.Length + 3];
        result[0] = SB;
        payload.CopyTo(result.AsSpan(1));
        result[^2] = EB;
        result[^1] = CR;
        return result;
    }

    /// <summary>把报文变成可读文本，并显式标出 MLLP 控制符。</summary>
    public static string VisibleText(byte[] payload, Encoding encoding)
    {
        var text = encoding.GetString(payload);
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');

        var sb = new StringBuilder();
        sb.AppendLine("<SB 0x0B 消息开始>");
        foreach (var segment in text.Split('\r'))
        {
            if (segment.Length == 0) continue;
            sb.AppendLine(segment);
        }
        sb.Append("<EB 0x1C><CR 0x0D> 消息结束");
        return sb.ToString();
    }

    /// <summary>标准十六进制 + ASCII 转储。</summary>
    public static string HexDump(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder();
        for (int offset = 0; offset < data.Length; offset += 16)
        {
            int count = Math.Min(16, data.Length - offset);
            sb.Append(offset.ToString("X4")).Append(": ");

            for (int i = 0; i < 16; i++)
            {
                if (i < count) sb.Append(data[offset + i].ToString("X2")).Append(' ');
                else sb.Append("   ");
                if (i == 7) sb.Append(' ');
            }

            sb.Append(" |");
            for (int i = 0; i < count; i++)
            {
                byte b = data[offset + i];
                sb.Append(b >= 0x20 && b <= 0x7E ? (char)b : '.');
            }
            sb.AppendLine("|");
        }
        return sb.ToString();
    }

    public static string HexPreview(ReadOnlySpan<byte> data, int maxBytes)
    {
        int count = Math.Min(maxBytes, data.Length);
        var sb = new StringBuilder(count * 3);
        for (int i = 0; i < count; i++) sb.Append(data[i].ToString("X2")).Append(' ');
        if (data.Length > count) sb.Append("...");
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// 按字节流重组 MLLP 帧。TCP 一次 Read 可能只收到半条报文，
/// 必须把数据累积到出现 0x1C 0x0D 才算一条完整报文。
/// </summary>
public sealed class MllpFrameExtractor
{
    private const int MaxBuffer = 8 * 1024 * 1024;
    private readonly List<byte> _buffer = new();

    /// <summary>宽松模式：没有 0x0B 起始符时也保留数据，等空闲超时后再拆出。</summary>
    public bool Lenient { get; set; }

    public int PendingCount => _buffer.Count;
    public long DiscardedBytes { get; private set; }

    public List<byte[]> Append(ReadOnlySpan<byte> data)
    {
        var frames = new List<byte[]>();
        foreach (byte b in data) _buffer.Add(b);

        while (true)
        {
            int start = _buffer.IndexOf(Mllp.SB);
            if (start < 0)
            {
                if (!Lenient && _buffer.Count > 0)
                {
                    DiscardedBytes += _buffer.Count;
                    _buffer.Clear();
                }
                break;
            }

            if (start > 0)
            {
                DiscardedBytes += start;
                _buffer.RemoveRange(0, start);
                continue;
            }

            int end = -1;
            for (int i = 1; i < _buffer.Count - 1; i++)
            {
                if (_buffer[i] == Mllp.EB && _buffer[i + 1] == Mllp.CR)
                {
                    end = i;
                    break;
                }
            }

            if (end < 0) break; // 报文还没收全，继续等下一批数据

            var payload = new byte[end - 1];
            _buffer.CopyTo(1, payload, 0, payload.Length);
            _buffer.RemoveRange(0, end + 2);
            frames.Add(payload);
        }

        if (_buffer.Count > MaxBuffer)
        {
            DiscardedBytes += _buffer.Count;
            _buffer.Clear();
        }

        return frames;
    }

    /// <summary>空闲超时后取出未成帧的缓存（诊断用，可能不完整）。</summary>
    public byte[] TakePending()
    {
        if (_buffer.Count == 0) return Array.Empty<byte>();

        int start = _buffer.IndexOf(Mllp.SB);
        var pending = _buffer.GetRange(start < 0 ? 0 : start, _buffer.Count - (start < 0 ? 0 : start)).ToArray();
        _buffer.Clear();
        return pending;
    }
}

/// <summary>按收到的消息类型生成 ACK / QCK 应答。</summary>
public static class Hl7Ack
{
    public static byte[] Build(Hl7Message message, string code, string text, Encoding encoding)
    {
        string ackType = message.AckMessageType ?? "ACK";
        string controlId = message.ControlId ?? string.Empty;
        string timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");

        string payload =
            $"MSH|^~\\&|||||{timestamp}||{ackType}|{controlId}|P|2.3.1||||0||ASCII|||\r" +
            $"MSA|{code}|{controlId}|{text}|||0|\r";

        return Mllp.Wrap(encoding.GetBytes(payload));
    }
}
