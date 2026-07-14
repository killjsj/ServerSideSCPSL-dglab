using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using QRCoder;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Pool;
using UserSettings.ServerSpecific;
using static UnityEngine.Rendering.RayTracingAccelerationStructure;
using Logger = LabApi.Features.Console.Logger;

namespace ServerSideSCPSL_dglab
{
    public class PlayerPrefer
    {
        public float BChannelMul = 1.0f;
        public float GlobalMul = 1.0f;
    }
    public class KeybindSystem
    {
        public static KeybindSystem Ins;
        public int enableshockSettingID = 781391;
        public SSTwoButtonsSetting EnableShocking;
        public int CurrentStatusSettingID = 911783;
        public SSTextArea CurrentStatus;
        public int QRCodeSettingID = 911378;
        public SSTextArea QRCode;
        public int BChannelMulSettingID = 791138;
        public SSPlaintextSetting BChannelMulSetting;
        public int GlobalMulSettingID = 791318;
        public SSPlaintextSetting GlobalMulSetting;
        public Dictionary<Player, List<ServerSpecificSettingBase>> PlayerMenuCache { get; } = new Dictionary<Player, List<ServerSpecificSettingBase>>();
        public Dictionary<Player, PlayerPrefer> PlayerPrefers { get; } = new();
        public void Init()
        {
            EnableShocking = new(enableshockSettingID, "是否启用郊狼", "yes", "no", true, "别死!", 255, true);
            QRCode = new(QRCodeSettingID, "");
            CurrentStatus = new(CurrentStatusSettingID, "");
            GlobalMulSetting = new(GlobalMulSettingID, "全局强度乘数", "1", contentType: TMPro.TMP_InputField.ContentType.DecimalNumber,isServerOnly:true);
            BChannelMulSetting = new(BChannelMulSettingID, "B通道强度乘数", "1", contentType: TMPro.TMP_InputField.ContentType.DecimalNumber, isServerOnly: true);
            DGGameEventHandler.dglab.OnCreatePlayerSocket += Dglab_OnCreatePlayerSocket;
            DGGameEventHandler.dglab.OnPlayerConnected += Dglab_OnPlayerConnected;
            DGGameEventHandler.dglab.OnPlayerDisconnected += Dglab_OnPlayerDisconnected;
            DGGameEventHandler.dglab.OnError += Dglab_OnError;
            ServerSpecificSettingsSync.ServerOnSettingValueReceived += OnKeyBindUpdated;
            Ins = this;
        }
        public void UpdatePlayersCurrentStatus(Player player,int str,string pulse)
        {
            CurrentStatus.SendTextUpdate($"目前强度(ab通道共用):{str},服务端波形名称:{pulse}",receiveFilter:x=>x == player.ReferenceHub);
        }
        private async Awaitable Dglab_OnError(Player player, string Error)
        {
            try
            {
                await Awaitable.MainThreadAsync();
                Unregister(player, QRCode);
                Unregister(player, CurrentStatus);
                Unregister(player, BChannelMulSetting);
                Unregister(player, GlobalMulSetting);
                Logger.Warn($"Player:{player} 's dglab met a error:{Error}");
                player.SendBroadcast($"dglab met a error:{Error},Disconnecting", 4,shouldClearPrevious:true);
                DGGameEventHandler.dglab.DisconnectPlayerAsync(player,reason: $"Player:{player} 's dglab met a error:{Error}");
                            CreatedSocketPlayers.Remove(player);
            }
            catch (Exception ex)
            {
                Logger.Error($"Dglab_OnError throw a except!,reason:{ex}");
            }
        }

        private async Awaitable Dglab_OnPlayerDisconnected(Player player)
        {
            try
            {
                await Awaitable.MainThreadAsync();
                Unregister(player, QRCode);
                Unregister(player, CurrentStatus);
                Unregister(player, BChannelMulSetting);
                Unregister(player, GlobalMulSetting);
                EnableShocking.SendValueUpdate(true,receiveFilter:x=>x == player.ReferenceHub);
                            CreatedSocketPlayers.Remove(player);
                DGGameEventHandler.enabledPlayers.Remove(player);
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed To Unreg QRCODE and unreg currentStatus,reason:{ex}");
            }
        }

