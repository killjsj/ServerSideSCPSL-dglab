using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace DglabKit;

public enum SocketState
{
    Idle,
    Connecting,
    WaitingForPeer,
    Paired,
    Disconnected
}

public enum SocketVersion
{
    V3,
    V4
}

public enum DeviceType
{
    [JsonProperty("COYOTE_020")] Coyote020,
    [JsonProperty("COYOTE_030")] Coyote030,
    [JsonProperty("BMTR_1")] Bmtr1,
    [JsonProperty("OVC_1")] Ovc1
}

public class DeviceTypeConverter : JsonConverter<DeviceType>
{
    public override DeviceType ReadJson(JsonReader reader, Type objectType, DeviceType existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        var s = reader.Value?.ToString();
        switch (s)
        {
            case "COYOTE_020": return DeviceType.Coyote020;
            case "COYOTE_030": return DeviceType.Coyote030;
            case "BMTR_1": return DeviceType.Bmtr1;
            case "OVC_1": return DeviceType.Ovc1;
            default: return default;
        }
    }

    public override void WriteJson(JsonWriter writer, DeviceType value, JsonSerializer serializer)
    {
        writer.WriteValue(value switch
        {
            DeviceType.Coyote020 => "COYOTE_020",
            DeviceType.Coyote030 => "COYOTE_030",
            DeviceType.Bmtr1 => "BMTR_1",
            DeviceType.Ovc1 => "OVC_1",
            _ => ""
        });
    }
}

public enum ActionType
{
    AppendPulseData = 0,
    AddIntensity = 3,
    SetTempIntensity = 4,
    SetIntensity = 7
}

public enum Channel
{
    A = 0,
    B = 1
}

// ==================== Server-Level Frames ====================

public class HelloFrame
{
    [JsonProperty("type")] public string Type => "hello";
    [JsonProperty("clientId")] public string ClientId { get; set; } = "";
    [JsonProperty("secret")] public string? Secret { get; set; }
}

public class ClientAttachedFrame
{
    [JsonProperty("type")] public string Type => "client_attached";
    [JsonProperty("clientId")] public string ClientId { get; set; } = "";
}

public class ClientDisconnectedFrame
{
    [JsonProperty("type")] public string Type => "client_disconnected";
    [JsonProperty("clientId")] public string ClientId { get; set; } = "";
}

public class HeartbeatFrame
{
    [JsonProperty("type")] public string Type => "heartbeat";
}

public class PongFrame
{
    [JsonProperty("type")] public string Type => "pong";
}

public class IdleTimeoutFrame
{
    [JsonProperty("type")] public string Type => "idle_timeout";
}

public class ErrorFrame
{
    [JsonProperty("type")] public string Type => "error";
    [JsonProperty("code")] public string Code { get; set; } = "";
    [JsonProperty("message")] public string? Message { get; set; }
    [JsonProperty("clientId")] public string? ClientId { get; set; }
}

public class MessageFrame<T>
{
    [JsonProperty("type")] public string Type => "message";
    [JsonProperty("clientId")] public string ClientId { get; set; } = "";
    [JsonProperty("data")] public T? Data { get; set; }
}

public class PingFrame
{
    [JsonProperty("type")] public string Type => "ping";
}

// ==================== RPC Request / Response ====================

public class RpcRequest
{
    [JsonProperty("t")] public string T => "req";
    [JsonProperty("reqId")] public string ReqId { get; set; } = "";
    [JsonProperty("requestId")] public string RequestId { get; set; } = "";
    [JsonProperty("m")] public string M { get; set; } = "";
    [JsonProperty("data")] public object? Data { get; set; }
}

public class RpcResponse
{
    [JsonProperty("t")] public string T => "resp";
    [JsonProperty("reqId")] public string? ReqId { get; set; }
    [JsonProperty("requestId")] public string? RequestId { get; set; }
    [JsonProperty("result")] public object? Result { get; set; }
    [JsonProperty("error")] public string? Error { get; set; }
}

// ==================== Device Descriptors ====================

public class DeviceDescriptor
{
    [JsonProperty("slotId")] public string SlotId { get; set; } = "";
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("type")] public DeviceType Type { get; set; }
}

