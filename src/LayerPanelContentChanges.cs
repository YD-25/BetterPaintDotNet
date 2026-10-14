using System.Reflection;
using PaintDotNet;
using PaintDotNet.Rendering;

namespace LayerChannelLive;

internal sealed partial class LayerChannelPanel
{
    private readonly HashSet<Layer> observedLayers = new();
    private readonly Dictionary<Layer, Delegate> layerPixelHandlers = new();
    private readonly HashSet<Layer> dirtyLayers = new();
    private readonly object contentChangeGate = new();
    private System.Windows.Forms.Timer? contentChangeTimer;
    private bool processingContentChanges;

    private void SynchronizeLayerSubscriptions(Document? doc)
    {
        var layers = PdnModel.GetLayers(doc).ToHashSet();
        foreach (Layer layer in this.observedLayers.Where(layer => !layers.Contains(layer)).ToArray())
        {
            if (this.layerPixelHandlers.Remove(layer, out var handler))
                typeof(Layer).GetEvent("Invalidated", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetRemoveMethod(true)!.Invoke(layer, new object[] { handler });
            typeof(Layer).GetEvent("RendererInvalidated", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetRemoveMethod(true)?.Invoke(layer, new object[] { new EventHandler(this.OnLayerRendererInvalidated) });
            this.observedLayers.Remove(layer);
            this.RemoveLayerThumbnail(layer);
            if (this.layerTruthSurfaces.Remove(layer, out var truth)) truth.Dispose();
        }
        foreach (Layer layer in layers)
        {
            if (!this.observedLayers.Add(layer)) continue;
            EventInfo evt = typeof(Layer).GetEvent("Invalidated", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
            Delegate handler = Delegate.CreateDelegate(evt.EventHandlerType!, this,
                GetType().GetMethod(nameof(this.OnLayerPixelsInvalidated), BindingFlags.Instance | BindingFlags.NonPublic)!);
            evt.GetAddMethod(true)!.Invoke(layer, new object[] { handler });
            this.layerPixelHandlers[layer] = handler;
            typeof(Layer).GetEvent("RendererInvalidated", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetAddMethod(true)?.Invoke(layer, new object[] { new EventHandler(this.OnLayerRendererInvalidated) });
        }
        this.contentChangeTimer ??= new System.Windows.Forms.Timer { Interval = 40 };
        if (!this.contentChangeTimer.Enabled)
        {
            this.contentChangeTimer.Tick -= this.OnContentChangeTick;
            this.contentChangeTimer.Tick += this.OnContentChangeTick;
            this.contentChangeTimer.Start();
        }
    }

    private void OnLayerPixelsInvalidated(object? sender, EventArgs e) => this.RecordContentChange(sender as Layer);
    private void OnLayerRendererInvalidated(object? sender, EventArgs e) => this.RecordContentChange(sender as Layer);
    private void RecordContentChange(Layer? layer)
    {
        if (layer == null || this.IsDisposed) return;
        lock (this.contentChangeGate) this.dirtyLayers.Add(layer);
    }

    private void OnContentChangeTick(object? sender, EventArgs e) => this.ProcessPendingContentChanges();
    private void ProcessPendingContentChanges()
    {
        if (this.IsDisposed || this.processingContentChanges) return;
        Layer[] changed;
        lock (this.contentChangeGate)
        {
            if (this.dirtyLayers.Count == 0) return;
            changed = this.dirtyLayers.ToArray();
            this.dirtyLayers.Clear();
        }
        this.processingContentChanges = true;
        try
        {
            foreach (Layer layer in changed)
            {
                this.RemoveLayerThumbnail(layer);
                // 缓存仅代表当前像素；原生反色、填充、效果、撤销及变换提交后必须过期。
                // 当前通道会话的 truth 是数据源，不能因灰阶显示的临时写入而丢弃。
                bool isLayerInChannelView = ReferenceEquals(layer, this.channelViewLayer) ||
                    (ChannelViewLayer.IsShowing && ReferenceEquals(layer, ChannelViewLayer.CurrentLayer));
                if (!isLayerInChannelView && !this.HasChannelPresentation(layer) &&
                    this.layerTruthSurfaces.Remove(layer, out var truth))
                    truth.Dispose();
                if (ReferenceEquals(layer, this.activeLayer))
                {
                    this.InvalidateChannels();
                    var source = MoveToolGuideService.Instance.GetLiveLayerRenderer(layer);
                    if (source != null) this.RequestLivePreview();
                    else if (this.livePreviewSurface != null) this.ClearLivePreview();
                }
            }
            this.Invalidate();
        }
        finally { this.processingContentChanges = false; }
    }

    private void RemoveLayerThumbnail(Layer layer)
    {
        foreach (var key in this.thumbnailCache.Keys.Where(key => ReferenceEquals(key.Layer, layer)).ToArray())
        {
            this.thumbnailCache[key].Dispose();
            this.thumbnailCache.Remove(key);
        }
    }

    private Surface? CreateChannelDisplayPreview(Layer layer, int width, int height)
    {
        Surface? backup = this.GetChannelViewBackupFor(layer);
        Surface? display = PdnModel.TryGetSurface(layer);
        if (backup == null || display == null || this.channelViewKind == null) return null;
        Surface? result = null;
        try
        {
            result = LivePreviewSampler.Sample(backup, width, height);
            using var displayed = LivePreviewSampler.Sample(display, width, height);
            ChannelViewLayer.CommitSurfaceToChannel(displayed, result, this.channelViewKind.Value);
            return result;
        }
        catch { result?.Dispose(); return null; }
    }

    private void DisposeContentNotifications()
    {
        this.contentChangeTimer?.Dispose();
        foreach (Layer layer in this.observedLayers)
        {
            if (this.layerPixelHandlers.Remove(layer, out var handler))
                typeof(Layer).GetEvent("Invalidated", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetRemoveMethod(true)!.Invoke(layer, new object[] { handler });
            typeof(Layer).GetEvent("RendererInvalidated", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetRemoveMethod(true)?.Invoke(layer, new object[] { new EventHandler(this.OnLayerRendererInvalidated) });
        }
        this.observedLayers.Clear();
        lock (this.contentChangeGate) this.dirtyLayers.Clear();
    }
}
