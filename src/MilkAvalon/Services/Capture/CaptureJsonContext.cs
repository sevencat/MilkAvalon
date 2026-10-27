using System.Text.Json.Serialization;

namespace MilkAvalon.Services.Capture;

/// <summary>
/// 抓取窗口里那两处 JSON 用量的源生成上下文（AOT 下不能走反射序列化）。
/// </summary>
/// <remarks>
/// 只序列化/反序列化 <c>string</c> 与 <c>string[]</c>：
/// ① 把待下载的图片 URL 数组拼进页内脚本；
/// ② 把 <c>InvokeScript</c> 返回的 JSON 字符串（带引号）还原成普通字符串。
/// </remarks>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class CaptureJsonContext : JsonSerializerContext;
