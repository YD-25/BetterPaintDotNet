using System.Collections;
using System.Reflection;
using PaintDotNet;

namespace LayerChannelLive;

internal sealed partial class LayerChannelPanel
{
    private Document? channelHistoryDocument;

    private sealed class ChannelUndoSnapshot : IDisposable
    {
        public Document Document { get; }
        public Layer Layer { get; }
        public Surface TruthSnapshot { get; }
        public List<ChannelKind> Order { get; }
        public bool[] Visibility { get; }
        public bool LayerVisible { get; }
        public bool RestoresPixels { get; }
        public object? NativeHistory { get; }
        public object? NativeUndoHead { get; }

        public ChannelUndoSnapshot(Document document, Layer layer, Surface truth, List<ChannelKind> order,
            bool[] visibility, bool restoresPixels, object? nativeHistory)
        {
            this.Document = document;
            this.Layer = layer;
            this.TruthSnapshot = ChannelViewLayer.TryClone(truth) ?? throw new InvalidOperationException("无法创建通道撤销快照");
            this.Order = new List<ChannelKind>(order);
            this.Visibility = (bool[])visibility.Clone();
            this.LayerVisible = PdnModel.GetVisible(layer);
            this.RestoresPixels = restoresPixels;
            this.NativeHistory = nativeHistory;
            this.NativeUndoHead = ReadUndoHead(nativeHistory);
        }

        public void Dispose() => this.TruthSnapshot.Dispose();
    }

    private static object? ReadUndoHead(object? history)
    {
        object? last = null;
        if (history?.GetType().GetProperty("UndoStack", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(history) is IEnumerable items)
            foreach (object? item in items) last = item;
        return last;
    }

    private object? CurrentNativeHistory() => this.documentWorkspace?.GetType()
        .GetProperty("History", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(this.documentWorkspace);

    private ChannelUndoSnapshot CaptureChannelSnapshot(Layer layer, bool restoresPixels)
    {
        Surface truth = this.GetOrCreateTruthSurface(layer);
        return new ChannelUndoSnapshot(this.document!, layer, truth, this.GetChannelOrder(layer),
            this.GetChannelVisibility(layer), restoresPixels, this.CurrentNativeHistory());
    }

    private void PushUndoSnapshot(Layer layer)
    {
        CommitActiveTool(this.documentWorkspace);
        if (this.document == null || !this.document.Layers.Contains(layer)) return;
        this.SynchronizeChannelHistory(this.document);
        this.undoStack.Push(this.CaptureChannelSnapshot(layer, true));
        this.ClearHistoryStack(this.redoStack);
        this.TrimHistory(this.undoStack);
    }

    private void PushLayerVisibilitySnapshot(Layer layer)
    {
        CommitActiveTool(this.documentWorkspace);
        if (this.document == null || !this.document.Layers.Contains(layer)) return;
        this.SynchronizeChannelHistory(this.document);
        this.undoStack.Push(this.CaptureChannelSnapshot(layer, false));
        this.ClearHistoryStack(this.redoStack);
        this.TrimHistory(this.undoStack);
    }

    private void ClearHistoryStack(Stack<ChannelUndoSnapshot> stack)
    {
        while (stack.Count > 0) stack.Pop().Dispose();
    }

    private void TrimHistory(Stack<ChannelUndoSnapshot> stack)
    {
        if (stack.Count <= MaxUndoDepth) return;
        var entries = stack.ToArray();
        stack.Clear();
        foreach (var removed in entries.Skip(MaxUndoDepth)) removed.Dispose();
        for (int i = MaxUndoDepth - 1; i >= 0; i--) stack.Push(entries[i]);
    }

    private void SynchronizeChannelHistory(Document? document)
    {
        if (ReferenceEquals(this.channelHistoryDocument, document)) return;
        this.ClearHistoryStack(this.undoStack);
        this.ClearHistoryStack(this.redoStack);
        this.channelHistoryDocument = document;
    }

    private bool IsApplicable(ChannelUndoSnapshot snapshot) =>
        ReferenceEquals(snapshot.Document, this.document) && snapshot.Document.Layers.Contains(snapshot.Layer) &&
        PdnModel.TryGetSurface(snapshot.Layer) is Surface live &&
        (!snapshot.RestoresPixels || live.Width == snapshot.TruthSnapshot.Width && live.Height == snapshot.TruthSnapshot.Height);

    private void DiscardStaleHistory(Stack<ChannelUndoSnapshot> stack)
    {
        while (stack.Count > 0 && !this.IsApplicable(stack.Peek())) stack.Pop().Dispose();
    }

    private bool CanRestoreHistory(Stack<ChannelUndoSnapshot> stack)
    {
        this.SynchronizeChannelHistory(this.document);
        this.DiscardStaleHistory(stack);
        if (stack.Count == 0) return false;
        var snapshot = stack.Peek();
        // 原生效果、画笔、移动和排序有自己的记录，较新的原生记录必须先由宿主撤销。
        return ReferenceEquals(snapshot.NativeHistory, this.CurrentNativeHistory()) &&
            ReferenceEquals(snapshot.NativeUndoHead, ReadUndoHead(snapshot.NativeHistory));
    }

    public bool PerformUndo() => this.RestoreHistory(this.undoStack, this.redoStack, "撤销");
    public bool PerformRedo() => this.RestoreHistory(this.redoStack, this.undoStack, "重做");

    private bool RestoreHistory(Stack<ChannelUndoSnapshot> from, Stack<ChannelUndoSnapshot> to, string action)
    {
        CommitActiveTool(this.documentWorkspace);
        if (!this.CanRestoreHistory(from)) return false;
        var snapshot = from.Peek();
        Layer layer = snapshot.Layer; // 记录所属图层，不使用当前选中图层。
        to.Push(this.CaptureChannelSnapshot(layer, snapshot.RestoresPixels));
        using var restored = from.Pop();
        if (restored.RestoresPixels)
        {
            if (this.layerTruthSurfaces.Remove(layer, out var previous)) previous.Dispose();
            this.layerTruthSurfaces[layer] = ChannelViewLayer.TryClone(restored.TruthSnapshot)!;
            this.layerChannelOrders[layer] = new List<ChannelKind>(restored.Order);
            this.layerChannelVisibilities[layer] = (bool[])restored.Visibility.Clone();
            this.ApplyChannelOrderOrVisibilityChange(layer);
        }
        else
        {
            // 图层整体显隐是属性操作，不回放旧像素，也不改变另一个图层的通道会话。
            layer.Visible = restored.LayerVisible;
            this.RemoveLayerThumbnail(layer);
            this.RefreshFromHost();
            this.Invalidate();
        }
        this.TrimHistory(to);
        MoveToolGuideService.Instance.ForceResync();
        Boot.Log($"[{action}] 图层 '{layer.Name}'，剩余记录 {from.Count}");
        return true;
    }
}
