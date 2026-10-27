namespace MilkAvalon.Services;

/// <summary>
/// 预设列表的展示项：列表里只显示文件名，完整路径放 ToolTip。
/// </summary>
/// <remarks>单独建类型是为了编译绑定（AOT 下反射绑定会失效，见 plan.md §9）。</remarks>
public sealed record PresetItem(string Path, string Name, string Folder)
{
    public static PresetItem FromPath(string path) => new(
        path,
        System.IO.Path.GetFileName(path),
        System.IO.Path.GetDirectoryName(path) ?? string.Empty);
}
