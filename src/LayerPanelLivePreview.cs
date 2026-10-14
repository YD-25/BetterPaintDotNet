using PaintDotNet;
using PaintDotNet.Rendering;

namespace LayerChannelLive;

internal sealed partial class LayerChannelPanel
{
    private System.Windows.Forms.Timer? livePreviewTimer;
    private Surface? livePreviewSurface;
    private Layer? livePreviewLayer;
    private IRenderer<ColorBgra>? livePreviewSource;
    private CancellationTokenSource? livePreviewCancellation;
    private int livePreviewVersion;
    private bool livePreviewBusy;
    private bool livePreviewPending;

    private Surface? LivePreviewFor(Layer layer) => ReferenceEquals(layer, this.livePreviewLayer) ? this.livePreviewSurface : null;

    private void RequestLivePreview()
    {
        if (this.IsDisposed) return;
        this.livePreviewPending = true;
        this.livePreviewTimer ??= CreatePreviewTimer();
        this.livePreviewTimer.Start();
    }

    private System.Windows.Forms.Timer CreatePreviewTimer()
    {
        var timer = new System.Windows.Forms.Timer { Interval = 40 };
        timer.Tick += (_, _) => this.UpdateLivePreview();
        return timer;
    }

    // 宿主每次同步时也检查渲染器，覆盖提交、撤销、换图层和切工具。
    internal void SynchronizeLivePreviews()
    {
        if (this.activeLayer == null) { this.ClearLivePreview(); return; }
        var source = MoveToolGuideService.Instance.GetLiveLayerRenderer(this.activeLayer);
        if (source == null)
        {
            if (this.livePreviewSurface != null || this.livePreviewPending) this.ClearLivePreview();
            return;
        }
        if (!ReferenceEquals(source, this.livePreviewSource) || !ReferenceEquals(this.livePreviewLayer, this.activeLayer))
            this.RequestLivePreview();
    }

    private void UpdateLivePreview()
    {
        if (!this.livePreviewPending || this.livePreviewBusy || this.activeLayer == null || !this.IsHandleCreated) return;
        Layer layer = this.activeLayer;
        Document? doc = this.document;
        var source = MoveToolGuideService.Instance.GetLiveLayerRenderer(layer);
        if (source == null) { this.ClearLivePreview(); return; }
        this.livePreviewPending = false;
        this.livePreviewBusy = true;
        this.livePreviewSource = source;
        int version = this.livePreviewVersion;
        var cancellation = new CancellationTokenSource();
        this.livePreviewCancellation?.Dispose();
        this.livePreviewCancellation = cancellation;
        int previewW = this.CurrentPreviewWidth();
        ChannelKind? kind = this.channelViewKind;
        Surface? truth = null;
        // 通道视图的原生渲染是灰阶：只把正在编辑的通道合入完整数据预览，避免串色。
        bool hasPresentation = this.HasChannelPresentation(layer);
        var order = this.GetChannelOrder(layer).ToArray();
        var visibility = (bool[])this.GetChannelVisibility(layer).Clone();
        if (kind != null || hasPresentation)
        {
            var backup = kind != null
                ? this.GetChannelViewBackupFor(layer) ?? ChannelViewLayer.GetCompositedSurfaceFor(layer)
                : this.GetOrCreateTruthSurface(layer);
            if (backup != null)
            {
                try { truth = LivePreviewSampler.Sample(backup, previewW, PreviewMaxHeight); }
                catch (Exception ex)
                {
                    this.livePreviewBusy = false;
                    this.livePreviewPending = true;
                    Boot.LogDiag("live-preview-backup", "通道预览备份暂不可读: " + ex.Message);
                    return;
                }
            }
        }
        _ = Task.Run(() =>
        {
            Surface? snapshot = null;
            try
            {
                snapshot = LivePreviewSampler.Sample(source, previewW, PreviewMaxHeight, cancellation.Token);
                if (truth != null)
                {
                    if (kind != null) ChannelViewLayer.CommitSurfaceToChannel(snapshot, truth, kind.Value);
                    else ChannelPresentation.MergeVisibleEdits(snapshot, truth, order, visibility);
                    snapshot.Dispose();
                    snapshot = truth;
                    truth = null;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Boot.LogDiag("live-preview", "原生预览采样已取消: " + ex.Message); }
            finally { truth?.Dispose(); }
            Surface? completed = snapshot;
            if (this.IsDisposed || !this.IsHandleCreated) { completed?.Dispose(); return; }
            try
            {
                this.BeginInvoke(new Action(() =>
                {
                    this.livePreviewBusy = false;
                    if (this.IsDisposed || version != this.livePreviewVersion || !ReferenceEquals(layer, this.activeLayer) ||
                        !ReferenceEquals(doc, this.document) || kind != this.channelViewKind ||
                        !order.SequenceEqual(this.GetChannelOrder(layer)) || !visibility.SequenceEqual(this.GetChannelVisibility(layer)) ||
                        previewW != this.CurrentPreviewWidth() || completed == null)
                    {
                        completed?.Dispose();
                        if (version == this.livePreviewVersion) this.livePreviewPending = true;
                        return;
                    }
                    this.livePreviewSurface?.Dispose();
                    this.livePreviewSurface = completed;
                    this.livePreviewLayer = layer;
                    this.InvalidateThumbnails();
                    this.InvalidateChannels();
                    this.Invalidate();
                    if (!this.livePreviewPending) this.livePreviewTimer?.Stop();
                }));
            }
            catch (InvalidOperationException) { completed?.Dispose(); }
        });
    }

    private void ClearLivePreview()
    {
        this.livePreviewVersion++;
        this.livePreviewPending = false;
        this.livePreviewCancellation?.Cancel();
        this.livePreviewTimer?.Stop();
        this.livePreviewSurface?.Dispose();
        this.livePreviewSurface = null;
        this.livePreviewLayer = null;
        this.livePreviewSource = null;
        this.InvalidateThumbnails();
        this.InvalidateChannels();
        this.Invalidate();
    }

    private void DisposeLivePreviews()
    {
        this.livePreviewCancellation?.Cancel();
        this.livePreviewTimer?.Dispose();
        this.livePreviewSurface?.Dispose();
        this.livePreviewCancellation?.Dispose();
    }
}
