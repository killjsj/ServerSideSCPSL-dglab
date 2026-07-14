using DglabKit;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.CustomHandlers;
using LabApi.Features.Wrappers;
using MEC;
using PlayerStatsSystem;
using RemoteAdmin.Communication;
using System;
using System.Collections.Generic;
using System.Net;
using UnityEngine;
using DeviceType = DglabKit.DeviceType;

namespace ServerSideSCPSL_dglab
{
    public class DGGameEventHandler : CustomEventsHandler
    {
        public static List<Player> enabledPlayers = new();
        public configs config;
        public static DglabSessionManager dglab;
        public void SendPulseOrIntensity(Player player, int time, int Ins, string Pulse = null, bool Immediate = false)
        {

            if (enabledPlayers.Contains(player))
            {
                if (KeybindSystem.Ins != null)
                {
                    if (!KeybindSystem.Ins.PlayerPrefers.TryGetValue(player, out var playerPrefer))
                    {
                        playerPrefer = new();
                        KeybindSystem.Ins.PlayerPrefers[player] = playerPrefer;
                    }
                    var s = dglab.GetSocket(player);
                    Ins = (int)Math.Ceiling(Ins * playerPrefer.GlobalMul);
                    var bChannelIns = (int)Math.Ceiling(Ins * playerPrefer.BChannelMul);
                    if(BaseIns.TryGetValue(player,out var v) && Time.time - v.Item2 <= config.hurtConfig.AddInsCountTime / 1000)
                    {
                        Ins += v.Item1;
                        bChannelIns += (int)Math.Ceiling(v.Item1 * playerPrefer.BChannelMul);
                    }
                    Ins = Mathf.Clamp(Ins, 0, s?.GetMaxIntensity()?.MaxA ?? 200);
                    bChannelIns = Mathf.Clamp(bChannelIns, 0, s?.GetMaxIntensity()?.MaxB ?? 200);
                    if (s != null)
                    {
                        foreach (var item in s.GetDevices())
                        {
                            if (item != null)
                            {
                                if (item.Type != DeviceType.Coyote030 && item.Type != DeviceType.Coyote020)
                                {
                                    s.DisconnectAsync(reason: "不支持除郊狼外其他设备"); break;
                                }
                                s.SetTempIntensityAsync(item.SlotId, Channel.A, Ins, time, new() { Immediate = true });
                                s.SetTempIntensityAsync(item.SlotId, Channel.B, bChannelIns, time, new() { Immediate = true });
                                if (!string.IsNullOrEmpty(Pulse) && plugin.TryGetPulse(Pulse, out var re))
                                {
                                    if (re.isIntArray)
                                    {
                                        s.SendPulseAsync(item.SlotId, Channel.A, time, re.array, new() { Immediate = Immediate });
                                        s.SendPulseAsync(item.SlotId, Channel.B, time, re.array, new() { Immediate = Immediate });
                                    }
                                    else
                                    {
                                        s.SendPulseAsync(item.SlotId, Channel.A, time, re.binFrames, new() { Immediate = Immediate });
                                        s.SendPulseAsync(item.SlotId, Channel.B, time, re.binFrames, new() { Immediate = Immediate });
                                    }
                                    KeybindSystem.Ins.UpdatePlayersCurrentStatus(player, Ins, Pulse);
                                }
                                else
                                {
                                    KeybindSystem.Ins.UpdatePlayersCurrentStatus(player, Ins, "");

                                }

                            }
                        }

                    }
                }
            }
        }
        public void init()
        {
            var apiAddr = config?.ApiAddress ?? "ws://127.0.0.1:9998";
            int port = 9998;
            if (Uri.TryCreate(apiAddr, UriKind.Absolute, out var uri) && uri.Port > 0)
                port = uri.Port;

            string qrAddr = config?.QRCodeAddress;
            if (string.IsNullOrEmpty(qrAddr))
            {
                qrAddr = $"ws://{ServerConsole.Ip}:{port}";
            }

            dglab = new DglabSessionManager(new DglabSocketOptions() { Url = apiAddr, QrCodeServerUrl = qrAddr });
        }
        public override void OnServerShutdown()
        {
            base.OnServerShutdown();
            dglab?.Dispose();
        }
        public override void OnPlayerJoined(PlayerJoinedEventArgs ev)
        {
            base.OnPlayerJoined(ev);
            if (KeybindSystem.Ins != null)
            {
                KeybindSystem.Ins.EnableShocking.SendValueUpdate(true, receiveFilter: x => x == ev.Player.ReferenceHub);
                KeybindSystem.Ins.Register(ev.Player,KeybindSystem.Ins.EnableShocking);
            }
        }
        public override void OnPlayerLeft(PlayerLeftEventArgs ev)
        {
            enabledPlayers.Remove(ev.Player);
            base.OnPlayerLeft(ev);
            dglab?.DisconnectPlayerAsync(ev.Player, reason: "leaving...");
            hasTakenDamage.Remove(ev.Player);
            BaseIns.Remove(ev.Player);
            if (ResetHandles.TryGetValue(ev.Player, out var leftHandle))
            {
                Timing.KillCoroutines(leftHandle);
                ResetHandles.Remove(ev.Player);
            }
        }
        public static Dictionary<Player, float> hasTakenDamage = new();
        public static Dictionary<Player, (int,float)> BaseIns = new();
        // scheduled reset handles per player to allow cancelling when player leaves or role changes
        public static Dictionary<Player, MEC.CoroutineHandle> ResetHandles = new();
        public override void OnPlayerHurt(PlayerHurtEventArgs ev)
        {
            base.OnPlayerHurt(ev);
            if (ev.Player != null && ev.DamageHandler is StandardDamageHandler handler)
            {
                var tname = ev.DamageHandler.GetType().Name;
                string pulse = config.hurtConfig.Pulse;
                var Ins = (int)Math.Ceiling(config.hurtConfig.strength * 0.1 * handler.TotalDamageDealt);
                var time = config.hurtConfig.time;
                var addcount = ev.Player.IsSCP ? config.hurtConfig.AddInsCountSCP : config.hurtConfig.AddInsCount;
                if (config.hurtConfig.SpecialPulses.TryGetValue(tname, out var specialPulses))
                {
                    pulse = specialPulses;
                }
                if (config.hurtConfig.SpecialStrength.TryGetValue(tname, out var specialstr))
                {
                    Ins = (int)Math.Ceiling(specialstr * 0.1 * handler.TotalDamageDealt); ;
                }
                if (config.hurtConfig.SpecialTime.TryGetValue(tname, out var specialtime))
                {
                    time = specialtime;
                }
                if (ev.Player.IsSCP)
                {
                    if (config.hurtConfig.SpecialAddInsCountSCP.TryGetValue(tname, out var specialCot))
                    {
                        addcount = specialCot;
                    }
                }
                else
                {
                    if (config.hurtConfig.SpecialAddInsCount.TryGetValue(tname, out var specialCot))
                    {
                        addcount = specialCot;
                    }
                }

                SendPulseOrIntensity(ev.Player, time, Ins, pulse, config.hurtConfig.Immediate);
                if (!hasTakenDamage.ContainsKey(ev.Player))
                    hasTakenDamage[ev.Player] = 0f;
                hasTakenDamage[ev.Player] += handler.TotalDamageDealt;
                if (addcount <= 0)
                {
                    return;
                }
                var player = ev.Player;
                if (enabledPlayers.Contains(player))
                {
                    if (KeybindSystem.Ins != null)
                    {
                        if (!KeybindSystem.Ins.PlayerPrefers.TryGetValue(player, out var playerPrefer))
                        {
                            
                            playerPrefer = new();
                            KeybindSystem.Ins.PlayerPrefers[player] = playerPrefer;
                        }
                        var s = dglab.GetSocket(player);
                        if (s != null)
                        {
                            float lostPercent = (hasTakenDamage[ev.Player] / ev.Player.MaxHealth) * 100f;
                            int addIns = (int)Math.Floor(lostPercent / addcount);
                            if (addIns >= 1)
                            {
                                float consumedDamage = addIns * addcount / 100f * ev.Player.MaxHealth;
                                hasTakenDamage[ev.Player] -= consumedDamage;
                                foreach (var item in s.GetDevices())
                                {
                                    if (item != null)
                                    {
                                        if (item.Type != DeviceType.Coyote030 && item.Type != DeviceType.Coyote020)
                                        {
                                            s.DisconnectAsync(reason: "不支持除郊狼外其他设备"); break;
                                        }
                                        s.AddIntensityAsync(item.SlotId, Channel.A, addIns);
                                        s.AddIntensityAsync(item.SlotId, Channel.B, (int)Math.Ceiling(addIns * playerPrefer.BChannelMul));

                                    }
                                }
                                if(!BaseIns.ContainsKey(player))
                                    BaseIns[player] = (0,Time.time);
                                BaseIns[player] = (BaseIns[player].Item1 + addIns,Time.time);
                                var newTs = BaseIns[player].Item2;
                                // cancel any previously scheduled reset for this player to avoid duplicates
                                if (ResetHandles.TryGetValue(player, out var prevHandle))
                                {
                                    Timing.KillCoroutines(prevHandle);
                                    ResetHandles.Remove(player);
                                }
                                var handle = Timing.CallDelayed((config.hurtConfig.AddInsCountTime / 1000f) + 0.1f, () =>
                                {
                                    if (BaseIns.TryGetValue(player, out var v) && v.Item2 == newTs && Time.time - v.Item2 >= config.hurtConfig.AddInsCountTime / 1000f)
                                    {
                                        var socket = dglab?.GetSocket(player);
                                        if (socket != null)
                                        {
                                            foreach (var item in socket.GetDevices())
                                            {
                                                if (item != null)
                                                {
                                                    if (item.Type != DeviceType.Coyote030 && item.Type != DeviceType.Coyote020)
                                                    {
                                                        socket.DisconnectAsync(reason: "不支持除郊狼外其他设备"); continue;
                                                    }
                                                    socket.ResetIntensityAsync(item.SlotId, Channel.A);
                                                    socket.ResetIntensityAsync(item.SlotId, Channel.B);
                                                }
                                            }
                                        }
                                        if (KeybindSystem.Ins != null) KeybindSystem.Ins.UpdatePlayersCurrentStatus(player, 0, "");
                                        BaseIns.Remove(player);
                                        ResetHandles.Remove(player);
                                    }
                                });
                                ResetHandles[player] = handle;
                            }
                        }
                    }
                }
            }
        }
        public override void OnPlayerChangedRole(PlayerChangedRoleEventArgs ev)
        {
            base.OnPlayerChangedRole(ev);
            if (ev.Player != null)
            {
                hasTakenDamage.Remove(ev.Player);
                BaseIns.Remove(ev.Player);
                if (ResetHandles.TryGetValue(ev.Player, out var roleHandle))
                {
                    Timing.KillCoroutines(roleHandle);
                    ResetHandles.Remove(ev.Player);
                }
                hasTakenDamage[ev.Player] = 0;
                var player = ev.Player;
                if (enabledPlayers.Contains(player))
                {
                    if (KeybindSystem.Ins != null)
                    {
                        if (!KeybindSystem.Ins.PlayerPrefers.TryGetValue(player, out var playerPrefer))
                        {
                            playerPrefer = new();
                            KeybindSystem.Ins.PlayerPrefers[player] = playerPrefer;
                        }
                        var s = dglab.GetSocket(player);

                        if (s != null)
                        {
                            foreach (var item in s.GetDevices())
                            {
                                if (item != null)
                                {
                                    if (item.Type != DeviceType.Coyote030 && item.Type != DeviceType.Coyote020)
                                    {
                                        s.DisconnectAsync(reason: "不支持除郊狼外其他设备"); continue;
                                    }
                                    s.ResetIntensityAsync(item.SlotId, Channel.A);
                                    s.ResetIntensityAsync(item.SlotId, Channel.B);
                                }
                            }
                            
                            KeybindSystem.Ins.UpdatePlayersCurrentStatus(player, 0, "");

                        }
                    }
                }
            }
        }
        public override void OnPlayerDeath(PlayerDeathEventArgs ev)
        {
            base.OnPlayerDeath(ev);
            if (ev.Player != null)
            {
                SendPulseOrIntensity(ev.Player, config.diedConfig.time, config.diedConfig.strength, config.diedConfig.Pulse, true);
            }
        }
    }
}