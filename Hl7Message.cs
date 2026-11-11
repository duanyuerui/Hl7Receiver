using System.Text;

namespace Hl7Receiver;

public sealed class Hl7Segment
{
    public string Name { get; set; } = "";
    public string Raw { get; set; } = "";
    public List<string> Fields { get; set; } = new();

    public string this[int index] => index >= 0 && index < Fields.Count ? Fields[index] : "";

    /// <summary>MSH 段的分隔符本身占 MSH-1，之后 split[i] 对应 MSH-(i+1)。</summary>
    public string FieldLabel(int splitIndex)
    {
        if (splitIndex == 0) return "段名";
        return Name.Equals("MSH", StringComparison.OrdinalIgnoreCase)
            ? $"MSH-{splitIndex + 1}"
            : $"{Name}-{splitIndex}";
    }
}

public sealed class ObxResult
{
    public string No { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Reference { get; set; } = "";
    public string Flag { get; set; } = "";
    public string Status { get; set; } = "";
    public string TestTime { get; set; } = "";
}

public sealed class Hl7Message
{
    public DateTime ReceivedAt { get; set; } = DateTime.Now;
    public string Source { get; set; } = "";
    public bool Incomplete { get; set; }

    public byte[] Payload { get; set; } = Array.Empty<byte>();
    public byte[] Frame { get; set; } = Array.Empty<byte>();
    public Encoding Encoding { get; set; } = Encoding.Latin1;

    public string RawText { get; set; } = "";
    public string SavedPath { get; set; } = "";
    public List<Hl7Segment> Segments { get; } = new();
    public List<ObxResult> Results { get; } = new();

    public string MessageType { get; private set; } = "";
    public string ControlId { get; private set; } = "";
    public string SampleId { get; private set; } = "";
    public string PatientName { get; private set; } = "";
    public string TestProfile { get; private set; } = "";
    public string Version { get; private set; } = "";

    /// <summary>该消息应回复的类型；ACK/QCK 本身返回 null。</summary>
    public string? AckMessageType
    {
        get
        {
            string type = MessageType.ToUpperInvariant();
            var parts = type.Split('^');
            string trigger = parts.Length > 1 ? parts[1] : "";

            if (type.StartsWith("ORU")) return "ACK^" + (trigger.Length > 0 ? trigger : "R01");
            if (type.StartsWith("QRY")) return "QCK^" + (trigger.Length > 0 ? trigger : "Q02");
            if (type.StartsWith("DSR")) return "ACK^" + (trigger.Length > 0 ? trigger : "Q03");
            return null;
        }
    }

    public static Hl7Message Parse(byte[] payload, byte[] frame, Encoding encoding, string source, bool incomplete)
    {
        var message = new Hl7Message
        {
            Payload = payload,
            Frame = frame,
            Encoding = encoding,
            Source = source,
            Incomplete = incomplete
        };

        message.RawText = encoding.GetString(payload)
            .Replace("\r\n", "\r")
            .Replace('\n', '\r');

        char separator = '|';
        bool separatorFound = false;

        foreach (var raw in message.RawText.Split('\r'))
        {
            if (raw.Length == 0) continue;

            if (!separatorFound && raw.StartsWith("MSH", StringComparison.Ordinal) && raw.Length > 3)
            {
                separator = raw[3]; // MSH-1 字段分隔符
                separatorFound = true;
            }

            var parts = raw.Split(separator).ToList();
            message.Segments.Add(new Hl7Segment
            {
                Name = parts[0].Trim(),
                Raw = raw,
                Fields = parts
            });
        }

        var msh = message.Segments.FirstOrDefault(s => s.Name.Equals("MSH", StringComparison.OrdinalIgnoreCase));
        if (msh != null)
        {
            message.MessageType = msh[8].Trim();   // MSH-9
            message.ControlId = msh[9].Trim();     // MSH-10
            message.Version = msh[11].Trim();      // MSH-12
        }

        foreach (var segment in message.Segments)
        {
            if (segment.Name.Equals("PID", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(message.SampleId))
                    message.SampleId = FirstNonEmpty(segment[3], segment[2]); // PID-3 病人ID / PID-2 外部ID
                if (string.IsNullOrWhiteSpace(message.PatientName))
                    message.PatientName = CleanName(segment[5]);              // PID-5 姓名
            }
            else if (segment.Name.Equals("OBR", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(message.SampleId))
                    message.SampleId = FirstNonEmpty(segment[2], segment[3]); // OBR-2 / OBR-3
                if (string.IsNullOrWhiteSpace(message.TestProfile))
                    message.TestProfile = CleanName(segment[4]);              // OBR-4 项目
            }
            else if (segment.Name.Equals("OBX", StringComparison.OrdinalIgnoreCase))
            {
                message.Results.Add(new ObxResult
                {
                    No = segment[1],
                    Code = segment[3],
                    Name = CleanName(segment[4]),
                    Value = segment[5],
                    Unit = segment[6],
                    Reference = segment[7],
                    Flag = segment[8],
                    Status = segment[11],
                    TestTime = segment[14]
                });
            }
        }

        return message;
    }

    public string GetVisibleText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"接收时间: {ReceivedAt:yyyy-MM-dd HH:mm:ss.fff}");
        sb.AppendLine($"来源    : {Source}");
        sb.AppendLine($"消息类型: {MessageType}    控制ID: {ControlId}    样本号: {SampleId}");
        sb.AppendLine($"字节数  : {Payload.Length} 字节（另有 0x0B / 0x1C 0x0D 三个 MLLP 控制字节）");
        if (Incomplete) sb.AppendLine("*** 注意：此报文为空闲超时强制拆出，可能不完整 ***");
        sb.AppendLine(new string('-', 80));
        sb.Append(Mllp.VisibleText(Payload, Encoding));
        return sb.ToString();
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        return "";
    }

    internal static string CleanName(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Replace('^', ' ').Replace('&', ' ').Trim();
    }

    public string FileSafeName()
    {
        string type = string.IsNullOrWhiteSpace(MessageType) ? "HL7" : MessageType;
        string control = string.IsNullOrWhiteSpace(ControlId) ? "" : "_" + ControlId;
        string name = $"{ReceivedAt:yyyyMMdd_HHmmss_fff}_{type}{control}";
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name + ".hl7";
    }
}