public class SlotState
{
    [JsonProperty("markLight")] public string? MarkLight { get; set; }
    [JsonProperty("hasDevice")] public bool HasDevice { get; set; }
    [JsonProperty("isMuted")] public bool? IsMuted { get; set; }
    [JsonProperty("warmUpScale")] public double? WarmUpScale { get; set; }
    [JsonProperty("intensityMax")] public int? IntensityMax { get; set; }
}

public class ComfortLimit
{
    [JsonProperty("mode")] public string? Mode { get; set; }
    [JsonProperty("comfortMax")] public int? ComfortMax { get; set; }
    [JsonProperty("absoluteMax")] public int? AbsoluteMax { get; set; }
    [JsonProperty("overheat")] public bool? Overheat { get; set; }
    [JsonProperty("overheatPercent")] public double? OverheatPercent { get; set; }
    [JsonProperty("autoIncr")] public bool? AutoIncr { get; set; }
    [JsonProperty("autoIncrMax")] public int? AutoIncrMax { get; set; }
    [JsonProperty("autoIncrScope")] public int? AutoIncrScope { get; set; }
    [JsonProperty("totalIncr")] public int? TotalIncr { get; set; }
}

public class DeviceInfo : DeviceDescriptor
{
    [JsonProperty("props")] public Dictionary<string, object>? Props { get; set; }
    [JsonProperty("slotState")] public Dictionary<string, object>? SlotState { get; set; }
}

// ==================== Event Payloads ====================

public class DevicesSnapshotEvent
{
    [JsonProperty("t")] public string T => "ev";
    [JsonProperty("ev")] public string Ev => "devices.snapshot";
    [JsonProperty("devices")] public List<DeviceInfo> Devices { get; set; } = new List<DeviceInfo>();
}

public class DevicesPatchEvent
{
    [JsonProperty("t")] public string T => "ev";
    [JsonProperty("ev")] public string Ev => "devices.patch";
    [JsonProperty("added")] public List<DeviceInfo>? Added { get; set; }
    [JsonProperty("removed")] public List<string>? Removed { get; set; }
}

public class SlotPatch
{
    [JsonProperty("slotId")] public string SlotId { get; set; } = "";
    [JsonProperty("props")] public Dictionary<string, object>? Props { get; set; }
    [JsonProperty("slotState")] public Dictionary<string, object>? SlotState { get; set; }
}

public class SlotsPatchEvent
{
    [JsonProperty("t")] public string T => "ev";
    [JsonProperty("ev")] public string Ev => "slots.patch";
    [JsonProperty("slots")] public List<SlotPatch>? Slots { get; set; }
}

public class CustomActionEvent
{
    [JsonProperty("t")] public string T => "ev";
    [JsonProperty("ev")] public string Ev => "custom.action";
    [JsonProperty("action")] public int Action { get; set; }
}

// ==================== Device Operate ====================

public class AddIntensityOperate
{
    [JsonProperty("s")] public string S { get; set; } = "";
    [JsonProperty("c")] public int C { get; set; }
    [JsonProperty("t")] public int T => (int)ActionType.AddIntensity;
    [JsonProperty("v")] public int V { get; set; }
    [JsonProperty("p")] public int? P { get; set; }
    [JsonProperty("d")] public int? D { get; set; }
    [JsonProperty("im")] public bool? Im { get; set; }
}

public class SetTempIntensityOperate
{
    [JsonProperty("s")] public string S { get; set; } = "";
    [JsonProperty("c")] public int C { get; set; }
    [JsonProperty("t")] public int T => (int)ActionType.SetTempIntensity;
    [JsonProperty("v")] public int V { get; set; }
    [JsonProperty("p")] public int? P { get; set; }
    [JsonProperty("d")] public int? D { get; set; }
    [JsonProperty("im")] public bool? Im { get; set; }
}

public class SetIntensityOperate
{
    [JsonProperty("s")] public string S { get; set; } = "";
    [JsonProperty("c")] public int C { get; set; }
    [JsonProperty("t")] public int T => (int)ActionType.SetIntensity;
    [JsonProperty("v")] public int V { get; set; }
    [JsonProperty("p")] public int? P { get; set; }
    [JsonProperty("d")] public int? D { get; set; }
    [JsonProperty("im")] public bool? Im { get; set; }
}

