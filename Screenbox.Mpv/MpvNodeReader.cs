// Screenbox.Mpv — MpvNode → 托管 MpvNodeValue 安全深拷贝（SPEC §5.1）。
// 只读拷贝，不修改/释放原生内存；归调用方所有的节点（mpv_get_property /
// mpv_command_node 结果）由调用方在拷贝后 mpv_free_node_contents；
// 事件（MPV_EVENT_PROPERTY_CHANGE）携带的节点归 libmpv 所有，不得 free。

using System;
using System.Collections.Generic;
using Screenbox.Mpv.Interop;

namespace Screenbox.Mpv;

/// <summary>MpvNodeValue 的判别标签。</summary>
public enum MpvNodeKind
{
    None,
    String,
    Flag,
    Int64,
    Double,
    Array,
    Map,
}

/// <summary>
/// mpv_node 的托管只读快照。按 <see cref="Kind"/> 取对应 As* 访问器；
/// 类型不匹配抛 <see cref="InvalidOperationException"/>。
/// </summary>
public sealed class MpvNodeValue
{
    private readonly object? _value;

    private MpvNodeValue(MpvNodeKind kind, object? value)
    {
        Kind = kind;
        _value = value;
    }

    /// <summary>MPV_FORMAT_NONE（属性不可用/未知类型）。</summary>
    public static MpvNodeValue None { get; } = new(MpvNodeKind.None, null);

    public MpvNodeKind Kind { get; }

    public string AsString => Kind == MpvNodeKind.String ? (string)_value! : throw WrongKind(MpvNodeKind.String);

    public bool AsBoolean => Kind == MpvNodeKind.Flag ? (bool)_value! : throw WrongKind(MpvNodeKind.Flag);

    public long AsInt64 => Kind == MpvNodeKind.Int64 ? (long)_value! : throw WrongKind(MpvNodeKind.Int64);

    public double AsDouble => Kind == MpvNodeKind.Double ? (double)_value! : throw WrongKind(MpvNodeKind.Double);

    public IReadOnlyList<MpvNodeValue> AsList =>
        Kind == MpvNodeKind.Array ? (IReadOnlyList<MpvNodeValue>)_value! : throw WrongKind(MpvNodeKind.Array);

    public IReadOnlyDictionary<string, MpvNodeValue> AsMap =>
        Kind == MpvNodeKind.Map ? (IReadOnlyDictionary<string, MpvNodeValue>)_value! : throw WrongKind(MpvNodeKind.Map);

    internal static MpvNodeValue FromString(string value) => new(MpvNodeKind.String, value);

    internal static MpvNodeValue FromBoolean(bool value) => new(MpvNodeKind.Flag, value);

    internal static MpvNodeValue FromInt64(long value) => new(MpvNodeKind.Int64, value);

    internal static MpvNodeValue FromDouble(double value) => new(MpvNodeKind.Double, value);

    internal static MpvNodeValue FromList(List<MpvNodeValue> value) => new(MpvNodeKind.Array, value);

    internal static MpvNodeValue FromMap(Dictionary<string, MpvNodeValue> value) => new(MpvNodeKind.Map, value);

    public override string ToString() => Kind switch
    {
        MpvNodeKind.String => AsString,
        MpvNodeKind.Flag => AsBoolean ? "true" : "false",
        MpvNodeKind.Int64 => AsInt64.ToString(),
        MpvNodeKind.Double => AsDouble.ToString("G"),
        MpvNodeKind.Array => $"[{AsList.Count} items]",
        MpvNodeKind.Map => $"{{{AsMap.Count} keys}}",
        _ => "(none)",
    };

    private InvalidOperationException WrongKind(MpvNodeKind expected) =>
        new($"MpvNodeValue kind is {Kind}, not {expected}.");
}

/// <summary>mpv_node 深拷贝读取器。拷贝后与原生存储无关联。</summary>
internal static unsafe class MpvNodeReader
{
    public static MpvNodeValue Copy(MpvNode* node)
    {
        if (node == null)
            return MpvNodeValue.None;

        switch (node->Format)
        {
            case MpvFormat.String:
                return MpvNodeValue.FromString(Utf8Marshaller.ToString(node->String) ?? string.Empty);
            case MpvFormat.Flag:
                return MpvNodeValue.FromBoolean(node->Flag != 0);
            case MpvFormat.Int64:
                return MpvNodeValue.FromInt64(node->Int64);
            case MpvFormat.Double:
                return MpvNodeValue.FromDouble(node->Double);
            case MpvFormat.NodeArray:
            {
                MpvNodeList* list = node->List;
                if (list == null || list->Num <= 0)
                    return MpvNodeValue.FromList(new List<MpvNodeValue>(0));

                var result = new List<MpvNodeValue>(list->Num);
                for (int i = 0; i < list->Num; i++)
                    result.Add(Copy(NodeAt(list, i)));
                return MpvNodeValue.FromList(result);
            }
            case MpvFormat.NodeMap:
            {
                MpvNodeList* list = node->List;
                if (list == null || list->Num <= 0)
                    return MpvNodeValue.FromMap(new Dictionary<string, MpvNodeValue>(0));

                var result = new Dictionary<string, MpvNodeValue>(list->Num, StringComparer.Ordinal);
                for (int i = 0; i < list->Num; i++)
                {
                    // keys 不允许为 NULL（client.h 保证）。
                    string key = Utf8Marshaller.ToString(list->Keys[i]) ?? string.Empty;
                    result[key] = Copy(NodeAt(list, i));
                }

                return MpvNodeValue.FromMap(result);
            }
            default:
                // NONE / OSD_STRING / BYTE_ARRAY / 未知格式：不产生假设（client.h 要求）。
                return MpvNodeValue.None;
        }
    }

    /// <summary>
    /// 按原生数组步长取第 i 个节点。步长不能取 sizeof(MpvNode)：x86 MinGW 构建下
    /// 原生 sizeof(mpv_node)==12（见 MpvNodeLayout 注释）。
    /// </summary>
    private static MpvNode* NodeAt(MpvNodeList* list, int index) =>
        (MpvNode*)((byte*)list->Values + index * MpvNodeLayout.NodeStride);
}
