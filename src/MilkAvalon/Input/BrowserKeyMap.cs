using Avalonia.Input;

namespace MilkAvalon.Input;

/// <summary>
/// Avalonia 的 <see cref="Key"/> → 浏览器 <c>event.keyCode</c>。
/// </summary>
/// <remarks>
/// <para>
/// Shadertoy 的 <c>keyboard</c> 通道按浏览器 keyCode 索引（<c>A</c>=65、<c>0</c>=48、方向键 37..40），
/// 上游 <c>MilkDrop.ShaderToy/Channels/KeyboardKeyMap.cs</c> 维护的是同一张表、只是源枚举是 GLFW 的 <c>Key</c>。
/// 本仓库的输入来自 Avalonia，所以这里按同一套编码重写一遍（数值必须与上游一致，否则同一个作品在两条线里表现不同）。
/// </para>
/// <para>没映射到的键返回 0（<see cref="MilkDrop.ShaderToy.Channels.KeyboardChannel"/> 会忽略 0）。</para>
/// </remarks>
public static class BrowserKeyMap
{
    /// <summary>取浏览器 keyCode；没有对应值时返回 0。</summary>
    public static int ToKeyCode(Key key) => key switch
    {
        // 字母
        Key.A => 65, Key.B => 66, Key.C => 67, Key.D => 68, Key.E => 69, Key.F => 70, Key.G => 71,
        Key.H => 72, Key.I => 73, Key.J => 74, Key.K => 75, Key.L => 76, Key.M => 77, Key.N => 78,
        Key.O => 79, Key.P => 80, Key.Q => 81, Key.R => 82, Key.S => 83, Key.T => 84, Key.U => 85,
        Key.V => 86, Key.W => 87, Key.X => 88, Key.Y => 89, Key.Z => 90,

        // 主键盘数字
        Key.D0 => 48, Key.D1 => 49, Key.D2 => 50, Key.D3 => 51, Key.D4 => 52,
        Key.D5 => 53, Key.D6 => 54, Key.D7 => 55, Key.D8 => 56, Key.D9 => 57,

        // 小键盘
        Key.NumPad0 => 96, Key.NumPad1 => 97, Key.NumPad2 => 98, Key.NumPad3 => 99, Key.NumPad4 => 100,
        Key.NumPad5 => 101, Key.NumPad6 => 102, Key.NumPad7 => 103, Key.NumPad8 => 104, Key.NumPad9 => 105,
        Key.Multiply => 106, Key.Add => 107, Key.Subtract => 109, Key.Decimal => 110, Key.Divide => 111,

        // 控制键
        Key.Back => 8, Key.Tab => 9, Key.Enter => 13, Key.LeftShift => 16, Key.RightShift => 16,
        Key.LeftCtrl => 17, Key.RightCtrl => 17, Key.LeftAlt => 18, Key.RightAlt => 18,
        Key.CapsLock => 20, Key.Escape => 27, Key.Space => 32, Key.PageUp => 33, Key.PageDown => 34,
        Key.End => 35, Key.Home => 36, Key.Left => 37, Key.Up => 38, Key.Right => 39, Key.Down => 40,
        Key.Insert => 45, Key.Delete => 46, Key.LWin => 91, Key.RWin => 92,

        // 功能键
        Key.F1 => 112, Key.F2 => 113, Key.F3 => 114, Key.F4 => 115, Key.F5 => 116, Key.F6 => 117,
        Key.F7 => 118, Key.F8 => 119, Key.F9 => 120, Key.F10 => 121, Key.F11 => 122, Key.F12 => 123,

        _ => 0,
    };
}
