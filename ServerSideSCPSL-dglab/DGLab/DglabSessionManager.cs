using LabApi.Features.Wrappers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace DglabKit;

public class DglabSessionManager : IDisposable
{
    private readonly ConcurrentDictionary<Player, DglabSocket> _sockets = new();
    private readonly DglabSocketOptions? _baseOptions;

    public DglabSocket? GetSocket(Player player)
        => _sockets.TryGetValue(player, out var s) ? s : null;

    public string? GetClientID(Player player)
        => GetSocket(player)?.IDToClientID(player);

    public Player? GetPlayer(string clientId)
    {
        foreach (var kvp in _sockets)
        {
            var player = kvp.Value.ClientIDToID(clientId);
            if (player != null) return player;
        }
        return null;
    }

    public delegate Awaitable OnCreatePlayerSocketDone(Player player, string TargetId, string QrCodeUrl, SocketVersion version);
    public event OnCreatePlayerSocketDone? OnCreatePlayerSocket;

    public async Task<(string TargetId, string QrCodeUrl)> CreatePlayerAsync(Player player, SocketVersion version = SocketVersion.V4)
    {
        var (targetId, qrCodeUrl) = await CreateSocketForPlayerAsync(player, version);
        OnCreatePlayerSocket?.Invoke(player, targetId, qrCodeUrl, version);
        return (targetId, qrCodeUrl);
    }

    private async Task<(string TargetId, string QrCodeUrl)> CreateSocketForPlayerAsync(Player player, SocketVersion version = SocketVersion.V4)
    {
        var options = _baseOptions ?? new DglabSocketOptions();
        var socketOptions = new DglabSocketOptions
        {
            Url = options.Url,
            ConnectTimeout = options.ConnectTimeout,
            ResponseTimeout = options.ResponseTimeout,
            Version = version,
            QrCodeServerUrl = options.QrCodeServerUrl
        };
        var socket = new DglabSocket(socketOptions) { Player = player };

        socket.OnClientAttached += (_, clientId) =>
        {
            _onPlayerConnected?.Invoke(player);
        };

        socket.OnClientDisconnected += (_, _) =>
        {
            _onPlayerDisconnected?.Invoke(player);
        };
        socket.OnDisconnected += (_, _) =>
        {
            _onPlayerDisconnected?.Invoke(player);
        };
        socket.OnError += (_, msg) =>
        {
            _onError?.Invoke(player, msg);
        };

        var result = await socket.ConnectAsync();
        _sockets[player] = socket;

        var qrCodeUrl = BuildQrCodeUrl(result.TargetId, socketOptions.QrCodeServerUrl, version);
        return (result.TargetId, qrCodeUrl);
    }

    public async Task DisconnectPlayerAsync(Player player, int code = 4000, string reason = "")
    {
        if (_sockets.TryRemove(player, out var socket))
        {
            await socket.DisconnectAsync(code, reason);
            socket.Dispose();
        }
    }

    public async Task DisconnectAllAsync()
    {
        var tasks = new List<Task>();
        foreach (var playerId in _sockets.Keys.ToArray())
        {
            if (_sockets.TryRemove(playerId, out var socket))
                tasks.Add(socket.DisconnectAsync());
        }
        await Task.WhenAll(tasks);
    }

    public void Dispose()
    {
        foreach (var kvp in _sockets)
            kvp.Value.Dispose();
        _sockets.Clear();
        GC.SuppressFinalize(this);
    }

    public delegate Awaitable OnPlayerConnectedArgs(Player player);
    public delegate Awaitable OnPlayerDisconnectedArgs(Player player);
    public delegate Awaitable OnErrorArgs(Player player, string Error);

    private event OnPlayerConnectedArgs? _onPlayerConnected;
    private event OnPlayerDisconnectedArgs? _onPlayerDisconnected;
    private event OnErrorArgs? _onError;

    public event OnPlayerConnectedArgs OnPlayerConnected
    {
        add => _onPlayerConnected += value;
        remove => _onPlayerConnected -= value;
    }

    public event OnPlayerDisconnectedArgs OnPlayerDisconnected
    {
        add => _onPlayerDisconnected += value;
        remove => _onPlayerDisconnected -= value;
    }

    public event OnErrorArgs OnError
    {
        add => _onError += value;
        remove => _onError -= value;
    }

    public DglabSessionManager(DglabSocketOptions? baseOptions = null)
    {
        _baseOptions = baseOptions;
    }

    private static string BuildQrCodeUrl(string targetId, string qrCodeServerUrl, SocketVersion version = SocketVersion.V4)
    {
        var url = qrCodeServerUrl.TrimEnd('/');
        var appSocketUrl = Uri.EscapeDataString($"{url}/?tid={targetId}");
        return $"https://dungeon-lab.cn/s/?v=1&action=socket&url={appSocketUrl}";
    }
}
