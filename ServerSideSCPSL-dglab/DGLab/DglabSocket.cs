using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LabApi.Features.Wrappers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace DglabKit;

public class DglabSocket : IDisposable
{
    private const int DefaultConnectTimeout = 8000;
    private const int DefaultResponseTimeout = 8000;
    private const int ServerPingInterval = 2000;
    private const int MaxMissedServerPongs = 3;

    private readonly JsonSerializerSettings _jsonSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        NullValueHandling = NullValueHandling.Ignore,
        Converters = new List<JsonConverter> { new DeviceTypeConverter() }
    };

    private readonly DglabSocketOptions _options;
    private readonly SocketVersion _version;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private Timer? _serverPingTimer;
    private int _missedServerPongs;

    private TaskCompletionSource<ConnectResult> _connectTcs = new();
    private Task<ConnectResult>? _connectTask;
    private CancellationTokenSource? _connectTimeoutCts;

    private string? _targetId;
    private string? _secret;

    private readonly ConcurrentDictionary<string, List<DeviceInfo>> _clients = new();

    private readonly ConcurrentDictionary<string, PendingRpc> _pendingRpc = new();

    private string? _pairedTargetId;
    private V3DeviceInfo? _v3Device;

    private readonly ConcurrentDictionary<Player, string> _playerBindings = new();

    private readonly ConcurrentDictionary<string, Player> _clientBindings = new();

    private readonly ConcurrentQueue<Player> _pendingPlayerIds = new();

    private readonly ConcurrentDictionary<string, CoyoteDeviceState> _deviceStates = new();

    public SocketState State { get; private set; } = SocketState.Idle;

    public Player? Player { get; set; }

    public string? TargetId => _targetId;
    public string? Secret => _secret;

    public IReadOnlyList<string> ClientIds => _clients.Keys.ToList();

    public string? CurrentClientId
    {
        get
        {
            if (_version == SocketVersion.V3)
                return _pairedTargetId;
            if (Player != null)
                return IDToClientID(Player);
            return _clients.Keys.FirstOrDefault();
        }
    }

    public event EventHandler<ConnectResult>? OnConnected;
    public event EventHandler<SocketCloseEventArgs>? OnDisconnected;
    public event EventHandler<string>? OnError;
    public event EventHandler<string>? OnClientAttached;
    public event EventHandler<string>? OnClientDisconnected;
    public event EventHandler<(string ClientId, List<DeviceInfo> Devices)>? OnDevicesUpdated;
    public event EventHandler<(string ClientId, DeviceInfo Device)>? OnDeviceChanged;
    public event EventHandler<(string ClientId, object Data)>? OnData;
    public event EventHandler<int>? OnAction;
    public event EventHandler<SocketState>? OnStateChanged;

    public DglabSocket(DglabSocketOptions? options = null)
    {
        _options = options ?? new DglabSocketOptions();
        _version = _options.Version;
    }

    public async Task<ConnectResult> ConnectAsync()
    {
        if (_connectTask != null && !_connectTask.IsCompleted)
            return await _connectTask;

        _connectTcs.TrySetCanceled();
        _connectTcs = new TaskCompletionSource<ConnectResult>();
        _connectTask = _connectTcs.Task;
        SetState(SocketState.Connecting);

        var timeoutCts = new CancellationTokenSource(
            _options.ConnectTimeout > 0 ? _options.ConnectTimeout : DefaultConnectTimeout);
        _connectTimeoutCts = timeoutCts;

        try
        {
            _ws = new ClientWebSocket();
            var uri = new Uri(_options.Url ?? "wss://ws.dungeon-lab.cn");
            await _ws.ConnectAsync(uri, timeoutCts.Token);

            _receiveCts = new CancellationTokenSource();
            _receiveTask = ReceiveLoopAsync(_receiveCts.Token);

            var delayMs = _options.ConnectTimeout > 0 ? _options.ConnectTimeout : DefaultConnectTimeout;
            var delayTask = Task.Delay(delayMs, timeoutCts.Token);
            var completed = await Task.WhenAny(_connectTcs.Task, delayTask);

            if (completed == _connectTcs.Task)
            {
                var result = await _connectTcs.Task;
                OnConnected?.Invoke(this, result);
                return result;
            }

            throw new TimeoutException("Connection timeout");
        }
        catch (Exception ex)
        {
            SetState(SocketState.Disconnected);
            OnError?.Invoke(this, $"Connect failed: {ex.Message}");
            throw new DglabException($"Connect failed: {ex.Message}", ex);
        }
        finally
        {
            timeoutCts.Dispose();
            _connectTimeoutCts = null;
        }
    }

    public async Task DisconnectAsync(int code = 4000, string reason = "")
    {
        StopServerPing();

        foreach (var reqId in _pendingRpc.Keys.ToList())
        {
            if (_pendingRpc.TryRemove(reqId, out var pending))
            {
                if (!pending.Settled)
                {
                    pending.Settled = true;
                    pending.Tcs.TrySetException(new DglabException("Connection closed"));
                }
            }
        }

        _receiveCts?.Cancel();
        if (_receiveTask != null)
        {
            try { await _receiveTask; } catch { }
            _receiveTask = null;
        }

        if (_ws?.State == WebSocketState.Open)
        {
            try
            {
                await _ws.CloseAsync((WebSocketCloseStatus)code, reason, CancellationToken.None);
            }
            catch { }
        }

        _ws?.Dispose();
        _ws = null;
        while (_pendingPlayerIds.TryDequeue(out _)) { }
        SetState(SocketState.Disconnected);
        _clients.Clear();
        OnDisconnected?.Invoke(this, new SocketCloseEventArgs { Code = code, Reason = reason });
    }

    public async Task<DevicesGetResult> RequestDevicesAsync(string clientId, int timeout = 0)
    {
        var req = CreateRpcRequest("devices.get");
        var resp = await SendRpcAsync(clientId, req, timeout);
        var result = JsonConvert.DeserializeObject<DevicesGetResult>(
            JsonConvert.SerializeObject(resp.Result), _jsonSettings);
        return result ?? new DevicesGetResult();
    }

    public async Task<DevicesGetResult> RequestDevicesAsync(int timeout = 0)
        => await RequestDevicesAsync(ResolveClientId(), timeout);

    public async Task<object?> PingAsync(string clientId, int timeout = 0)
    {
        var req = CreateRpcRequest("ping");
        var resp = await SendRpcAsync(clientId, req, timeout);
        return resp.Result;
    }

    public async Task<object?> PingAsync(int timeout = 0)
        => await PingAsync(ResolveClientId(), timeout);

    public async Task<object?> AddIntensityAsync(
        string clientId, string slotId, Channel channel, int value,
        OperateOptions? options = null)
    {
        var operate = new AddIntensityOperate
        {
            S = slotId, C = (int)channel, V = value,
            P = options?.Priority, Im = options?.Immediate
        };
        var req = CreateRpcRequest("device.op", operate);
        var resp = await SendRpcAsync(clientId, req, options?.Timeout);
        return resp.Result;
    }

    public async Task<object?> AddIntensityAsync(
        string slotId, Channel channel, int value, OperateOptions? options = null)
        => await AddIntensityAsync(ResolveClientId(), slotId, channel, value, options);

    public async Task<object?> ReduceStrengthAsync(
        string clientId, string slotId, Channel channel, int value,
        OperateOptions? options = null)
    {
        return await AddIntensityAsync(clientId, slotId, channel, -value, options);
    }

    public async Task<object?> ReduceStrengthAsync(
        string slotId, Channel channel, int value, OperateOptions? options = null)
        => await ReduceStrengthAsync(ResolveClientId(), slotId, channel, value, options);

    public async Task<object?> SetTempIntensityAsync(
        string clientId, string slotId, Channel channel, int value, int duration,
        OperateOptions? options = null)
    {
        if(value >= 100) value = 100;
        var operate = new SetTempIntensityOperate
        {
            S = slotId, C = (int)channel, V = value, D = duration,
            P = options?.Priority, Im = options?.Immediate
        };
        var req = CreateRpcRequest("device.op", operate);
        var resp = await SendRpcAsync(clientId, req, options?.Timeout);
        return resp.Result;
    }

    public async Task<object?> SetTempIntensityAsync(
        string slotId, Channel channel, int value, int duration, OperateOptions? options = null)
        => await SetTempIntensityAsync(ResolveClientId(), slotId, channel, value, duration, options);

    public async Task<object?> ResetIntensityAsync(
        string clientId, string slotId, Channel channel,
        OperateOptions? options = null)
    {
        var operate = new SetIntensityOperate
        {
            S = slotId, C = (int)channel, V = 0,
            P = options?.Priority, Im = options?.Immediate
        };
        var req = CreateRpcRequest("device.op", operate);
        var resp = await SendRpcAsync(clientId, req, options?.Timeout);
        return resp.Result;
    }

    public async Task<object?> ResetIntensityAsync(
        string slotId, Channel channel, OperateOptions? options = null)
        => await ResetIntensityAsync(ResolveClientId(), slotId, channel, options);

    public async Task<object?> SendPulseAsync(
        string clientId, string slotId, Channel channel,
        int duration, List<string> frames,
        OperateOptions? options = null)
    {
        var operate = new AppendPulseDataOperate
        {
            S = slotId, C = (int)channel, V = frames, D = duration,
            Ver = options?.Version, Seq = options?.Seq,
            P = options?.Priority, Im = options?.Immediate
        };
        var req = CreateRpcRequest("device.op", operate);
        var resp = await SendRpcAsync(clientId, req, options?.Timeout);
        return resp.Result;
    }

    public async Task<object?> SendPulseAsync(
        string slotId, Channel channel, int duration, List<string> frames,
        OperateOptions? options = null)
        => await SendPulseAsync(ResolveClientId(), slotId, channel, duration, frames, options);

    public async Task<object?> SendPulseAsync(
        string clientId, string slotId, Channel channel,
        int duration, int[][] frames,
        OperateOptions? options = null)
    {
        var operate = new AppendPulseDataOperate
        {
            S = slotId, C = (int)channel, V = frames, D = duration,
            Ver = options?.Version, Seq = options?.Seq,
            P = options?.Priority, Im = options?.Immediate
        };
        var req = CreateRpcRequest("device.op", operate);
        var resp = await SendRpcAsync(clientId, req, options?.Timeout);
        return resp.Result;
    }

    public async Task<object?> SendPulseAsync(
        string slotId, Channel channel, int duration, int[][] frames,
        OperateOptions? options = null)
        => await SendPulseAsync(ResolveClientId(), slotId, channel, duration, frames, options);

    /// <summary>
    /// 从 (A强度, B强度) 元组列表下发波形。每个元组 = 1ms，总时长 = 元组数。
    /// </summary>
    public async Task<object?> SendPulseFromTuplesAsync(string clientId, string slotId, Channel channel,
        List<(int A, int B)> pulses, OperateOptions? options = null)
    {
        var frames = WaveformBuilder.FromTuples(pulses);
        return await SendPulseAsync(clientId, slotId, channel, frames.Count, frames, options);
    }

    /// <summary>
    /// 从 (A强度, B强度) 元组列表下发波形。每个元组 = 1ms，总时长 = 元组数。
    /// </summary>
    public async Task<object?> SendPulseFromTuplesAsync(string slotId, Channel channel,
        List<(int A, int B)> pulses, OperateOptions? options = null)
        => await SendPulseFromTuplesAsync(ResolveClientId(), slotId, channel, pulses, options);

    public async Task<object?> ClearPulseAsync(string clientId, string slotId, Channel channel)
    {
        return await ClearOperateAsync(clientId, slotId, channel);
    }

    public async Task<object?> ClearPulseAsync(string slotId, Channel channel)
        => await ClearPulseAsync(ResolveClientId(), slotId, channel);

    public async Task<object?> ClearOperateAsync(
        string clientId, string? slotId = null, Channel? channel = null)
    {
        object? data = null;
        if (slotId != null)
        {
            data = new ClearOperateRequest { S = slotId, C = (int?)channel };
        }
        var req = CreateRpcRequest("device.op.clear", data);
        var resp = await SendRpcAsync(clientId, req);
        return resp.Result;
    }

    public async Task<object?> ClearOperateAsync(string? slotId = null, Channel? channel = null)
        => await ClearOperateAsync(ResolveClientId(), slotId, channel);

    public async Task<RpcResponse> SendCustomAsync(
        string clientId, object data, int timeout = 0)
    {
        var req = CreateRpcRequest("custom", data);
        return await SendRpcAsync(clientId, req, timeout);
    }

    public async Task<RpcResponse> SendCustomAsync(object data, int timeout = 0)
        => await SendCustomAsync(ResolveClientId(), data, timeout);

    public IReadOnlyList<DeviceInfo>? GetDevices(string clientId)
    {
        return _clients.TryGetValue(clientId, out var list) ? list : null;
    }

    public IReadOnlyList<DeviceInfo>? GetDevices()
        => GetDevices(ResolveClientId());

    public string? IDToClientID(Player player)
    {
        return _playerBindings.TryGetValue(player, out var clientId) ? clientId : null;
    }

    public Player? ClientIDToID(string clientId)
    {
        return _clientBindings.TryGetValue(clientId, out var player) ? player : null;
    }

    public void BindPlayerToClient(Player player, string clientId)
    {
        _playerBindings[player] = clientId;
        _clientBindings[clientId] = player;
    }

    public void EnqueuePlayer(Player player)
    {
        _pendingPlayerIds.Enqueue(player);
    }

    public void UnbindPlayer(Player player)
    {
        if (_playerBindings.TryRemove(player, out var clientId))
            _clientBindings.TryRemove(clientId, out _);
    }

    public void Dispose()
    {
        _ = DisconnectAsync();
        _receiveCts?.Cancel();
        _receiveCts?.Dispose();
        _connectTimeoutCts?.Dispose();
        _serverPingTimer?.Dispose();
        _ws?.Dispose();
        GC.SuppressFinalize(this);
    }

    // ==================== Private Methods ====================

    private string ResolveClientId()
    {
        if (Player != null)
        {
            var cid = IDToClientID(Player);
            if (cid != null) return cid;
        }
        var first = _clients.Keys.FirstOrDefault();
        if (first != null) return first;
        throw new DglabException("No client connected. Wait for client-attached or set PlayerId.");
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        var messageBuilder = new StringBuilder();
        try
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                if (result.EndOfMessage)
                {
                    var text = messageBuilder.ToString();
                    messageBuilder.Clear();
                    HandleMessage(text);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OnError?.Invoke(this, $"Receive error: {ex.Message}");
        }
    }

    private void HandleMessage(string text)
    {
        try
        {
            var root = JObject.Parse(text);
            var typeProp = root["type"];
            if (typeProp == null) return;

            if (_version == SocketVersion.V3)
            {
                HandleV3Message(root, typeProp);
                return;
            }

            var messageType = typeProp.ToString();

            switch (messageType)
            {
                case "hello":
                    HandleHello(root);
                    break;
                case "client_attached":
                    HandleClientAttached(root);
                    break;
                case "client_disconnected":
                    HandleClientDisconnected(root);
                    break;
                case "message":
                    HandleAppMessage(root);
                    break;
                case "pong":
                    _missedServerPongs = 0;
                    break;
                case "heartbeat":
                    break;
                case "idle_timeout":
                    OnError?.Invoke(this, "Idle timeout");
                    _ = DisconnectAsync(4002, "idle_timeout");
                    break;
                case "error":
                    var code = (string?)root["code"] ?? "";
                    var msg = (string?)root["message"] ?? "";
                    OnError?.Invoke(this, $"Server error [{code}]: {msg}");
                    break;
            }
        }
        catch (JsonReaderException ex)
        {
            OnError?.Invoke(this, $"JSON parse error: {ex.Message}");
        }
    }

    private void HandleHello(JObject root)
    {
        _targetId = (string?)root["clientId"] ?? "";
        _secret = (string?)root["secret"];

        SetState(SocketState.WaitingForPeer);
        StartServerPing();

        _connectTcs.TrySetResult(new ConnectResult
        {
            TargetId = _targetId ?? "",
            Secret = _secret
        });
    }

    private void HandleClientAttached(JObject root)
    {
        var clientId = (string?)root["clientId"] ?? "";
        _clients.TryAdd(clientId, new List<DeviceInfo>());

        if (Player != null)
        {
            BindPlayerToClient(Player, clientId);
        }
        else if (_pendingPlayerIds.TryDequeue(out var pendingPlayer))
        {
            BindPlayerToClient(pendingPlayer, clientId);
        }

        SetState(SocketState.Paired);
        OnClientAttached?.Invoke(this, clientId);
    }

    private void HandleClientDisconnected(JObject root)
    {
        var clientId = (string?)root["clientId"] ?? "";
        _clients.TryRemove(clientId, out _);

        if (_clientBindings.TryRemove(clientId, out var player))
            _playerBindings.TryRemove(player, out _);

        foreach (var reqId in _pendingRpc.Keys.ToList())
        {
            if (reqId.StartsWith($"{clientId}\0"))
            {
                if (_pendingRpc.TryRemove(reqId, out var pending) && !pending.Settled)
                {
                    pending.Settled = true;
                    pending.Tcs.TrySetException(new DglabException("Client disconnected"));
                }
            }
        }

        if (_clients.IsEmpty)
            SetState(SocketState.WaitingForPeer);

        OnClientDisconnected?.Invoke(this, clientId);
    }

    private void HandleAppMessage(JObject root)
    {
        var clientId = (string?)root["clientId"] ?? "";
        var dataElement = root["data"];
        if (dataElement == null) return;

        var raw = dataElement.ToString(Formatting.None);
        OnData?.Invoke(this, (clientId, raw));

        var data = JObject.Parse(raw);
        var t = (string?)data["t"];

        if (t == "ev")
        {
            HandleEvent(clientId, data);
        }
        else if (t == "resp")
        {
            HandleRpcResponse(clientId, data);
        }
    }

    private void HandleEvent(string clientId, JObject data)
    {
        var ev = (string?)data["ev"] ?? "";
        var js = JsonSerializer.Create(_jsonSettings);

        switch (ev)
        {
            case "devices.snapshot":
                var devices = data["devices"]?.ToObject<List<DeviceInfo>>(js) ?? new List<DeviceInfo>();
                _clients[clientId] = devices;
                ApplyCoyoteStates(devices);
                OnDevicesUpdated?.Invoke(this, (clientId, devices));
                foreach (var d in devices)
                    OnDeviceChanged?.Invoke(this, (clientId, d));
                break;

            case "devices.patch":
                var added = data["added"]?.ToObject<List<DeviceInfo>>(js) ?? new List<DeviceInfo>();
                var removed = data["removed"]?.ToObject<List<string>>(js) ?? new List<string>();

                ApplyCoyoteStates(added);
                foreach (var slotId in removed)
                    _deviceStates.TryRemove(slotId, out _);

                if (_clients.TryGetValue(clientId, out var currentDevices))
                {
                    foreach (var d in added)
                    {
                        var idx = currentDevices.FindIndex(x => x.SlotId == d.SlotId);
                        if (idx >= 0) currentDevices[idx] = d;
                        else currentDevices.Add(d);
                        OnDeviceChanged?.Invoke(this, (clientId, d));
                    }
                    currentDevices.RemoveAll(x => removed.Contains(x.SlotId));
                    OnDevicesUpdated?.Invoke(this, (clientId, currentDevices));
                }
                break;

            case "slots.patch":
                var slots = data["slots"];
                if (slots != null)
                {
                    var patches = slots.ToObject<List<SlotPatch>>(js) ?? new List<SlotPatch>();
                    foreach (var patch in patches)
                    {
                        ApplySlotPatchState(patch);
                        var evt = new DeviceInfo { SlotId = patch.SlotId };
                        if (patch.Props != null) evt.Props = patch.Props;
                        if (patch.SlotState != null) evt.SlotState = patch.SlotState;
                        OnDeviceChanged?.Invoke(this, (clientId, evt));
                    }
                }
                break;

            case "custom.action":
                var action = data["action"]?.Value<int>() ?? 0;
                OnAction?.Invoke(this, action);
                break;
        }
    }

    private void HandleRpcResponse(string clientId, JObject data)
    {
        var requestId = (string?)data["requestId"] ?? (string?)data["reqId"];
        if (requestId == null) return;

        var key = $"{clientId}\0{requestId}";
        if (!_pendingRpc.TryRemove(key, out var pending)) return;
        if (pending.Settled) return;

        pending.Settled = true;
        pending.Timer?.Dispose();

        if (data["error"] != null)
        {
            pending.Tcs.TrySetException(new DglabException((string?)data["error"] ?? "Unknown error"));
        }
        else
        {
            var resultToken = data["result"];
            var result = resultToken != null ? resultToken.ToObject<object>(JsonSerializer.Create(_jsonSettings)) : null;
            pending.Tcs.TrySetResult(new RpcResponse { Result = result });
        }
    }

    // ==================== Device State ====================

    private void ApplyCoyoteStates(List<DeviceInfo> devices)
    {
        foreach (var d in devices)
        {
            if (!IsCoyote(d.Type)) continue;
            var state = GetOrCreateState(d.SlotId);
            state.Type = d.Type;
            ApplyDeviceProps(state, d.Props);
            ApplySlotState(state, d.SlotState);
        }
    }

    private void ApplySlotPatchState(SlotPatch patch)
    {
        if (patch.SlotId == null) return;
        var state = GetOrCreateState(patch.SlotId);
        if (patch.Props != null) ApplyDeviceProps(state, patch.Props);
        if (patch.SlotState != null) ApplySlotState(state, patch.SlotState);
    }

    private CoyoteDeviceState GetOrCreateState(string slotId)
    {
        return _deviceStates.GetOrAdd(slotId, _ => new CoyoteDeviceState { SlotId = slotId });
    }

    private static void ApplyDeviceProps(CoyoteDeviceState state, Dictionary<string, object>? props)
    {
        if (props == null) return;
        if (props.TryGetValue("intensityA", out var ia))
            state.IntensityA = Convert.ToInt32(ia);
        if (props.TryGetValue("intensityB", out var ib))
            state.IntensityB = Convert.ToInt32(ib);
    }

    private static void ApplySlotState(CoyoteDeviceState state, Dictionary<string, object>? slotState)
    {
        if (slotState == null) return;
        state.IsConnected = slotState.TryGetValue("hasDevice", out var hd) && Convert.ToBoolean(hd);

        if (slotState.TryGetValue("channelA", out var ca) && ca is Dictionary<string, object> chA)
        {
            if (chA.TryGetValue("intensityMax", out var maxA))
                state.MaxIntensityA = Convert.ToInt32(maxA);
        }
        if (slotState.TryGetValue("channelB", out var cb) && cb is Dictionary<string, object> chB)
        {
            if (chB.TryGetValue("intensityMax", out var maxB))
                state.MaxIntensityB = Convert.ToInt32(maxB);
        }
    }

    private static bool IsCoyote(DeviceType type) =>
        type == DeviceType.Coyote020 || type == DeviceType.Coyote030;

    // ==================== Public State Getters ====================

    public CoyoteDeviceState? GetDeviceState(string slotId)
    {
        return _deviceStates.TryGetValue(slotId, out var s) ? s : null;
    }

    public CoyoteDeviceState? GetDeviceState()
    {
        foreach (var kv in _deviceStates)
            return kv.Value;
        return null;
    }

    public (int A, int B)? GetIntensity(string slotId)
    {
        var state = GetDeviceState(slotId);
        return state != null ? (state.IntensityA, state.IntensityB) : ((int, int)?)null;
    }

    public (int A, int B)? GetIntensity()
    {
        var state = GetDeviceState();
        return state != null ? (state.IntensityA, state.IntensityB) : ((int, int)?)null;
    }

    public int? GetIntensityA(string? slotId = null)
    {
        var state = slotId != null ? GetDeviceState(slotId) : GetDeviceState();
        return state?.IntensityA;
    }

    public int? GetIntensityB(string? slotId = null)
    {
        var state = slotId != null ? GetDeviceState(slotId) : GetDeviceState();
        return state?.IntensityB;
    }

    public (int MaxA, int MaxB)? GetMaxIntensity(string slotId)
    {
        var state = GetDeviceState(slotId);
        return state != null ? (state.MaxIntensityA, state.MaxIntensityB) : ((int, int)?)null;
    }

    public (int MaxA, int MaxB)? GetMaxIntensity()
    {
        var state = GetDeviceState();
        return state != null ? (state.MaxIntensityA, state.MaxIntensityB) : ((int, int)?)null;
    }

    private async Task<RpcResponse> SendRpcAsync(string clientId, RpcRequest request, int? timeout = null)
    {
        var requestId = request.ReqId;
        var key = $"{clientId}\0{requestId}";

        var tcs = new TaskCompletionSource<RpcResponse>();
        var effectiveTimeout = timeout ?? _options.ResponseTimeout;
        if (effectiveTimeout <= 0) effectiveTimeout = DefaultResponseTimeout;

        var timer = new CancellationTokenSource(effectiveTimeout);
        var pending = new PendingRpc
        {
            Tcs = tcs,
            Timer = timer,
            Settled = false
        };

        _pendingRpc[key] = pending;

        timer.Token.Register(() =>
        {
            if (_pendingRpc.TryRemove(key, out var p) && !p.Settled)
            {
                p.Settled = true;
                p.Tcs.TrySetException(new TimeoutException("RPC response timeout"));
            }
            timer.Dispose();
        });

        var frame = new MessageFrame<RpcRequest>
        {
            ClientId = clientId,
            Data = request
        };
        var json = JsonConvert.SerializeObject(frame, _jsonSettings);
        await SendRawAsync(json);

        return await tcs.Task;
    }

    private async Task SendRawAsync(string json)
    {
        if (_ws?.State != WebSocketState.Open)
            throw new DglabException("WebSocket is not connected");

        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private RpcRequest CreateRpcRequest(string method, object? data = null)
    {
        var requestId = $"v4-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}-{Guid.NewGuid().ToString("N").Substring(0, 6)}";
        return new RpcRequest
        {
            ReqId = requestId,
            RequestId = requestId,
            M = method,
            Data = data
        };
    }

    private void StartServerPing()
    {
        StopServerPing();
        _missedServerPongs = 0;
        _serverPingTimer = new Timer(_ =>
        {
            if (_missedServerPongs >= MaxMissedServerPongs)
            {
                _ = DisconnectAsync(1000, "ping_timeout");
                return;
            }

            try
            {
                var json = JsonConvert.SerializeObject(new PingFrame(), _jsonSettings);
                _ = SendRawAsync(json);
                _missedServerPongs++;
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, $"Ping error: {ex.Message}");
                _ = DisconnectAsync(1000, "ping_timeout");
            }
        }, null, ServerPingInterval, ServerPingInterval);
    }

    private void StopServerPing()
    {
        _serverPingTimer?.Dispose();
        _serverPingTimer = null;
        _missedServerPongs = 0;
    }

    // ==================== V3 Protocol Handling ====================

    private void HandleV3Message(JObject root, JToken typeProp)
    {
        var messageType = typeProp.ToString();

        switch (messageType)
        {
            case "bind":
                HandleV3Bind(root);
                break;
            case "msg":
            case "4":
                HandleV3Msg(root);
                break;
            case "break":
                HandleV3Break(root);
                break;
            case "error":
                {
                    var errMsg = (string?)root["message"] ?? "";
                    OnError?.Invoke(this, $"V3 server error: {errMsg}");
                }
                break;
            case "heartbeat":
                break;
        }
    }

    private void HandleV3Bind(JObject root)
    {
        var clientId = (string?)root["clientId"] ?? "";
        var targetId = (string?)root["targetId"];
        var message = (string?)root["message"];

        if (string.IsNullOrEmpty(targetId))
        {
            _targetId = clientId;
            SetState(SocketState.WaitingForPeer);

            _connectTcs.TrySetResult(new ConnectResult
            {
                TargetId = _targetId ?? "",
                Secret = null
            });
        }
        else if (message == "200")
        {
            _pairedTargetId = targetId!;
            _v3Device = new V3DeviceInfo { Type = DeviceType.Coyote030 };
            _clients.TryAdd(targetId!, new List<DeviceInfo>
            {
                new()
                {
                    SlotId = targetId!,
                    Name = "Coyote 3.0",
                    Type = DeviceType.Coyote030
                }
            });

            SetState(SocketState.Paired);

            if (Player != null)
                BindPlayerToClient(Player, targetId!);

            OnClientAttached?.Invoke(this, targetId!);
            OnDevicesUpdated?.Invoke(this, (targetId!, _clients[targetId!]));
        }
    }

    private void HandleV3Msg(JObject root)
    {
        var targetId = (string?)root["targetId"] ?? _pairedTargetId;
        var message = (string?)root["message"] ?? "";

        if (targetId != null)
        {
            OnData?.Invoke(this, (targetId, message));

            var strengthMatch = Regex.Match(
                message, @"^strength-(\d+)\+(\d+)\+(\d+)\+(\d+)$");
            if (strengthMatch.Success)
            {
                var deviceInfo = new DeviceInfo
                {
                    SlotId = targetId,
                    Name = "Coyote 3.0",
                    Type = DeviceType.Coyote030,
                    Props = new Dictionary<string, object>
                    {
                        ["strength"] = new Dictionary<string, object>
                        {
                            ["A"] = int.Parse(strengthMatch.Groups[1].Value),
                            ["B"] = int.Parse(strengthMatch.Groups[2].Value),
                        },
                        ["softLimit"] = new Dictionary<string, object>
                        {
                            ["A"] = int.Parse(strengthMatch.Groups[3].Value),
                            ["B"] = int.Parse(strengthMatch.Groups[4].Value),
                        }
                    }
                };
                OnDeviceChanged?.Invoke(this, (targetId, deviceInfo));
            }

            var feedbackMatch = Regex.Match(message, @"^feedback-(\d+)$");
            if (feedbackMatch.Success)
            {
                OnAction?.Invoke(this, int.Parse(feedbackMatch.Groups[1].Value));
            }
        }
    }

    private void HandleV3Break(JObject root)
    {
        var targetId = (string?)root["targetId"] ?? _pairedTargetId;
        _pairedTargetId = null;
        _v3Device = null;

        if (targetId != null)
        {
            if (_clientBindings.TryRemove(targetId, out var player))
                _playerBindings.TryRemove(player, out _);

            _clients.TryRemove(targetId, out _);
            OnClientDisconnected?.Invoke(this, targetId);
        }

        if (_targetId != null)
            SetState(SocketState.WaitingForPeer);
    }

    // ==================== V3 Command Methods ====================

    private async Task SendV3CommandAsync(V3LegacyCommand command)
    {
        if (_ws?.State != WebSocketState.Open)
            throw new DglabException("WebSocket is not connected");
        if (_targetId == null || _pairedTargetId == null)
            throw new DglabException("V3 pairing not complete");

        var frame = new Dictionary<string, object>
        {
            ["type"] = command.Type,
            ["message"] = command.Message,
            ["clientId"] = _targetId,
            ["targetId"] = _pairedTargetId,
        };
        if (command.Channel.HasValue) frame["channel"] = command.Channel.Value;
        if (command.Time.HasValue) frame["time"] = command.Time.Value;
        if (command.Strength.HasValue) frame["strength"] = command.Strength.Value;

        var json = JsonConvert.SerializeObject(frame, _jsonSettings);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public async Task AddStrengthV3Async(V3Channel channel, int step = 1)
    {
        var type = step >= 0 ? 2 : 1;
        var count = Math.Min(Math.Max(1, Math.Abs(step)), 200);
        for (var i = 0; i < count; i++)
            await SendV3CommandAsync(new V3LegacyCommand { Type = type, Channel = (int)channel, Message = "set channel" });
    }

    public async Task ReduceStrengthV3Async(V3Channel channel, int step = 1)
    {
        await AddStrengthV3Async(channel, -step);
    }

    public async Task SetStrengthV3Async(V3Channel channel, int strength)
    {
        await SendV3CommandAsync(new V3LegacyCommand
        {
            Type = 3,
            Channel = (int)channel,
            Message = "set channel",
            Strength = strength
        });
    }

    public async Task SendPulseV3Async(V3WaveOptions options)
    {
        var payload = JsonConvert.SerializeObject(options.Data);
        var channel = options.Channel.Length > 0 ? options.Channel.Substring(0, 1) : "A";
        await SendV3CommandAsync(new V3LegacyCommand
        {
            Type = "clientMsg",
            Channel = channel == "A" ? 1 : 2,
            Time = options.Time,
            Message = $"{channel}:{payload}"
        });
    }

    public async Task ClearPulseV3Async(V3Channel channel)
    {
        await SendV3CommandAsync(new V3LegacyCommand
        {
            Type = 4,
            Channel = (int)channel,
            Message = "clear"
        });
    }

    private void SetState(SocketState state)
    {
        if (State == state) return;
        State = state;
        OnStateChanged?.Invoke(this, state);
    }

    private class PendingRpc
    {
        public TaskCompletionSource<RpcResponse> Tcs { get; set; } = null!;
        public CancellationTokenSource? Timer { get; set; }
        public bool Settled { get; set; }
    }
}

public class DglabSocketOptions
{
    public string? Url { get; set; } = "wss://ws.dungeon-lab.cn";
    public int ConnectTimeout { get; set; } = 8000;
    public int ResponseTimeout { get; set; } = 8000;
    public SocketVersion Version { get; set; } = SocketVersion.V4;
    public string QrCodeServerUrl { get; set; } = "wss://ws.dungeon-lab.cn";
}

public class DglabException : Exception
{
    public DglabException(string message) : base(message) { }
    public DglabException(string message, Exception inner) : base(message, inner) { }
}