public class AppendPulseDataOperate
{
    [JsonProperty("s")] public string S { get; set; } = "";
    [JsonProperty("c")] public int C { get; set; }
    [JsonProperty("t")] public int T => (int)ActionType.AppendPulseData;
    [JsonProperty("v")] public object V { get; set; } = new List<string>();  // string[] or number[][]
    [JsonProperty("ver")] public int? Ver { get; set; }
    [JsonProperty("seq")] public int? Seq { get; set; }
    [JsonProperty("p")] public int? P { get; set; }
    [JsonProperty("d")] public int? D { get; set; }
    [JsonProperty("im")] public bool? Im { get; set; }
}

public class ClearOperateRequest
{
    [JsonProperty("s")] public string? S { get; set; }
    [JsonProperty("c")] public int? C { get; set; }
}

public class DevicesGetResult
{
    [JsonProperty("devices")] public List<DeviceDescriptor> Devices { get; set; } = new List<DeviceDescriptor>();
}

public class OperateOptions
{
    public int? Priority { get; set; }
    public bool? Immediate { get; set; }
    public int? Timeout { get; set; }
    public int? Version { get; set; }
    public int? Seq { get; set; }
}

public class ConnectResult
{
    public string TargetId { get; set; } = "";
    public string? Secret { get; set; }
}

public class SocketCloseEventArgs : EventArgs
{
    public int Code { get; set; }
    public string Reason { get; set; } = "";
    public bool WasClean { get; set; }
}

// ==================== V3 Protocol Types ====================

public enum V3Channel
{
    A = 1,
    B = 2
}

public enum V3CommandType
{
    ReduceStrength = 1,
    IncreaseStrength = 2,
    SetStrength = 3,
    ClearPulse = 4,
    ClientMsg = -1  // sent as string "clientMsg"
}

public class V3BindFrame
{
    [JsonProperty("type")] public string Type => "bind";
    [JsonProperty("clientId")] public string ClientId { get; set; } = "";
    [JsonProperty("targetId")] public string? TargetId { get; set; }
    [JsonProperty("message")] public string? Message { get; set; }
}

public class V3BreakFrame
{
    [JsonProperty("type")] public string Type => "break";
    [JsonProperty("clientId")] public string? ClientId { get; set; }
    [JsonProperty("targetId")] public string? TargetId { get; set; }
    [JsonProperty("message")] public string? Message { get; set; }
}

public class V3ErrorFrame
{
    [JsonProperty("type")] public string Type => "error";
    [JsonProperty("message")] public string? Message { get; set; }
}

public class V3HeartbeatFrame
{
    [JsonProperty("type")] public string Type => "heartbeat";
}

public class V3LegacyCommand
{
    [JsonProperty("type")] public object Type { get; set; } = null!;  // int or "clientMsg"
    [JsonProperty("message")] public string Message { get; set; } = "";
    [JsonProperty("channel")] public int? Channel { get; set; }
    [JsonProperty("time")] public int? Time { get; set; }            // seconds
    [JsonProperty("strength")] public int? Strength { get; set; }
}

public class V3WaveOptions
{
    public string Channel { get; set; } = "A";  // "A" or "B"
    public int Time { get; set; }               // seconds
    public string[] Data { get; set; } = new string[0];
}

public class V3DeviceInfo
{
    [JsonProperty("type")] public DeviceType Type { get; set; } = DeviceType.Coyote030;
    [JsonProperty("props")] public V3DeviceProps? Props { get; set; }
}

public class V3DeviceProps
{
    [JsonProperty("strength")] public V3Strength? Strength { get; set; }
    [JsonProperty("softLimit")] public V3Strength? SoftLimit { get; set; }
}

public class V3Strength
{
    [JsonProperty("A")] public int A { get; set; }
    [JsonProperty("B")] public int B { get; set; }
}

// ==================== Device State (Coyote only) ====================

public class CoyoteDeviceState
{
    public string SlotId { get; set; } = "";
    public DeviceType Type { get; set; }

    /// <summary>通道 A 当前强度 (0-200)</summary>
    public int IntensityA { get; set; }

    /// <summary>通道 B 当前强度 (0-200)</summary>
    public int IntensityB { get; set; }

    /// <summary>通道 A 最大强度上限</summary>
    public int MaxIntensityA { get; set; }

    /// <summary>通道 B 最大强度上限</summary>
    public int MaxIntensityB { get; set; }

    /// <summary>设备是否已连接</summary>
    public bool IsConnected { get; set; }
}
