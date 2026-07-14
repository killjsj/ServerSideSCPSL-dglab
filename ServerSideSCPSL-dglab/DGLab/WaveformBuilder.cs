using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace DglabKit;

/// <summary>
/// 自定义波形构建器。
/// 每帧 8 字节: 前 4 字节为通道 A 强度 (0-100), 后 4 字节为通道 B 强度 (0-100)。
/// </summary>
public class WaveformBuilder
{
    private readonly List<string> _frames = new();

    /// <summary>
    /// 添加一帧。a1-a4 为通道 A 的 4 个强度值，b1-b4 为通道 B。
    /// 每值范围 0-100。
    /// </summary>
    public WaveformBuilder AddFrame(int a1, int a2, int a3, int a4, int b1, int b2, int b3, int b4)
    {
        _frames.Add(
            $"{Clamp(a1):X2}{Clamp(a2):X2}{Clamp(a3):X2}{Clamp(a4):X2}" +
            $"{Clamp(b1):X2}{Clamp(b2):X2}{Clamp(b3):X2}{Clamp(b4):X2}");
        return this;
    }

    /// <summary>
    /// 添加一帧。a 为通道 A 统一强度，b 为通道 B 统一强度。
    /// </summary>
    public WaveformBuilder AddFrame(int a, int b)
    {
        var ca = Clamp(a).ToString("X2");
        var cb = Clamp(b).ToString("X2");
        _frames.Add($"{ca}{ca}{ca}{ca}{cb}{cb}{cb}{cb}");
        return this;
    }

    /// <summary>
    /// 添加通道 A 线性渐变。仅变化 A，B 保持 b 不变。
    /// </summary>
    public WaveformBuilder RampA(int from, int to, int steps, int b = 0)
    {
        var cb = Clamp(b).ToString("X2");
        for (var i = 0; i < steps; i++)
        {
            var a = steps == 1 ? to : from + (to - from) * i / (steps - 1);
            var ca = Clamp(a).ToString("X2");
            _frames.Add($"{ca}{ca}{ca}{ca}{cb}{cb}{cb}{cb}");
        }
        return this;
    }

    /// <summary>
    /// 添加通道 B 线性渐变。仅变化 B，A 保持 a 不变。
    /// </summary>
    public WaveformBuilder RampB(int from, int to, int steps, int a = 0)
    {
        var ca = Clamp(a).ToString("X2");
        for (var i = 0; i < steps; i++)
        {
            var b = steps == 1 ? to : from + (to - from) * i / (steps - 1);
            var cb = Clamp(b).ToString("X2");
            _frames.Add($"{ca}{ca}{ca}{ca}{cb}{cb}{cb}{cb}");
        }
        return this;
    }

    /// <summary>
    /// 添加双通道线性渐变。
    /// </summary>
    public WaveformBuilder Ramp(int aFrom, int aTo, int bFrom, int bTo, int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            var a = steps == 1 ? aTo : aFrom + (aTo - aFrom) * i / (steps - 1);
            var b = steps == 1 ? bTo : bFrom + (bTo - bFrom) * i / (steps - 1);
            var ca = Clamp(a).ToString("X2");
            var cb = Clamp(b).ToString("X2");
            _frames.Add($"{ca}{ca}{ca}{ca}{cb}{cb}{cb}{cb}");
        }
        return this;
    }

    /// <summary>
    /// 添加脉冲：在给定步数内 A 从 0 → peak → 0，B 从 0 → 0。
    /// </summary>
    public WaveformBuilder Pulse(int peak, int steps)
    {
        var half = steps / 2;
        RampA(0, peak, half);
        RampA(peak, 0, steps - half);
        return this;
    }

    /// <summary>
    /// 添加正弦波。A 在 0-sweepA 间摆动，B 固定为 b。
    /// </summary>
    public WaveformBuilder SineA(int sweepA, int steps, int b = 0)
    {
        for (var i = 0; i < steps; i++)
        {
            var a = (int)(sweepA * 0.5 * (1 - Math.Cos(2 * Math.PI * i / steps)));
            var ca = Clamp(a).ToString("X2");
            var cb = Clamp(b).ToString("X2");
            _frames.Add($"{ca}{ca}{ca}{ca}{cb}{cb}{cb}{cb}");
        }
        return this;
    }

    /// <summary>
    /// 添加恒值保持
    /// </summary>
    public WaveformBuilder Hold(int a, int b, int steps)
    {
        var ca = Clamp(a).ToString("X2");
        var cb = Clamp(b).ToString("X2");
        var frame = $"{ca}{ca}{ca}{ca}{cb}{cb}{cb}{cb}";
        for (var i = 0; i < steps; i++)
            _frames.Add(frame);
        return this;
    }

    /// <summary>
    /// 添加交替脉冲
    /// </summary>
    public WaveformBuilder Alternating(int peak, int steps, int offSteps = 2)
    {
        var caP = Clamp(peak).ToString("X2");
        var ca0 = Clamp(0).ToString("X2");
        var onFrame = $"{caP}{caP}{caP}{caP}{ca0}{ca0}{ca0}{ca0}";
        var offFrame = $"{ca0}{ca0}{ca0}{ca0}{ca0}{ca0}{ca0}{ca0}";
        for (var i = 0; i < steps - offSteps; i += 1 + offSteps)
        {
            _frames.Add(onFrame);
            for (var j = 0; j < offSteps; j++)
                _frames.Add(offFrame);
        }
        return this;
    }

    /// <summary>
    /// 从 (A强度, B强度) 列表生成波形帧。每个元组对应 1ms。
    /// </summary>
    public static List<string> FromTuples(IEnumerable<(int A, int B)> pulses)
    {
        var frames = new List<string>(pulses.Count());
        foreach (var (a, b) in pulses)
        {
            var ca = Clamp(a).ToString("X2");
            var cb = Clamp(b).ToString("X2");
            frames.Add($"{ca}{ca}{ca}{ca}{cb}{cb}{cb}{cb}");
        }
        return frames;
    }

    /// <summary>
    /// 从 (A强度, B强度) 列表生成波形帧。每个元组对应 1ms。
    /// </summary>
    public WaveformBuilder AddTuples(List<(int A, int B)> pulses)
    {
        foreach (var (a, b) in pulses)
        {
            var ca = Clamp(a).ToString("X2");
            var cb = Clamp(b).ToString("X2");
            _frames.Add($"{ca}{ca}{ca}{ca}{cb}{cb}{cb}{cb}");
        }
        return this;
    }

    /// <summary>
    /// 获取构建完成的波形帧
    /// </summary>
    public List<string> Build() => new(_frames);

    /// <summary>
    /// 清空已添加的帧
    /// </summary>
    public WaveformBuilder Clear()
    {
        _frames.Clear();
        return this;
    }

    private static int Clamp(int v) => Math.Max(0, Math.Min(100, v));
}