        private async Awaitable Dglab_OnPlayerConnected(Player player)
        {
            try
            {
                await Awaitable.MainThreadAsync();
                Unregister(player, QRCode);
                Unregister(player, CurrentStatus);
                Unregister(player, BChannelMulSetting);
                Unregister(player, GlobalMulSetting);
                Register(player, CurrentStatus);
                Register(player, BChannelMulSetting);
                Register(player, GlobalMulSetting);
                DGGameEventHandler.enabledPlayers.Add(player);
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed To Unreg QRCODE and append currentStatus,reason:{ex}");
            }
        }

        public IEnumerable<ServerSpecificSettingBase> Register(Player player, ServerSpecificSettingBase setting, bool bypassCheck = false)
            => Register(player, new ServerSpecificSettingBase[] { setting }, bypassCheck);

        public IEnumerable<ServerSpecificSettingBase> Register(Player player, IEnumerable<ServerSpecificSettingBase> settings, bool bypassCheck = false)
        {
            if (player == null) return Enumerable.Empty<ServerSpecificSettingBase>();

            if (!PlayerMenuCache.TryGetValue(player, out var playerMenu))
            {
                playerMenu = new List<ServerSpecificSettingBase>();
                PlayerMenuCache[player] = playerMenu;
            }

            var result = InRegister(
                player,
                settings.Where(x => bypassCheck || !playerMenu.Any(y => y.SettingId == x.SettingId))
            ).ToList();

            playerMenu.AddRange(result);
            return result;
        }
        static readonly List<ServerSpecificSettingBase> Settings = new List<ServerSpecificSettingBase>();
        public static void SendToAll(Func<Player, bool> predicate)
        {
            foreach (Player item in Player.List)
            {
                if (predicate(item))
                {
                    SendToPlayer(item);
                }
            }
        }
        public static void SendToPlayer(Player player)
        {
            ServerSpecificSettingsSync.SendToPlayer(player.ReferenceHub);
        }
        public static IEnumerable<ServerSpecificSettingBase> InRegister(Player player, IEnumerable<ServerSpecificSettingBase> settings)
        {
            List<ServerSpecificSettingBase> list = new List<ServerSpecificSettingBase>();
            list.AddRange(settings.Where(x=>x!=null));
            ServerSpecificSettingsSync.DefinedSettings = (ServerSpecificSettingsSync.DefinedSettings ?? Array.Empty<ServerSpecificSettingBase>()).Concat(list.Select((ServerSpecificSettingBase s) => s)).ToArray();
            SendToPlayer(player);
            return list;
        }
        public static IEnumerable<ServerSpecificSettingBase> InUnregister(Func<Player, bool> predicate, IEnumerable<ServerSpecificSettingBase> settings)
        {
            List<ServerSpecificSettingBase> list = ListPool<ServerSpecificSettingBase>.Get();
            list.AddRange(ServerSpecificSettingsSync.DefinedSettings);
            List<ServerSpecificSettingBase> result = new List<ServerSpecificSettingBase>((settings ?? Settings).Where((ServerSpecificSettingBase setting) => list.Remove(setting)));
            ServerSpecificSettingsSync.DefinedSettings = list.ToArray();
                SendToAll(predicate);
            

            ListPool<ServerSpecificSettingBase>.Release(list);
            return result;
        }
        public IEnumerable<ServerSpecificSettingBase> Unregister(Player player, ServerSpecificSettingBase setting = null, bool bypassCheck = false)
            => Unregister(player, new ServerSpecificSettingBase[] { setting }, bypassCheck);

