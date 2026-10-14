using System.Reflection;
using PaintDotNet;

namespace LayerChannelLive;

internal static class LayerReorder
{
    // 在实际 LayerList 上逐步交换。若接入宿主，将这些交换合为一个原生撤销步骤。
    public static void Move(Document document, object? workspace, int source, int destination)
    {
        if (source < 0 || destination < 0 || source >= document.Layers.Count || destination >= document.Layers.Count)
            throw new ArgumentOutOfRangeException(nameof(source));
        if (source == destination) return;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        object? history = workspace?.GetType().GetProperty("History", flags)?.GetValue(workspace);
        Assembly? hostAssembly = workspace?.GetType().GetInterfaces()
            .FirstOrDefault(t => t.FullName == "PaintDotNet.IHistoryWorkspace")?.Assembly ?? workspace?.GetType().Assembly;
        Type? mementoType = hostAssembly?.GetType("PaintDotNet.HistoryMementos.SwapLayerHistoryMemento");
        Type? compoundType = hostAssembly?.GetType("PaintDotNet.HistoryMementos.CompoundHistoryMemento");
        object? compound = null;
        MethodInfo? add = null;
        MethodInfo? push = null;
        if (history != null)
        {
            if (mementoType == null || compoundType == null) throw new InvalidOperationException("宿主图层撤销类型不可用");
            compound = Activator.CreateInstance(compoundType, new object?[] { "调整图层顺序", null });
            add = compoundType.GetMethod("AddMemento", flags);
            push = history.GetType().GetMethods(flags).Single(m => m.Name == "PushNewMemento" && m.GetParameters().Length == 1);
        }
        int direction = Math.Sign(destination - source);
        // 准备好全部撤销记录后再修改集合，避免类型/构造失败时留下半次排序。
        if (compound != null)
        {
            for (int current = source; current != destination; current += direction)
            {
                object memento = Activator.CreateInstance(mementoType!, new object?[] { "调整图层顺序", null, workspace, current, current + direction })!;
                add!.Invoke(compound, new[] { memento });
            }
        }
        using ((IDisposable?)document.Layers.GetType().GetMethod("UseChangeScope", flags)?.Invoke(document.Layers, null))
        {
            for (int current = source; current != destination; current += direction)
                document.Layers.SwapAt(current, current + direction);
        }
        if (compound != null) push!.Invoke(history, new[] { compound });
        document.Invalidate();
    }
}
