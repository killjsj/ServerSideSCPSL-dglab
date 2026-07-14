using DglabKit;
using LabApi.Events.CustomHandlers;
using LabApi.Features.Console;
using LabApi.Loader.Features.Plugins;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ServerSideSCPSL_dglab
{
    public class plugin : Plugin<configs>
    {
        public override string Name => "ServerSideSCPSL_dglab";

        public override string Description => "将电击做到了服务端,Warning:用这个的我默认会以下内容:会配置依赖 配置不乱写 自主分析错误 上github看源代码 会有效提问等";

        public override string Author => "killjsj";

        public override Version RequiredApiVersion => new Version(1,1,7);

        public override void Disable()
        {
            CustomHandlersManager.UnregisterEventsHandler(Events);
        }

        public static Dictionary<string, int[][]> availablePulses = new(StringComparer.OrdinalIgnoreCase);
        public DGGameEventHandler Events { get; } = new();
        public static int[][] ConvertToArray(List<PulseFrame> frames)
        {
            var result = new int[frames.Count][];
            for (int i = 0; i < frames.Count; i++)
            {
                var f = frames[i];
                result[i] = new[] { f.A, f.A, f.A, f.A, f.B, f.B, f.B, f.B };
            }
            return result;
        }
        public struct PulseStruct
        {
            public bool isIntArray;
            public int[][] array;
            public List<string> binFrames;
        }
        public static bool TryGetPulse(string name, out PulseStruct pulse)
        {
            pulse = new PulseStruct();
            if(availablePulses.TryGetValue(name, out var pulses))
            {
                pulse.array = pulses;
                pulse.isIntArray = true;
                return true;
            } else if(CoyoteWaveforms.All.TryGetValue(name.ToUpper(),out pulse.binFrames))
            {
                pulse.isIntArray = false;
                return true;
            }
            return false;
        }
        public override void Enable()
        {
            foreach (var item in Config.PulseDefines)
            {
                try
                {
                    availablePulses[item.Key] = ConvertToArray(item.Value);
                }catch(Exception e)
                {
                    Logger.Error($"Error at converting!: {e}");
                }
            }
            Events.config = this.Config;
            Events.init();
            new KeybindSystem().Init();
            CustomHandlersManager.RegisterEventsHandler(Events);
        }
    }
    public class PulseFrame
    {
        public int A { get; set; }
        public int B { get; set; }
    }

    public class configs
    {
        [Description("额外波形定义 从 (A强度, B强度) 列表生成波形帧 每个元组对应 1ms 同时可调用自带(如EXTRUSTION 详情查看 https://github.com/dungeonlab-open/dglab-kit/blob/main/src/waveform/coyote.ts ) 不区分大小写 此配置优先")]
        public Dictionary<string,List<PulseFrame>> PulseDefines { get; set; } = new() {
            {"example",new(){ new PulseFrame{A=0,B=0}, new PulseFrame{A=0,B=1}, new PulseFrame{A=1,B=0} } }
        };
        [Description("api地址 插件连接用 详见 https://github.com/dungeonlab-open/dglab-websocket-server")]
        public string ApiAddress { get; set; } = "ws://127.0.0.1:9998";
        [Description("api地址 api连接用 要求公网可访问 详见 https://github.com/dungeonlab-open/dglab-websocket-server 可留空使插件读取ServerConsole.Ip")]
        public string QRCodeAddress { get; set; } = "ws://127.0.0.1:9998";
        public OnDiedConfig diedConfig { get; set; } = new();
        [Description("注:只处理StandardDamageHandler及其子类型")]
        public OnHurtConfig hurtConfig { get; set; } = new();

    }
    public class OnDiedConfig
    {
        [Description("此为波形id 可为空 空则只设置强度时间")]
        public string Pulse { get; set; } = "EXTRUSTION";
        [Description("此为死亡后一键开火时长(ms)")]
        public int time { get; set; } = 5000;
        [Description("此为死亡后一键开火强度 最大100")]
        public int strength { get; set; } = 30;
    }
    public class OnHurtConfig
    {
        [Description("此为通用波形id 可为空 空则只设置强度时间")]
        public string Pulse { get; set; } = "EXTRUSTION";
        [Description("此为受伤后一键开火时长(ms) 建议小于等于服务器每刻时长(1s/60t)")]
        public int time { get; set; } = 10;
        [Description("此为受伤后一键开火强度 最大100,实际施加强度=扣血*0.01*strength后向上取整")]
        public int strength { get; set; } = 10;
        [Description("此为受伤后是否跳过队列直接施加波形")]
        public bool Immediate { get; set; } = true;
        [Description("此为受伤后该角色期间受到多少伤害(百分比)加一强度")]
        public float AddInsCount { get; set; } = 50;
        [Description("此为受伤后该角色期间受到多少伤害(百分比)加一强度(scp版)")]
        public float AddInsCountSCP { get; set; } = 950;
        [Description("此为特殊波形id 对应伤害类型(DamageHandler的类名 需使用dnspy) 可为空 空则只设置强度时间")]
        public Dictionary<string, string> SpecialPulses { get; set; } = new()
        {
            {"ExplosionDamageHandler","EXTRUSTION" }
        };
        [Description("此为特殊强度 对应伤害类型(DamageHandler的类名 需使用dnspy) 最大100")]
        public Dictionary<string, int> SpecialStrength { get; set; } = new()
        {
            {"ExplosionDamageHandler",30 }
        };
        [Description("此为特殊时间 对应伤害类型(DamageHandler的类名 需使用dnspy) 单位ms")]
        public Dictionary<string, int> SpecialTime { get; set; } = new()
        {
            {"ExplosionDamageHandler",10 }
        };

        [Description("此为特殊\"受到多少伤害加一强度\" 对应伤害类型(DamageHandler的类名 需使用dnspy)")]
        public Dictionary<string, int> SpecialAddInsCount { get; set; } = new()
        {
            {"ExplosionDamageHandler",50 }
        };
        [Description("此为特殊\"受到多少伤害加一强度(scp版)\" 对应伤害类型(DamageHandler的类名 需使用dnspy)")]
        public Dictionary<string, int> SpecialAddInsCountSCP { get; set; } = new()
        {
            {"ExplosionDamageHandler",700 }
        };
        [Description("此为特殊\"受到多少伤害加一强度\"的时长(ms) 每次受伤刷新")]
        public int AddInsCountTime { get; set; } = 5000;


    }
}