        public IEnumerable<ServerSpecificSettingBase> Unregister(Player player, IEnumerable<ServerSpecificSettingBase> settings = null, bool bypassCheck = false)
        {
            if (player == null) return Enumerable.Empty<ServerSpecificSettingBase>();

            if (!PlayerMenuCache.TryGetValue(player, out var playerMenu) || playerMenu.Count == 0)
                return Enumerable.Empty<ServerSpecificSettingBase>();

            var result = InUnregister(
                x => x==player,
                settings.Where(x => bypassCheck || playerMenu.Any(y => y.SettingId == x.SettingId))
            ).ToList();

            playerMenu.RemoveAll(x => result.Contains(x));
            return result;
        }
        private async UnityEngine.Awaitable Dglab_OnCreatePlayerSocket(Player player, string TargetId, string QrCodeUrl, DglabKit.SocketVersion version)
        {
            try
            {
                await Awaitable.BackgroundThreadAsync();
                string qrAsAscii = "";
                using (var generator = new QRCodeGenerator())
                {
                    QRCodeData qrData = generator.CreateQrCode(QrCodeUrl, QRCodeGenerator.ECCLevel.L);
                    using (var asciiQr = new AsciiQRCode(qrData))
                    {
                        qrAsAscii = asciiQr.GetGraphic(1, "█", "<color=#00000000>█</color>");
                    }
                }
                await Awaitable.MainThreadAsync();
                Logger.Info($"QRCode for player:{player} is ready!,url: {QrCodeUrl}  targetID:{TargetId}");
                Unregister(player, QRCode);
                Register(player, QRCode);
                QRCode.SendUpdate($"<size=12><line-height=75%>DGLab QRCode(version:{version}):\n{qrAsAscii}</line-height></size>","",receiveFilter:x=>x == player.ReferenceHub);

            }
            catch (Exception ex) {
                player.SendBroadcast($"Failed To get QRCODE,reason:{ex.Message}",4,shouldClearPrevious:true);
                Logger.Error($"Failed To get QRCODE,reason:{ex}");
            }
        }
        public static List<Player> CreatedSocketPlayers = new();
        public void OnKeyBindUpdated(ReferenceHub refhub, ServerSpecificSettingBase settingBase)
        {
            try
            {
                if (refhub != null && settingBase != null)
                {
                    var player = Player.Get(refhub);
                    if (settingBase is SSTwoButtonsSetting twoButtonsSetting)
                    {
                        if (twoButtonsSetting.SettingId == enableshockSettingID)
                        {
                            if (twoButtonsSetting.SyncIsB)
                            {
                                //disable
                                DGGameEventHandler.dglab.DisconnectPlayerAsync(player, reason: "主动");
                                DGGameEventHandler.enabledPlayers.Remove(player);
                                CreatedSocketPlayers.Remove(player);
                            }
                            else if (!CreatedSocketPlayers.Contains(player))
                            {

                                DGGameEventHandler.dglab.CreatePlayerAsync(player);
                                CreatedSocketPlayers.Add(player);
                            }
                        }
                    }
                    if (settingBase is SSPlaintextSetting plaintextSetting)
                    {
                        if (settingBase.SettingId == BChannelMulSettingID && !string.IsNullOrEmpty(plaintextSetting.SyncInputText))
                        {
                            if (!PlayerPrefers.TryGetValue(player, out var playerPrefer))
                            {
                                playerPrefer = new();
                                PlayerPrefers[player] = playerPrefer;
                            }
                            playerPrefer.BChannelMul = float.Parse(plaintextSetting.SyncInputText);
                            if (playerPrefer.BChannelMul >= 20)
                            {
                                player.SendBroadcast("乘数过高!", 5, shouldClearPrevious: true);
                                playerPrefer.BChannelMul = 20;
                                plaintextSetting.SendValueUpdate("20", receiveFilter: x => x == player.ReferenceHub);
                            }
                        }
                        else if (settingBase.SettingId == GlobalMulSettingID)
                        {
                            if (!PlayerPrefers.TryGetValue(player, out var playerPrefer))
                            {
                                playerPrefer = new();
                                PlayerPrefers[player] = playerPrefer;
                            }
                            playerPrefer.GlobalMul = float.Parse(plaintextSetting.SyncInputText);
                            if (playerPrefer.GlobalMul >= 20)
                            {
                                player.SendBroadcast("乘数过高!", 5, shouldClearPrevious: true);
                                playerPrefer.GlobalMul = 20;
                                plaintextSetting.SendValueUpdate("20", receiveFilter: x => x == player.ReferenceHub);
                            }
                        }
                    }

                }
            }
            catch (FormatException) { }
            catch (Exception e)
            {
                Logger.Error(e);
            }
        }
    }
}
