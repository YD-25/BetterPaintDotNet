using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PaintDotNet;

namespace LayerChannelLive;

/// <summary>
/// 增强移动工具（Move Selected Pixels tool / 移动所选像素工具）：
/// 当用户按住 Shift + 鼠标左键拖动图片时：
/// 1. 显示辅助线（画布边缘参考线、角落对齐线、吸附高亮线）。
/// 2. 靠近画布角落（(0,0)、(W,0)、(0,H)、(W,H)）及边缘时精确吸附。
/// 3. 松开 Shift 或松开鼠标时，辅助线立刻消失，吸附坐标精准生效。
/// </summary>
internal sealed class MoveToolGuideService : IDisposable, IMessageFilter
{
    private static MoveToolGuideService? instance;

    private object? workspace;
    private Control? canvasControl;
    private Document? document;
    private GuideOverlayForm? overlayForm;




    private object? currentHookedTool;
    private object? currentHookedTransformControl;
    private Delegate? editChangedHandler;
    private Delegate? editingBeginHandler;
    private Delegate? editingFinishedHandler;
    private Delegate? editingCancelledHandler;

    private bool isDragging;
    private bool isAdjusting;
    private bool isPointerReleasePending;

    // 所有吸附阈值均以“屏幕像素”定义，运行时按画布缩放比例换算为文档像素，任意缩放级别下手感一致。
    // 进入阈值 < 保持阈值（迟滞），在阈值边缘不会反复跳变。
    // 边缘与角落吸附参数（平移/单边拉伸对齐画布角落、边缘与中线）
    private const double EdgeSnapInScreenPx = 56.0;
    private const double EdgeSnapOutScreenPx = 88.0;
    private const double EdgeSnapReleaseHoldScreenPx = 100.0; // 松开 Shift 后继续保持吸附的距离

    // 尺寸拉伸吸附参数（缩放至 2 的整数幂像素尺寸）
    private const double ScaleSnapInScreenPx = 48.0;
    private const double ScaleSnapOutScreenPx = 76.0;
    // 吸附半径按相邻幂的实际间距封顶；保持区与下一目标的进入区之间保留线性拖动区间。
    private const double ScaleSnapMaxInGapRatio = 0.34;
    private const double ScaleSnapMaxOutGapRatio = 0.46;

    private bool wasShiftPressed;
    private bool hasDragMoved;
    // 真实的原始鼠标手势按下点（在第1次EditChanged时由Paint.NET原生赋值中读取，绝不漂移）
    private PaintDotNet.Rendering.Point2Double? originalClickPt;

    // 平移 / 边缘吸附专用状态
    private bool isSnapLocked;
    private double lockedTargetCanvasX = double.NaN;
    private double lockedTargetCanvasY = double.NaN;
    private bool lastSnappedL;
    private bool lastSnappedR;
    private bool lastSnappedT;
    private bool lastSnappedB;
    private bool lastSnappedCenterX;
    private bool lastSnappedCenterY;
    private string lastBadgeText = "";
    private PointF lastBadgeDocPt = PointF.Empty;
    private double lastTargetCanvasX = double.NaN;
    private double lastTargetCanvasY = double.NaN;
    private long lastEdgeSnapTimestamp;

    // 缩放 / 拉伸吸附专用状态
    private bool isScaleSnapLocked;
    private double lockedTargetScale = double.NaN;
    private double lockedTargetScaleX = double.NaN;
    private double lockedTargetScaleY = double.NaN;
    private double lastSnappedScaleFactor = double.NaN;
    private double lastSnappedScaleX = double.NaN;
    private double lastSnappedScaleY = double.NaN;
    private bool lastScaleSnappedL;
    private bool lastScaleSnappedR;
    private bool lastScaleSnappedT;
    private bool lastScaleSnappedB;
    private long lastScaleSnapTimestamp;
    private double lastObservedScaleX = double.NaN;
    private double lastObservedScaleY = double.NaN;
    private double lockedScaleSize = double.NaN;
    private bool lockedScaleOnHeight;

    public static MoveToolGuideService Instance => instance ??= new MoveToolGuideService();

    private MoveToolGuideService()
    {
    }

    public void Attach(object documentWorkspace)
    {
        if (ReferenceEquals(this.workspace, documentWorkspace))
        {
            this.SyncTool();
            return;
        }

        this.Detach();
        this.workspace = documentWorkspace;

        try
        {
            this.document = PdnModel.TryGetDocument(documentWorkspace);
            this.canvasControl = (ReadProperty(documentWorkspace, "CanvasControl") ?? documentWorkspace) as Control;

            Application.AddMessageFilter(this);
            System.Windows.Interop.ComponentDispatcher.ThreadFilterMessage += this.OnThreadFilterMessage;



            this.SyncTool();
            Boot.Log("[MoveToolGuide] 移动工具辅助线与吸附服务已挂载 (纯原生事件驱动，零时钟打架)");
        }
        catch (Exception ex)
        {
            Boot.LogDiag("movetool-attach", "挂载失败: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    public bool IsDragging => this.isDragging;
    public bool IsSnapLocked => this.isSnapLocked;
    public bool IsScaleSnapLocked => this.isScaleSnapLocked;
    public bool HasOverlayForm => this.overlayForm != null;
    public bool OverlayVisible => this.overlayForm != null && this.overlayForm.IsOverlayVisible;
    internal double LastTargetCanvasX => !double.IsNaN(this.lockedTargetCanvasX) ? this.lockedTargetCanvasX : this.lastTargetCanvasX;
    internal double LastTargetCanvasY => !double.IsNaN(this.lockedTargetCanvasY) ? this.lockedTargetCanvasY : this.lastTargetCanvasY;
    internal bool LastSnappedL => this.lastSnappedL;
    internal bool LastSnappedR => this.lastSnappedR;
    internal bool LastSnappedT => this.lastSnappedT;
    internal bool LastSnappedB => this.lastSnappedB;

    internal PaintDotNet.Rendering.IRenderer<ColorBgra>? GetLiveLayerRenderer(Layer layer)
    {
        // 油漆桶、画笔及其他事务工具也可能有尚未提交的图层临时渲染结果。
        object? activeTool = ReadProperty(this.workspace, "Tool") ?? this.currentHookedTool;
        object? handler = ReadField(activeTool, "bitmapLayerTxHandler") ?? ReadField(activeTool, "txHandler");
        object? overlay = ReadField(handler, "layerOverlay");
        if (overlay == null || !ReferenceEquals(ReadProperty(overlay, "Layer"), layer)) return null;
        return ReadProperty(overlay, "Source") as PaintDotNet.Rendering.IRenderer<ColorBgra>;
    }

    public void ForceResync()
    {
        this.UnhookCurrentTool();
        this.SyncTool();
    }

    public void SyncTool()
    {
        if (this.workspace is null)
        {
            return;
        }

        try
        {
            this.document = PdnModel.TryGetDocument(this.workspace);

            if (this.canvasControl is null && this.workspace is not null)
            {
                this.canvasControl = (ReadProperty(this.workspace, "CanvasControl") ?? this.workspace) as Control;
            }

            object? activeTool = ReadProperty(this.workspace, "Tool") ?? ReadField(this.workspace, "activeTool");
            if (activeTool is null)
            {
                this.UnhookCurrentTool();
                return;
            }

            string toolTypeName = activeTool.GetType().FullName ?? "";
            bool isMoveTool = toolTypeName.Contains("Move", StringComparison.OrdinalIgnoreCase);

            if (!isMoveTool)
            {
                this.UnhookCurrentTool();
                return;
            }

            // 获取 MoveTool 当前的 UI 和 TransformControl
            object? ui = ReadProperty(activeTool, "UI");
            object? tc = ui != null ? (ReadField(ui, "transformControl") ?? ReadProperty(ui, "TransformControl")) : null;

            // 关键修复：只有当 tool 和 tc 都匹配且 tc 不为 null 时，才说明已经处于正常挂载状态！
            // 如果 tc 为 null（UI 尚未创建），或者 tc 实例变了（例如按 Ctrl+Z 撤销后重新创建了 TransformControl），
            // 都必须重新挂载！
            if (ReferenceEquals(this.currentHookedTool, activeTool) &&
                ReferenceEquals(this.currentHookedTransformControl, tc) &&
                tc != null)
            {
                return;
            }

            this.HookTool(activeTool, tc);
        }
        catch (Exception ex)
        {
            Boot.LogDiag("movetool-sync", "SyncTool 异常: " + ex.Message);
        }
    }

    private void HookTool(object tool, object? tc)
    {
        this.UnhookCurrentTool();
        this.currentHookedTool = tool;

        if (tc is null)
        {
            return;
        }

        try
        {
            this.currentHookedTransformControl = tc;
            Type tcType = tc.GetType();

            EventInfo? editChangedEvt = tcType.GetEvent("EditChanged");
            if (editChangedEvt != null && editChangedEvt.EventHandlerType != null)
            {
                MethodInfo mi = typeof(MoveToolGuideService).GetMethod(
                    nameof(this.OnTransformEditChanged),
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                this.editChangedHandler = Delegate.CreateDelegate(editChangedEvt.EventHandlerType, this, mi);
                editChangedEvt.AddEventHandler(tc, this.editChangedHandler);

                // 原生 UI 在构造时先注册了转发事件，必须把吸附校正放在它之前。
                // 否则原始鼠标变换和吸附变换都会被送给异步像素渲染器。
                object? ui = ReadProperty(tool, "UI");
                MethodInfo? forward = ui?.GetType().GetMethod("OnTransformControlEditChanged",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (ui != null && forward != null)
                {
                    Delegate nativeHandler = Delegate.CreateDelegate(editChangedEvt.EventHandlerType, ui, forward);
                    editChangedEvt.RemoveEventHandler(tc, nativeHandler);
                    editChangedEvt.AddEventHandler(tc, nativeHandler);
                }
            }

            EventInfo? editingBeginEvt = tcType.GetEvent("EditingBegin");
            if (editingBeginEvt != null && editingBeginEvt.EventHandlerType != null)
            {
                MethodInfo mi = typeof(MoveToolGuideService).GetMethod(
                    nameof(this.OnTransformEditingBegin),
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                this.editingBeginHandler = Delegate.CreateDelegate(editingBeginEvt.EventHandlerType, this, mi);
                editingBeginEvt.AddEventHandler(tc, this.editingBeginHandler);
            }

            EventInfo? editingFinishedEvt = tcType.GetEvent("EditingFinished");
            if (editingFinishedEvt != null && editingFinishedEvt.EventHandlerType != null)
            {
                MethodInfo mi = typeof(MoveToolGuideService).GetMethod(
                    nameof(this.OnTransformEditingFinished),
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                this.editingFinishedHandler = Delegate.CreateDelegate(editingFinishedEvt.EventHandlerType, this, mi);
                editingFinishedEvt.AddEventHandler(tc, this.editingFinishedHandler);
            }

            EventInfo? editingCancelledEvt = tcType.GetEvent("EditingCancelled");
            if (editingCancelledEvt != null && editingCancelledEvt.EventHandlerType != null)
            {
                MethodInfo mi = typeof(MoveToolGuideService).GetMethod(
                    nameof(this.OnTransformEditingCancelled),
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                this.editingCancelledHandler = Delegate.CreateDelegate(editingCancelledEvt.EventHandlerType, this, mi);
                editingCancelledEvt.AddEventHandler(tc, this.editingCancelledHandler);
            }

            Boot.Log("[MoveToolGuide] 成功挂载 TransformControl 变换监听");
        }
        catch (Exception ex)
        {
            Boot.LogDiag("movetool-hook", "HookTool 异常: " + ex.Message);
        }
    }

    private void UnhookCurrentTool()
    {
        if (this.currentHookedTransformControl is not null)
        {
            try
            {
                Type tcType = this.currentHookedTransformControl.GetType();
                if (this.editChangedHandler is not null)
                {
                    tcType.GetEvent("EditChanged")?.RemoveEventHandler(this.currentHookedTransformControl, this.editChangedHandler);
                }
                if (this.editingBeginHandler is not null)
                {
                    tcType.GetEvent("EditingBegin")?.RemoveEventHandler(this.currentHookedTransformControl, this.editingBeginHandler);
                }
                if (this.editingFinishedHandler is not null)
                {
                    tcType.GetEvent("EditingFinished")?.RemoveEventHandler(this.currentHookedTransformControl, this.editingFinishedHandler);
                }
                if (this.editingCancelledHandler is not null)
                {
                    tcType.GetEvent("EditingCancelled")?.RemoveEventHandler(this.currentHookedTransformControl, this.editingCancelledHandler);
                }
            }
            catch {}
        }

        this.currentHookedTransformControl = null;
        this.currentHookedTool = null;
        this.editChangedHandler = null;
        this.editingBeginHandler = null;
        this.editingFinishedHandler = null;
        this.editingCancelledHandler = null;
        this.isDragging = false; this.hasDragMoved = false;
        this.isPointerReleasePending = false;
        this.isSnapLocked = false;
                        this.lockedTargetCanvasX = double.NaN;
        this.lockedTargetCanvasY = double.NaN;
        this.isScaleSnapLocked = false;
                this.lockedTargetScale = double.NaN;
        this.lastSnappedScaleFactor = double.NaN;
        this.lastSnappedScaleX = double.NaN;
        this.lastSnappedScaleY = double.NaN;
        this.lastSnappedL = false;
        this.lastSnappedR = false;
        this.lastSnappedT = false;
        this.lastSnappedB = false;
        this.lastTargetCanvasX = double.NaN;
        this.lastTargetCanvasY = double.NaN;
        this.lastEdgeSnapTimestamp = 0;
        this.lastScaleSnapTimestamp = 0;
        this.lastObservedScaleX = double.NaN;
        this.lastObservedScaleY = double.NaN;
        this.wasShiftPressed = false;
        this.overlayForm?.HideOverlay();
    }

    private int GetCurrentEditingMode()
    {
        if (this.currentHookedTransformControl != null)
        {
            object? modeObj = ReadProperty(this.currentHookedTransformControl, "EditingMode");
            if (modeObj != null)
            {
                return Convert.ToInt32(modeObj);
            }
        }
        return -1;
    }

    private void OnTransformEditingBegin(object sender, object e)
    {
        this.isDragging = true;
        this.isPointerReleasePending = false;
        this.hasDragMoved = false;
        this.originalClickPt = null;

        this.isSnapLocked = false;
                        this.lockedTargetCanvasX = double.NaN;
        this.lockedTargetCanvasY = double.NaN;
        this.isScaleSnapLocked = false;
                this.lockedTargetScale = double.NaN;
        this.lockedTargetScaleX = double.NaN;
        this.lockedTargetScaleY = double.NaN;
        this.lastSnappedScaleFactor = double.NaN;
        this.lastSnappedScaleX = double.NaN;
        this.lastSnappedScaleY = double.NaN;
        this.lastScaleSnappedL = false;
        this.lastScaleSnappedR = false;
        this.lastScaleSnappedT = false;
        this.lastScaleSnappedB = false;
        this.lastSnappedL = false;
        this.lastSnappedR = false;
        this.lastSnappedT = false;
        this.lastSnappedB = false;
        this.lastSnappedCenterX = false;
        this.lastSnappedCenterY = false;
        this.lastTargetCanvasX = double.NaN;
        this.lastTargetCanvasY = double.NaN;
        this.lastEdgeSnapTimestamp = 0;
        this.lastScaleSnapTimestamp = 0;
        this.lastObservedScaleX = 1.0;
        this.lastObservedScaleY = 1.0;
        this.wasShiftPressed = (GetAsyncKeyState(0x10) & 0x8000) != 0 || ((Control.ModifierKeys & Keys.Shift) == Keys.Shift);
    }

    private void OnTransformEditingFinished(object sender, object e)
    {
        try
        {
            // 提交时微动校正：处于吸附锁定或最近吸附状态下，确保 DeltaTransform 100% 对齐吸附目标
            bool isSnappedOrRecent = this.isSnapLocked ||
                                     ((this.lastSnappedL || this.lastSnappedR || this.lastSnappedCenterX ||
                                       this.lastSnappedT || this.lastSnappedB || this.lastSnappedCenterY) &&
                                      (Environment.TickCount64 - this.lastEdgeSnapTimestamp < 1500));

            if (isSnappedOrRecent)
            {
                this.EnforceSnappingOnCommit();
            }

            // 提交完成：通知图层面板即时刷新缩略图与通道视图
            LayerChannelHost.Current?.Panel?.NotifyLayerContentChanged();
        }
        catch (Exception ex)
        {
            Boot.LogDiag("movetool-finished", "Commit完成通知异常: " + ex.Message);
        }
        finally
        {
            this.isDragging = false;
            this.isPointerReleasePending = false;
            this.hasDragMoved = false;
            this.isSnapLocked = false;
            this.isScaleSnapLocked = false;
            this.lockedScaleSize = double.NaN;
            this.lockedTargetScale = double.NaN;
            this.lockedTargetScaleX = double.NaN;
            this.lockedTargetScaleY = double.NaN;
            this.lastSnappedScaleFactor = double.NaN;
            this.lastSnappedScaleX = double.NaN;
            this.lastSnappedScaleY = double.NaN;
            this.lastScaleSnappedL = false;
            this.lastScaleSnappedR = false;
            this.lastScaleSnappedT = false;
            this.lastScaleSnappedB = false;
            this.lockedTargetCanvasX = double.NaN;
            this.lockedTargetCanvasY = double.NaN;
            this.overlayForm?.HideOverlay();
            this.lastSnappedL = false;
            this.lastSnappedR = false;
            this.lastSnappedT = false;
            this.lastSnappedB = false;
            this.lastSnappedCenterX = false;
            this.lastSnappedCenterY = false;
            this.lastTargetCanvasX = double.NaN;
            this.lastTargetCanvasY = double.NaN;
            this.lastEdgeSnapTimestamp = 0;
            this.lastScaleSnapTimestamp = 0;
            this.lastObservedScaleX = double.NaN;
            this.lastObservedScaleY = double.NaN;
            this.wasShiftPressed = false;
            this.originalClickPt = null;
        }
    }

    private void EnforceSnappingOnCommit()
    {
        bool isSnappedOrRecent = this.isSnapLocked ||
                                 ((this.lastSnappedL || this.lastSnappedR || this.lastSnappedCenterX ||
                                   this.lastSnappedT || this.lastSnappedB || this.lastSnappedCenterY) &&
                                  (Environment.TickCount64 - this.lastEdgeSnapTimestamp < 1500));

        if (this.currentHookedTransformControl is null || !isSnappedOrRecent)
        {
            return;
        }

        object tc = this.currentHookedTransformControl;
        double targetCanvasX = !double.IsNaN(this.lockedTargetCanvasX) ? this.lockedTargetCanvasX : this.lastTargetCanvasX;
        double targetCanvasY = !double.IsNaN(this.lockedTargetCanvasY) ? this.lockedTargetCanvasY : this.lastTargetCanvasY;
        if (double.IsNaN(targetCanvasX) || double.IsNaN(targetCanvasY))
        {
            return;
        }

        object? baseBoundsObj = ReadProperty(tc, "BaseBounds");
        if (baseBoundsObj is null) return;

        double bbX = Convert.ToDouble(ReadProperty(baseBoundsObj, "X") ?? 0);
        double bbY = Convert.ToDouble(ReadProperty(baseBoundsObj, "Y") ?? 0);
        double bbW = Convert.ToDouble(ReadProperty(baseBoundsObj, "Width") ?? 0);
        double bbH = Convert.ToDouble(ReadProperty(baseBoundsObj, "Height") ?? 0);
        if (bbW <= 0 || bbH <= 0) return;

        object? deltaTransform = ReadProperty(tc, "DeltaTransform");
        if (deltaTransform is null || !TryGetTranslation(deltaTransform, out double dTx, out double dTy, out _))
        {
            return;
        }

        object? baseTransform = ReadProperty(tc, "BaseTransform");
        GetActualRenderedBounds(bbX, bbY, bbW, bbH, baseTransform, deltaTransform,
            out double renderedX, out double renderedY, out _, out _);
        double targetDeltaX = dTx + (this.lastSnappedL || this.lastSnappedR || this.lastSnappedCenterX ? targetCanvasX - renderedX : 0.0);
        double targetDeltaY = dTy + (this.lastSnappedT || this.lastSnappedB || this.lastSnappedCenterY ? targetCanvasY - renderedY : 0.0);
        if (Math.Abs(targetDeltaX - dTx) > 0.0001 || Math.Abs(targetDeltaY - dTy) > 0.0001)
        {
            MethodInfo? mSetDelta = tc.GetType().GetMethod(
                "SetDeltaTransform",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                new[] { typeof(PaintDotNet.Rendering.Matrix3x2Double) });

            if (mSetDelta != null)
            {
                PaintDotNet.Rendering.Matrix3x2Double clampedMatrix;
                PropertyInfo? pVal = deltaTransform.GetType().GetProperty("Value");
                if (pVal != null && pVal.GetValue(deltaTransform) is PaintDotNet.Rendering.Matrix3x2Double curM)
                {
                    clampedMatrix = new PaintDotNet.Rendering.Matrix3x2Double(
                        curM.M11, curM.M12,
                        curM.M21, curM.M22,
                        targetDeltaX, targetDeltaY);
                }
                else
                {
                    clampedMatrix = PaintDotNet.Rendering.Matrix3x2Double.Translation(targetDeltaX, targetDeltaY);
                }

                mSetDelta.Invoke(tc, new object[] { clampedMatrix });

                object? ui = ReadProperty(this.currentHookedTool, "UI");
                if (ui != null)
                {
                    try
                    {
                        object? newDt = ReadProperty(tc, "DeltaTransform");
                        PropertyInfo? pUiDelta = ui.GetType().GetProperty("DeltaTransform");
                        pUiDelta?.SetValue(ui, newDt);
                    }
                    catch {}
                }

                try
                {
                    MethodInfo? mUpdate = tc.GetType().GetMethod("UpdateFinalTransform", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    mUpdate?.Invoke(tc, null);
                    MethodInfo? mInval = tc.GetType().GetMethod("InvalidateVisual", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    mInval?.Invoke(tc, null);
                }
                catch {}

                Boot.Log($"[MoveToolGuide] 拖拽提交微动校正成功: 目标画布位置=({targetCanvasX}, {targetCanvasY})");
            }
        }
    }

    private void OnTransformEditingCancelled(object sender, object e)
    {
        try
        {
            LayerChannelHost.Current?.Panel?.NotifyLayerContentChanged();
        }
        catch {}

        this.isDragging = false;
        this.isPointerReleasePending = false;
        this.hasDragMoved = false;
        this.isSnapLocked = false;
        this.lockedTargetCanvasX = double.NaN;
        this.lockedTargetCanvasY = double.NaN;
        this.isScaleSnapLocked = false;
        this.lockedScaleSize = double.NaN;
        this.lockedTargetScale = double.NaN;
        this.lockedTargetScaleX = double.NaN;
        this.lockedTargetScaleY = double.NaN;
        this.lastSnappedScaleFactor = double.NaN;
        this.lastSnappedScaleX = double.NaN;
        this.lastSnappedScaleY = double.NaN;
        this.lastScaleSnappedL = false;
        this.lastScaleSnappedR = false;
        this.lastScaleSnappedT = false;
        this.lastScaleSnappedB = false;
        this.lastSnappedL = false;
        this.lastSnappedR = false;
        this.lastSnappedT = false;
        this.lastSnappedB = false;
        this.lastSnappedCenterX = false;
        this.lastSnappedCenterY = false;
        this.lastTargetCanvasX = double.NaN;
        this.lastTargetCanvasY = double.NaN;
        this.lastEdgeSnapTimestamp = 0;
        this.lastScaleSnapTimestamp = 0;
        this.lastObservedScaleX = double.NaN;
        this.lastObservedScaleY = double.NaN;
        this.wasShiftPressed = false;
        this.originalClickPt = null;
        this.overlayForm?.HideOverlay();
    }

    private void OnTransformEditChanged(object sender, object e)
    {
        if (this.isAdjusting || this.workspace is null)
        {
            return;
        }

        this.ProcessSnappingAndGuides();
        LayerChannelHost.Current?.Panel?.NotifyLayerContentChangedThrottled();
    }

    private struct Matrix2D
    {
        public double M11, M12, M21, M22, OffsetX, OffsetY;

        public static Matrix2D Identity => new Matrix2D { M11 = 1, M22 = 1 };

        public static Matrix2D Multiply(Matrix2D a, Matrix2D b)
        {
            return new Matrix2D
            {
                M11 = a.M11 * b.M11 + a.M12 * b.M21,
                M12 = a.M11 * b.M12 + a.M12 * b.M22,
                M21 = a.M21 * b.M11 + a.M22 * b.M21,
                M22 = a.M21 * b.M12 + a.M22 * b.M22,
                OffsetX = a.OffsetX * b.M11 + a.OffsetY * b.M21 + b.OffsetX,
                OffsetY = a.OffsetX * b.M12 + a.OffsetY * b.M22 + b.OffsetY
            };
        }

        public PointF Transform(double x, double y)
        {
            return new PointF(
                (float)(x * M11 + y * M21 + OffsetX),
                (float)(x * M12 + y * M22 + OffsetY)
            );
        }
    }

    private static Matrix2D ExtractMatrix(object? transform)
    {
        if (transform == null) return Matrix2D.Identity;
        Type t = transform.GetType();

        var px = t.GetProperty("X");
        var py = t.GetProperty("Y");
        if (px != null && py != null)
        {
            double tx = Convert.ToDouble(px.GetValue(transform) ?? 0);
            double ty = Convert.ToDouble(py.GetValue(transform) ?? 0);
            return new Matrix2D { M11 = 1, M22 = 1, OffsetX = tx, OffsetY = ty };
        }

        object? matrixObj = t.GetProperty("Value")?.GetValue(transform) ?? t.GetProperty("Matrix")?.GetValue(transform) ?? transform;
        if (matrixObj != null)
        {
            Type mt = matrixObj.GetType();
            double m11 = Convert.ToDouble(mt.GetProperty("M11")?.GetValue(matrixObj) ?? mt.GetField("M11")?.GetValue(matrixObj) ?? 1);
            double m12 = Convert.ToDouble(mt.GetProperty("M12")?.GetValue(matrixObj) ?? mt.GetField("M12")?.GetValue(matrixObj) ?? 0);
            double m21 = Convert.ToDouble(mt.GetProperty("M21")?.GetValue(matrixObj) ?? mt.GetField("M21")?.GetValue(matrixObj) ?? 0);
            double m22 = Convert.ToDouble(mt.GetProperty("M22")?.GetValue(matrixObj) ?? mt.GetField("M22")?.GetValue(matrixObj) ?? 1);
            double ox = Convert.ToDouble(mt.GetProperty("OffsetX")?.GetValue(matrixObj) ?? mt.GetField("OffsetX")?.GetValue(matrixObj) ?? mt.GetProperty("DX")?.GetValue(matrixObj) ?? 0);
            double oy = Convert.ToDouble(mt.GetProperty("OffsetY")?.GetValue(matrixObj) ?? mt.GetField("OffsetY")?.GetValue(matrixObj) ?? mt.GetProperty("DY")?.GetValue(matrixObj) ?? 0);
            return new Matrix2D { M11 = m11, M12 = m12, M21 = m21, M22 = m22, OffsetX = ox, OffsetY = oy };
        }

        return Matrix2D.Identity;
    }

    private static void GetActualRenderedBounds(
        double bbX, double bbY, double bbW, double bbH,
        object? baseTransform, object? deltaTransform,
        out double preCanvasX, out double preCanvasY,
        out double actualW, out double actualH)
    {
        Matrix2D matBase = ExtractMatrix(baseTransform);
        Matrix2D matDelta = ExtractMatrix(deltaTransform);
        Matrix2D preMatrix = Matrix2D.Multiply(matBase, matDelta);

        PointF p1 = preMatrix.Transform(bbX, bbY);
        PointF p2 = preMatrix.Transform(bbX + bbW, bbY);
        PointF p3 = preMatrix.Transform(bbX, bbY + bbH);
        PointF p4 = preMatrix.Transform(bbX + bbW, bbY + bbH);

        double minX = Math.Min(Math.Min(p1.X, p2.X), Math.Min(p3.X, p4.X));
        double maxX = Math.Max(Math.Max(p1.X, p2.X), Math.Max(p3.X, p4.X));
        double minY = Math.Min(Math.Min(p1.Y, p2.Y), Math.Min(p3.Y, p4.Y));
        double maxY = Math.Max(Math.Max(p1.Y, p2.Y), Math.Max(p3.Y, p4.Y));

        actualW = Math.Max(1.0, maxX - minX);
        actualH = Math.Max(1.0, maxY - minY);
        preCanvasX = minX;
        preCanvasY = minY;
    }
    private static bool TryGetTranslation(object? transform, out double tx, out double ty, out Action<double, double>? setTxTy)
    {
        tx = 0;
        ty = 0;
        setTxTy = null;
        if (transform is null) return false;

        Type type = transform.GetType();

        // 1. Direct TranslateTransform (X, Y)
        PropertyInfo? propX = type.GetProperty("X");
        PropertyInfo? propY = type.GetProperty("Y");
        if (propX != null && propY != null)
        {
            try
            {
                tx = Convert.ToDouble(propX.GetValue(transform) ?? 0);
                ty = Convert.ToDouble(propY.GetValue(transform) ?? 0);
                setTxTy = (newX, newY) =>
                {
                    propX.SetValue(transform, newX);
                    propY.SetValue(transform, newY);
                };
                return true;
            }
            catch {}
        }

        // Helper to extract translation from matrix struct or class
        static bool TryExtractFromMatrix(object? matrixObj, out double mtx, out double mty)
        {
            mtx = 0;
            mty = 0;
            if (matrixObj == null) return false;
            Type mType = matrixObj.GetType();

            // Properties: OffsetX, DY, M31
            PropertyInfo? pX = mType.GetProperty("OffsetX") ?? mType.GetProperty("DX") ?? mType.GetProperty("M31");
            PropertyInfo? pY = mType.GetProperty("OffsetY") ?? mType.GetProperty("DY") ?? mType.GetProperty("M32");
            if (pX != null && pY != null)
            {
                try
                {
                    mtx = Convert.ToDouble(pX.GetValue(matrixObj) ?? 0);
                    mty = Convert.ToDouble(pY.GetValue(matrixObj) ?? 0);
                    return true;
                }
                catch {}
            }

            // Fields: OffsetX, DY, M31, m31
            FieldInfo? fX = mType.GetField("OffsetX") ?? mType.GetField("DX") ?? mType.GetField("M31") ?? mType.GetField("m31");
            FieldInfo? fY = mType.GetField("OffsetY") ?? mType.GetField("DY") ?? mType.GetField("M32") ?? mType.GetField("m32");
            if (fX != null && fY != null)
            {
                try
                {
                    mtx = Convert.ToDouble(fX.GetValue(matrixObj) ?? 0);
                    mty = Convert.ToDouble(fY.GetValue(matrixObj) ?? 0);
                    return true;
                }
                catch {}
            }

            // Translation property
            PropertyInfo? pTrans = mType.GetProperty("Translation");
            if (pTrans != null)
            {
                object? transObj = pTrans.GetValue(matrixObj);
                if (transObj != null)
                {
                    Type tType = transObj.GetType();
                    PropertyInfo? ptX = tType.GetProperty("X");
                    PropertyInfo? ptY = tType.GetProperty("Y");
                    if (ptX != null && ptY != null)
                    {
                        mtx = Convert.ToDouble(ptX.GetValue(transObj) ?? 0);
                        mty = Convert.ToDouble(ptY.GetValue(transObj) ?? 0);
                        return true;
                    }
                }
            }

            return false;
        }

        // 2. Value property returning Matrix (System.Windows.Media.Matrix or PaintDotNet.Rendering.Matrix3x2Double)
        PropertyInfo? propVal = type.GetProperty("Value");
        if (propVal != null)
        {
            try
            {
                object? val = propVal.GetValue(transform);
                if (TryExtractFromMatrix(val, out tx, out ty))
                {
                    return true;
                }
            }
            catch {}
        }

        // 3. Matrix property (MatrixTransform)
        PropertyInfo? propMatrix = type.GetProperty("Matrix");
        if (propMatrix != null)
        {
            try
            {
                object? matrix = propMatrix.GetValue(transform);
                if (TryExtractFromMatrix(matrix, out tx, out ty))
                {
                    Type mType = matrix!.GetType();
                    MethodInfo? mTranslation = mType.GetMethod("Translation", new[] { typeof(double), typeof(double) });
                    if (mTranslation != null)
                    {
                        setTxTy = (newX, newY) =>
                        {
                            object newMat = mTranslation.Invoke(null, new object[] { newX, newY })!;
                            propMatrix.SetValue(transform, newMat);
                        };
                    }
                    return true;
                }
            }
            catch {}
        }

        return false;
    }

    public void ProcessSnappingAndGuides(bool forceActive = false)
    {
        if (this.isAdjusting) return;
        if (this.isPointerReleasePending)
        {
            // WM_LBUTTONUP 到达时，原生控件尚未冻结并提交变换。
            // 此间的最后一次鼠标/修饰键更新也必须使用已显示的锁定结果。
            if (this.isScaleSnapLocked) this.EnforceLastScaleSnapBeforeCommit();
            else if (this.isSnapLocked) this.EnforceLastSnapBeforeCommit();
            return;
        }

        if (this.workspace is null || this.currentHookedTransformControl is null)
        {
            return;
        }

        bool isShift = forceActive || this.wasShiftPressed || (GetAsyncKeyState(0x10) & 0x8000) != 0 || ((Control.ModifierKeys & Keys.Shift) == Keys.Shift) || ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) == System.Windows.Input.ModifierKeys.Shift);
        bool isLButton = forceActive || this.isDragging || (GetAsyncKeyState(0x01) & 0x8000) != 0 || ((Control.MouseButtons & MouseButtons.Left) == MouseButtons.Left);

        if (!isLButton && !forceActive && !this.isSnapLocked)
        {
            this.isSnapLocked = false;
            this.lockedTargetCanvasX = double.NaN;
            this.lockedTargetCanvasY = double.NaN;
            this.overlayForm?.HideOverlay();
            return;
        }

        try
        {
            object tc = this.currentHookedTransformControl;

            object? baseBoundsObj = ReadProperty(tc, "BaseBounds");
            if (baseBoundsObj is null) return;

            double bbX = Convert.ToDouble(ReadProperty(baseBoundsObj, "X") ?? 0);
            double bbY = Convert.ToDouble(ReadProperty(baseBoundsObj, "Y") ?? 0);
            double bbW = Convert.ToDouble(ReadProperty(baseBoundsObj, "Width") ?? 0);
            double bbH = Convert.ToDouble(ReadProperty(baseBoundsObj, "Height") ?? 0);

            if (bbW <= 0 || bbH <= 0) return;

            object? ui = ReadProperty(this.currentHookedTool, "UI");
            object? token = ReadProperty(tc, "ActiveEditToken")
                            ?? ReadField(tc, "activeEditToken")
                            ?? ReadField(tc, "pointerEditToken")
                            ?? (ui != null ? (ReadProperty(ui, "ActiveEditToken") ?? ReadField(ui, "activeEditToken")) : null);

            object? editTransform = (token != null ? (ReadProperty(token, "EditTransform") ?? ReadField(token, "editTransform")) : null)
                                   ?? ReadProperty(tc, "EditTransform")
                                   ?? (ui != null ? ReadProperty(ui, "EditTransform") : null);
            if (editTransform is null) return;

            string editType = editTransform.GetType().Name;
            object? editingModeObj = ReadProperty(tc, "EditingMode");
            int editingMode = editingModeObj != null ? Convert.ToInt32(editingModeObj) : -1;

            if (editingMode == 2 || editType.Contains("Scale"))
            {
                this.ProcessScaleSnappingAndGuides(tc, ui, token, editTransform, bbW, bbH, forceActive, isShift);
                return;
            }

            if (!TryGetTranslation(editTransform, out double editTx, out double editTy, out var setTxTy) || setTxTy is null)
            {
                return;
            }

            // 防点击即吸附：未发生实际拖拽位移时不触发吸附
            if (!this.hasDragMoved && !forceActive)
            {
                if (Math.Abs(editTx) >= 3.0 || Math.Abs(editTy) >= 3.0)
                {
                    this.hasDragMoved = true;
                }
                else
                {
                    this.overlayForm?.HideOverlay();
                    return;
                }
            }

            object? deltaTransform = (token != null ? ReadProperty(token, "OldDeltaTransform") : null)
                                    ?? ReadProperty(tc, "DeltaTransform");
            object? baseTransform = (token != null ? ReadProperty(token, "BaseTransform") : null)
                                   ?? ReadProperty(tc, "BaseTransform");

            // 计算缩放/旋转变换后的真实渲染尺寸与画布原点
            GetActualRenderedBounds(bbX, bbY, bbW, bbH, baseTransform, deltaTransform,
                out double preCanvasX, out double preCanvasY,
                out double actualW, out double actualH);

            Document? doc = this.document ?? PdnModel.TryGetDocument(this.workspace);
            if (doc is null) return;

            double docW = doc.Width;
            double docH = doc.Height;

            double scale = 1.0;
            object? scaleFactorObj = ReadProperty(this.workspace, "ScaleFactor");
            if (scaleFactorObj is not null)
            {
                scale = Convert.ToDouble(ReadProperty(scaleFactorObj, "Ratio") ?? 1.0);
            }
            if (scale <= 0.001) scale = 1.0;

            // 边缘吸附参数（屏幕像素换算为文档像素，保持任意缩放手感一致）
            double threshIn = EdgeSnapInScreenPx / scale;
            double threshOut = EdgeSnapOutScreenPx / scale;
            double releaseHold = EdgeSnapReleaseHoldScreenPx / scale;

            // 图像当前真实渲染的包围盒文档坐标（基于绝对平滑真实的物理位移）
            FieldInfo? fLatest = tc.GetType().GetField("latestDragMovePt", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            FieldInfo? fBegin = tc.GetType().GetField("dragBeginPt", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            if (this.originalClickPt == null && fBegin != null)
            {
                if (fBegin.GetValue(tc) is PaintDotNet.Rendering.Point2Double initPt && double.IsFinite(initPt.X) && double.IsFinite(initPt.Y) && initPt.X < 1e8)
                {
                    this.originalClickPt = initPt;
                }
            }

            PaintDotNet.Rendering.Point2Double? curMousePtOpt = fLatest?.GetValue(tc) as PaintDotNet.Rendering.Point2Double?;

            double rawTx, rawTy;
            if (curMousePtOpt != null && this.originalClickPt != null &&
                double.IsFinite(curMousePtOpt.Value.X) && double.IsFinite(curMousePtOpt.Value.Y) && curMousePtOpt.Value.X < 1e8)
            {
                rawTx = curMousePtOpt.Value.X - this.originalClickPt.Value.X;
                rawTy = curMousePtOpt.Value.Y - this.originalClickPt.Value.Y;
            }
            else
            {
                rawTx = editTx;
                rawTy = editTy;
            }

            double curL = preCanvasX + rawTx;
            double curT = preCanvasY + rawTy;
            double curR = curL + actualW;
            double curB = curT + actualH;
            double curCenterX = curL + actualW / 2.0;
            double curCenterY = curT + actualH / 2.0;

            bool snapL = false;
            bool snapR = false;
            bool snapCenterX = false;
            bool snapT = false;
            bool snapB = false;
            bool snapCenterY = false;

            double targetCanvasX = curL;
            double targetCanvasY = curT;
            double targetEditTx = rawTx;
            double targetEditTy = rawTy;
            string badgeText = "";
            PointF badgeDocPt = PointF.Empty;

            if (isShift)
            {
                double[] targetsX = { 0.0, Math.Round((docW - actualW) / 2.0), Math.Round(docW - actualW) };
                double[] targetsY = { 0.0, Math.Round((docH - actualH) / 2.0), Math.Round(docH - actualH) };
                int lockedX = this.isSnapLocked ? (this.lastSnappedL ? 0 : this.lastSnappedCenterX ? 1 : this.lastSnappedR ? 2 : -1) : -1;
                int lockedY = this.isSnapLocked ? (this.lastSnappedT ? 0 : this.lastSnappedCenterY ? 1 : this.lastSnappedB ? 2 : -1) : -1;
                double cornerEnter = threshIn * 1.25;
                bool nearCornerX = Math.Min(Math.Abs(curL - targetsX[0]), Math.Abs(curL - targetsX[2])) <= cornerEnter;
                bool nearCornerY = Math.Min(Math.Abs(curT - targetsY[0]), Math.Abs(curT - targetsY[2])) <= cornerEnter;
                int selectedX = SelectAxisSnap(curL, targetsX, lockedX, nearCornerY ? cornerEnter : threshIn, threshOut);
                int selectedY = SelectAxisSnap(curT, targetsY, lockedY, nearCornerX ? cornerEnter : threshIn, threshOut);
                snapL = selectedX == 0;
                snapCenterX = selectedX == 1;
                snapR = selectedX == 2;
                snapT = selectedY == 0;
                snapCenterY = selectedY == 1;
                snapB = selectedY == 2;
                if (selectedX >= 0) targetCanvasX = targetsX[selectedX];
                if (selectedY >= 0) targetCanvasY = targetsY[selectedY];

                if (snapL || snapR || snapCenterX)
                {
                    targetEditTx = targetCanvasX - preCanvasX;
                }
                else
                {
                    targetEditTx = rawTx;
                }

                if (snapT || snapB || snapCenterY)
                {
                    targetEditTy = targetCanvasY - preCanvasY;
                }
                else
                {
                    targetEditTy = rawTy;
                }

                if (snapL || snapR || snapCenterX || snapT || snapB || snapCenterY)
                {
                    this.isSnapLocked = true;
                    this.lockedTargetCanvasX = targetCanvasX;
                    this.lockedTargetCanvasY = targetCanvasY;
                }
                else
                {
                    this.isSnapLocked = false;
                    this.lockedTargetCanvasX = double.NaN;
                    this.lockedTargetCanvasY = double.NaN;
                }
            }
            else if (this.isSnapLocked)
            {
                bool holdX = (this.lastSnappedL || this.lastSnappedR || this.lastSnappedCenterX) &&
                    Math.Abs(curL - this.lockedTargetCanvasX) <= releaseHold;
                bool holdY = (this.lastSnappedT || this.lastSnappedB || this.lastSnappedCenterY) &&
                    Math.Abs(curT - this.lockedTargetCanvasY) <= releaseHold;
                if (holdX)
                {
                    targetCanvasX = this.lockedTargetCanvasX;
                    targetEditTx = targetCanvasX - preCanvasX;
                    snapL = this.lastSnappedL;
                    snapR = this.lastSnappedR;
                    snapCenterX = this.lastSnappedCenterX;
                }
                if (holdY)
                {
                    targetCanvasY = this.lockedTargetCanvasY;
                    targetEditTy = targetCanvasY - preCanvasY;
                    snapT = this.lastSnappedT;
                    snapB = this.lastSnappedB;
                    snapCenterY = this.lastSnappedCenterY;
                }
                this.isSnapLocked = holdX || holdY;
                this.lockedTargetCanvasX = holdX ? targetCanvasX : double.NaN;
                this.lockedTargetCanvasY = holdY ? targetCanvasY : double.NaN;
            }

            bool isSnappedNow = this.isSnapLocked && (snapL || snapR || snapCenterX || snapT || snapB || snapCenterY);

            // 组合生成 4个角、4个边缘中心点、画布中心点的提示徽章与准星坐标
            if (isSnappedNow)
            {
                // 四个角
                if (snapL && snapT) { badgeText = "吸附: 左上角 (0, 0)"; badgeDocPt = new PointF(0, 0); }
                else if (snapR && snapT) { badgeText = "吸附: 右上角"; badgeDocPt = new PointF((float)docW, 0); }
                else if (snapL && snapB) { badgeText = "吸附: 左下角"; badgeDocPt = new PointF(0, (float)docH); }
                else if (snapR && snapB) { badgeText = "吸附: 右下角"; badgeDocPt = new PointF((float)docW, (float)docH); }
                // 四个边缘中心点
                else if (snapCenterX && snapT) { badgeText = "吸附: 顶边中心点"; badgeDocPt = new PointF((float)(docW / 2.0), 0); }
                else if (snapCenterX && snapB) { badgeText = "吸附: 底边中心点"; badgeDocPt = new PointF((float)(docW / 2.0), (float)docH); }
                else if (snapL && snapCenterY) { badgeText = "吸附: 左边中心点"; badgeDocPt = new PointF(0, (float)(docH / 2.0)); }
                else if (snapR && snapCenterY) { badgeText = "吸附: 右边中心点"; badgeDocPt = new PointF((float)docW, (float)(docH / 2.0)); }
                // 画布中心
                else if (snapCenterX && snapCenterY) { badgeText = "吸附: 画布中心点"; badgeDocPt = new PointF((float)(docW / 2.0), (float)(docH / 2.0)); }
                // 单轴对齐
                else if (snapL) { badgeText = "对齐: 左边缘"; badgeDocPt = new PointF(0, (float)curT); }
                else if (snapR) { badgeText = "对齐: 右边缘"; badgeDocPt = new PointF((float)docW, (float)curT); }
                else if (snapCenterX) { badgeText = "对齐: 水平居中"; badgeDocPt = new PointF((float)(docW / 2.0), (float)curT); }
                else if (snapT) { badgeText = "对齐: 顶边缘"; badgeDocPt = new PointF((float)curL, 0); }
                else if (snapB) { badgeText = "对齐: 底边缘"; badgeDocPt = new PointF((float)curL, (float)docH); }
                else if (snapCenterY) { badgeText = "对齐: 垂直居中"; badgeDocPt = new PointF((float)curL, (float)(docH / 2.0)); }
            }

            if (isSnappedNow)
            {
                this.lastSnappedL = snapL;
                this.lastSnappedR = snapR;
                this.lastSnappedCenterX = snapCenterX;
                this.lastSnappedT = snapT;
                this.lastSnappedB = snapB;
                this.lastSnappedCenterY = snapCenterY;
                this.lastBadgeText = badgeText;
                this.lastBadgeDocPt = badgeDocPt;
                this.lastTargetCanvasX = targetCanvasX;
                this.lastTargetCanvasY = targetCanvasY;
                this.lastEdgeSnapTimestamp = Environment.TickCount64;

                this.isAdjusting = true;
                try
                {
                    using var batch = BeginTransformChanges(tc);
                    setTxTy(targetEditTx, targetEditTy);
                    NotifyTransformChanged(tc, ui, token, editTransform);
                }
                finally
                {
                    this.isAdjusting = false;
                }
            }
            else
            {
                this.lastSnappedL = false;
                this.lastSnappedR = false;
                this.lastSnappedCenterX = false;
                this.lastSnappedT = false;
                this.lastSnappedB = false;
                this.lastSnappedCenterY = false;
                this.lastTargetCanvasX = double.NaN;
                this.lastTargetCanvasY = double.NaN;

                if (Math.Abs(editTx - rawTx) > 0.0001 || Math.Abs(editTy - rawTy) > 0.0001)
                {
                    this.isAdjusting = true;
                    try
                    {
                        using var batch = BeginTransformChanges(tc);
                        setTxTy(rawTx, rawTy);
                        NotifyTransformChanged(tc, ui, token, editTransform);
                    }
                    finally
                    {
                        this.isAdjusting = false;
                    }
                }
            }

            double finalRenderL = targetCanvasX;
            double finalRenderT = targetCanvasY;

            if (this.canvasControl is null && this.workspace is not null)
            {
                this.canvasControl = (ReadProperty(this.workspace, "CanvasControl") ?? this.workspace) as Control;
            }

            if (this.canvasControl is not null)
            {
                if (this.overlayForm is null)
                {
                    this.overlayForm = new GuideOverlayForm(this.canvasControl);
                }

                object? visDocRectObj = ReadProperty(this.workspace, "VisibleDocumentRect");
                object? visScreenRectObj = ReadProperty(this.workspace, "VisibleDocumentScreenRect");

                double vdX = 0, vdY = 0, vdW = docW, vdH = docH;
                if (visDocRectObj != null)
                {
                    vdX = Convert.ToDouble(ReadProperty(visDocRectObj, "X") ?? 0);
                    vdY = Convert.ToDouble(ReadProperty(visDocRectObj, "Y") ?? 0);
                    vdW = Convert.ToDouble(ReadProperty(visDocRectObj, "Width") ?? docW);
                    vdH = Convert.ToDouble(ReadProperty(visDocRectObj, "Height") ?? docH);
                }
                if (vdW <= 0.001) vdW = docW;
                if (vdH <= 0.001) vdH = docH;

                Point screenOrigin = this.canvasControl.PointToScreen(Point.Empty);
                double vsX = screenOrigin.X;
                double vsY = screenOrigin.Y;
                double vsW = Math.Max(1, this.canvasControl.ClientSize.Width);
                double vsH = Math.Max(1, this.canvasControl.ClientSize.Height);
                if (visScreenRectObj != null)
                {
                    vsX = Convert.ToDouble(ReadProperty(visScreenRectObj, "X") ?? vsX);
                    vsY = Convert.ToDouble(ReadProperty(visScreenRectObj, "Y") ?? vsY);
                    vsW = Convert.ToDouble(ReadProperty(visScreenRectObj, "Width") ?? vsW);
                    vsH = Convert.ToDouble(ReadProperty(visScreenRectObj, "Height") ?? vsH);
                }

                this.overlayForm?.UpdateGuideState(
                    docW, docH,
                    finalRenderL, finalRenderT, actualW, actualH,
                    snapL, snapR, snapCenterX,
                    snapT, snapB, snapCenterY,
                    badgeText, badgeDocPt,
                    vdX, vdY, vdW, vdH,
                    vsX, vsY, vsW, vsH,
                    this.canvasControl);
            }
        }
        catch (Exception ex)
        {
            Boot.LogDiag("movetool-snap", "吸附计算异常: " + ex.Message);
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public bool PreFilterMessage(ref Message m)
    {
        // 0x0100 = WM_KEYDOWN, 0x0101 = WM_KEYUP
        if (m.Msg == 0x0100 || m.Msg == 0x0101)
        {
            int vk = (int)m.WParam & 0xFFFF;
            if (vk == 0x10) // VK_SHIFT
            {
                if (m.Msg == 0x0100)
                {
                    this.wasShiftPressed = true;
                    if (this.isDragging && this.currentHookedTransformControl != null)
                    {
                        this.ProcessSnappingAndGuides();
                    }
                }
                else
                {
                    this.wasShiftPressed = false;
                    if (this.isDragging && this.currentHookedTransformControl != null)
                    {
                        int mode = this.GetCurrentEditingMode();
                        if (mode == 2)
                        {
                            this.ApplyLastScaleSnapOnShiftRelease();
                        }
                        else
                        {
                            this.ApplyLastSnapOnShiftRelease();
                        }
                    }
                }
            }
            else if (vk == (int)Keys.Z && (Control.ModifierKeys & Keys.Control) == Keys.Control)
            {
                this.overlayForm?.HideOverlay();
                LayerChannelHost.Current?.Panel?.NotifyLayerContentChanged();
            }
        }
        // 0x0202 = WM_LBUTTONUP
        else if (m.Msg == 0x0202)
        {
            if (this.isDragging || this.isSnapLocked || this.isScaleSnapLocked)
            {
                this.isPointerReleasePending = true;
                int mode = this.GetCurrentEditingMode();
                if (mode == 2 && this.isScaleSnapLocked) this.EnforceLastScaleSnapBeforeCommit();
                else if (this.isSnapLocked) this.EnforceLastSnapBeforeCommit();
            }
            // 只隐藏辅助线；锁定状态由 EditingFinished / EditingCancelled 清理。
            this.overlayForm?.HideOverlay();
        }

        return false;
    }

    private void OnThreadFilterMessage(ref System.Windows.Interop.MSG msg, ref bool handled)
    {
        Message m = Message.Create(msg.hwnd, msg.message, msg.wParam, msg.lParam);
        this.PreFilterMessage(ref m);
    }

    internal void EnforceLastSnapBeforeCommit()
    {
        if (this.currentHookedTransformControl is null || !this.isSnapLocked) return;
        try
        {
            object tc = this.currentHookedTransformControl;
            double targetX = !double.IsNaN(this.lockedTargetCanvasX) ? this.lockedTargetCanvasX : this.lastTargetCanvasX;
            double targetY = !double.IsNaN(this.lockedTargetCanvasY) ? this.lockedTargetCanvasY : this.lastTargetCanvasY;
            if (double.IsNaN(targetX) || double.IsNaN(targetY)) return;

            object? baseBoundsObj = ReadProperty(tc, "BaseBounds");
            if (baseBoundsObj is null) return;
            double bbX = Convert.ToDouble(ReadProperty(baseBoundsObj, "X") ?? 0);
            double bbY = Convert.ToDouble(ReadProperty(baseBoundsObj, "Y") ?? 0);
            double bbW = Convert.ToDouble(ReadProperty(baseBoundsObj, "Width") ?? 0);
            double bbH = Convert.ToDouble(ReadProperty(baseBoundsObj, "Height") ?? 0);

            object? deltaTransform = ReadProperty(tc, "DeltaTransform") ?? ReadProperty(tc, "activeEditToken");
            object? baseTransform = ReadProperty(tc, "BaseTransform");

            GetActualRenderedBounds(bbX, bbY, bbW, bbH, baseTransform, deltaTransform,
                out double preCanvasX, out double preCanvasY, out _, out _);

            object? ui = ReadProperty(this.currentHookedTool, "UI");
            object? token = ReadProperty(tc, "ActiveEditToken")
                            ?? ReadField(tc, "activeEditToken")
                            ?? ReadField(tc, "pointerEditToken")
                            ?? (ui != null ? (ReadProperty(ui, "ActiveEditToken") ?? ReadField(ui, "activeEditToken")) : null);

            object? editTransform = (token != null ? (ReadProperty(token, "EditTransform") ?? ReadField(token, "editTransform")) : null)
                                   ?? ReadProperty(tc, "EditTransform")
                                   ?? (ui != null ? ReadProperty(ui, "EditTransform") : null);
            if (editTransform is null || !TryGetTranslation(editTransform, out _, out _, out var setTxTy) || setTxTy is null) return;

            double targetEditTx = targetX - preCanvasX;
            double targetEditTy = targetY - preCanvasY;

            this.isAdjusting = true;
            try
            {
                using var batch = BeginTransformChanges(tc);
                setTxTy(targetEditTx, targetEditTy);
                NotifyTransformChanged(tc, ui, token, editTransform);
                Boot.Log($"[MoveToolGuide] 鼠标抬起提交前锁定成功：坐标 ({targetX}, {targetY})");
            }
            finally
            {
                this.isAdjusting = false;
            }
        }
        catch (Exception ex)
        {
            Boot.LogDiag("movetool-precommit", "EnforceLastSnapBeforeCommit 异常: " + ex.Message);
        }
    }

    internal void EnforceLastScaleSnapBeforeCommit()
    {
        if (this.currentHookedTransformControl is null) return;
        if (!this.isScaleSnapLocked) return;

        double targetScaleX = !double.IsNaN(this.lockedTargetScaleX) ? this.lockedTargetScaleX : this.lastSnappedScaleX;
        double targetScaleY = !double.IsNaN(this.lockedTargetScaleY) ? this.lockedTargetScaleY : this.lastSnappedScaleY;

        if ((double.IsNaN(targetScaleX) || targetScaleX <= 0.0001) &&
            (double.IsNaN(targetScaleY) || targetScaleY <= 0.0001))
        {
            double targetScale = !double.IsNaN(this.lockedTargetScale) ? this.lockedTargetScale : this.lastSnappedScaleFactor;
            if (double.IsNaN(targetScale) || targetScale <= 0.0001) return;
            targetScaleX = targetScale;
            targetScaleY = targetScale;
        }

        try
        {
            object tc = this.currentHookedTransformControl;
            object? baseBoundsObj = ReadProperty(tc, "BaseBounds");
            if (baseBoundsObj is null) return;

            double bbW = Convert.ToDouble(ReadProperty(baseBoundsObj, "Width") ?? 0);
            double bbH = Convert.ToDouble(ReadProperty(baseBoundsObj, "Height") ?? 0);
            if (bbW <= 0 || bbH <= 0) return;

            object? ui = ReadProperty(this.currentHookedTool, "UI");
            object? token = ReadProperty(tc, "ActiveEditToken")
                            ?? ReadField(tc, "activeEditToken")
                            ?? ReadField(tc, "pointerEditToken")
                            ?? (ui != null ? (ReadProperty(ui, "ActiveEditToken") ?? ReadField(ui, "activeEditToken")) : null);

            object? editTransform = (token != null ? (ReadProperty(token, "EditTransform") ?? ReadField(token, "editTransform")) : null)
                                   ?? ReadProperty(tc, "EditTransform")
                                   ?? (ui != null ? ReadProperty(ui, "EditTransform") : null);

            if (editTransform is null) return;

            PropertyInfo? pScaleX = editTransform.GetType().GetProperty("ScaleX");
            PropertyInfo? pScaleY = editTransform.GetType().GetProperty("ScaleY");
            if (pScaleX == null || pScaleY == null) return;

            double curScaleX = Convert.ToDouble(pScaleX.GetValue(editTransform) ?? 1.0);
            double curScaleY = Convert.ToDouble(pScaleY.GetValue(editTransform) ?? 1.0);
            double signX = Math.Sign(curScaleX) != 0 ? Math.Sign(curScaleX) : 1.0;
            double signY = Math.Sign(curScaleY) != 0 ? Math.Sign(curScaleY) : 1.0;

            double newScaleX = double.IsNaN(targetScaleX) || targetScaleX <= 0.0001 ? curScaleX : signX * Math.Abs(targetScaleX);
            double newScaleY = double.IsNaN(targetScaleY) || targetScaleY <= 0.0001 ? curScaleY : signY * Math.Abs(targetScaleY);

            this.isAdjusting = true;
            try
            {
                using var batch = BeginTransformChanges(tc);
                pScaleX.SetValue(editTransform, newScaleX);
                pScaleY.SetValue(editTransform, newScaleY);
                NotifyTransformChanged(tc, ui, token, editTransform);
                this.lastObservedScaleX = newScaleX;
                this.lastObservedScaleY = newScaleY;
                Boot.Log($"[MoveToolGuide] 缩放鼠标抬起提交前锁定成功：尺寸 ({bbW * Math.Abs(newScaleX):0} × {bbH * Math.Abs(newScaleY):0})");
            }
            finally
            {
                this.isAdjusting = false;
            }
        }
        catch (Exception ex)
        {
            Boot.LogDiag("movetool-scale-precommit", "EnforceLastScaleSnapBeforeCommit 异常: " + ex.Message);
        }
    }

    /// <summary>
    /// 松开快捷键时强行执行一次最后判定的吸附位置锁定
    /// </summary>
    internal void ApplyLastSnapOnShiftRelease()
    {
        if (this.currentHookedTransformControl is null)
        {
            this.overlayForm?.HideOverlay();
            return;
        }

        double targetX = !double.IsNaN(this.lockedTargetCanvasX) ? this.lockedTargetCanvasX : this.lastTargetCanvasX;
        double targetY = !double.IsNaN(this.lockedTargetCanvasY) ? this.lockedTargetCanvasY : this.lastTargetCanvasY;

        bool wasSnapped = this.isSnapLocked ||
                          ((this.lastSnappedL || this.lastSnappedR || this.lastSnappedCenterX ||
                            this.lastSnappedT || this.lastSnappedB || this.lastSnappedCenterY) &&
                           !double.IsNaN(targetX) && !double.IsNaN(targetY));

        if (!wasSnapped)
        {
            this.overlayForm?.HideOverlay();
            return;
        }

        try
        {
            object tc = this.currentHookedTransformControl;
            object? baseBoundsObj = ReadProperty(tc, "BaseBounds");
            if (baseBoundsObj is null)
            {
                this.overlayForm?.HideOverlay();
                return;
            }

            double bbX = Convert.ToDouble(ReadProperty(baseBoundsObj, "X") ?? 0);
            double bbY = Convert.ToDouble(ReadProperty(baseBoundsObj, "Y") ?? 0);
            double bbW = Convert.ToDouble(ReadProperty(baseBoundsObj, "Width") ?? 0);
            double bbH = Convert.ToDouble(ReadProperty(baseBoundsObj, "Height") ?? 0);

            object? deltaTransform = ReadProperty(tc, "DeltaTransform");
            object? baseTransform = ReadProperty(tc, "BaseTransform");

            GetActualRenderedBounds(bbX, bbY, bbW, bbH, baseTransform, deltaTransform,
                out double preCanvasX, out double preCanvasY, out _, out _);

            object? ui = ReadProperty(this.currentHookedTool, "UI");
            object? token = ReadProperty(tc, "ActiveEditToken")
                            ?? ReadField(tc, "activeEditToken")
                            ?? ReadField(tc, "pointerEditToken")
                            ?? (ui != null ? (ReadProperty(ui, "ActiveEditToken") ?? ReadField(ui, "activeEditToken")) : null);

            object? editTransform = (token != null ? (ReadProperty(token, "EditTransform") ?? ReadField(token, "editTransform")) : null)
                                   ?? ReadProperty(tc, "EditTransform")
                                   ?? (ui != null ? ReadProperty(ui, "EditTransform") : null);

            if (editTransform is null || !TryGetTranslation(editTransform, out _, out _, out var setTxTy) || setTxTy is null)
            {
                this.overlayForm?.HideOverlay();
                return;
            }

            double targetEditTx = targetX - preCanvasX;
            double targetEditTy = targetY - preCanvasY;

            this.isSnapLocked = true;
            this.lockedTargetCanvasX = targetX;
            this.lockedTargetCanvasY = targetY;

            this.isAdjusting = true;
            try
            {
                using var batch = BeginTransformChanges(tc);
                setTxTy(targetEditTx, targetEditTy);
                NotifyTransformChanged(tc, ui, token, editTransform);
                this.lastEdgeSnapTimestamp = Environment.TickCount64;
                Boot.Log($"[MoveToolGuide] 松开快捷键吸附生效：强制将图片吸附至目标位置 ({targetX}, {targetY})");
            }
            finally
            {
                this.isAdjusting = false;
            }
        }
        catch (Exception ex)
        {
            Boot.LogDiag("movetool-shift-release", "ApplyLastSnapOnShiftRelease 异常: " + ex.Message);
        }
    }

    internal void ApplyLastScaleSnapOnShiftRelease()
    {
        if (this.currentHookedTransformControl is null)
        {
            this.overlayForm?.HideOverlay();
            return;
        }

        if (!this.isScaleSnapLocked)
        {
            this.overlayForm?.HideOverlay();
            return;
        }

        double targetScaleX = !double.IsNaN(this.lockedTargetScaleX) ? this.lockedTargetScaleX : this.lastSnappedScaleX;
        double targetScaleY = !double.IsNaN(this.lockedTargetScaleY) ? this.lockedTargetScaleY : this.lastSnappedScaleY;

        if ((double.IsNaN(targetScaleX) || targetScaleX <= 0.0001) &&
            (double.IsNaN(targetScaleY) || targetScaleY <= 0.0001))
        {
            double targetScale = !double.IsNaN(this.lockedTargetScale) ? this.lockedTargetScale : this.lastSnappedScaleFactor;
            if (double.IsNaN(targetScale) || targetScale <= 0.0001) return;
            targetScaleX = targetScale;
            targetScaleY = targetScale;
        }

        try
        {
            object tc = this.currentHookedTransformControl;
            object? baseBoundsObj = ReadProperty(tc, "BaseBounds");
            if (baseBoundsObj is null) return;

            double bbW = Convert.ToDouble(ReadProperty(baseBoundsObj, "Width") ?? 0);
            double bbH = Convert.ToDouble(ReadProperty(baseBoundsObj, "Height") ?? 0);
            if (bbW <= 0 || bbH <= 0) return;

            object? ui = ReadProperty(this.currentHookedTool, "UI");
            object? token = ReadProperty(tc, "ActiveEditToken")
                            ?? ReadField(tc, "activeEditToken")
                            ?? ReadField(tc, "pointerEditToken")
                            ?? (ui != null ? (ReadProperty(ui, "ActiveEditToken") ?? ReadField(ui, "activeEditToken")) : null);

            object? editTransform = (token != null ? (ReadProperty(token, "EditTransform") ?? ReadField(token, "editTransform")) : null)
                                   ?? ReadProperty(tc, "EditTransform")
                                   ?? (ui != null ? ReadProperty(ui, "EditTransform") : null);

            if (editTransform is null) return;

            PropertyInfo? pScaleX = editTransform.GetType().GetProperty("ScaleX");
            PropertyInfo? pScaleY = editTransform.GetType().GetProperty("ScaleY");
            if (pScaleX == null || pScaleY == null) return;

            double curScaleX = Convert.ToDouble(pScaleX.GetValue(editTransform) ?? 1.0);
            double curScaleY = Convert.ToDouble(pScaleY.GetValue(editTransform) ?? 1.0);
            double signX = Math.Sign(curScaleX) != 0 ? Math.Sign(curScaleX) : 1.0;
            double signY = Math.Sign(curScaleY) != 0 ? Math.Sign(curScaleY) : 1.0;

            double newScaleX = double.IsNaN(targetScaleX) || targetScaleX <= 0.0001 ? curScaleX : signX * Math.Abs(targetScaleX);
            double newScaleY = double.IsNaN(targetScaleY) || targetScaleY <= 0.0001 ? curScaleY : signY * Math.Abs(targetScaleY);

            this.isScaleSnapLocked = true;
            this.lockedTargetScaleX = Math.Abs(newScaleX);
            this.lockedTargetScaleY = Math.Abs(newScaleY);
            this.lockedTargetScale = Math.Max(this.lockedTargetScaleX, this.lockedTargetScaleY);

            this.isAdjusting = true;
            try
            {
                using var batch = BeginTransformChanges(tc);
                pScaleX.SetValue(editTransform, newScaleX);
                pScaleY.SetValue(editTransform, newScaleY);
                NotifyTransformChanged(tc, ui, token, editTransform);
                this.lastObservedScaleX = newScaleX;
                this.lastObservedScaleY = newScaleY;
                this.lastScaleSnapTimestamp = Environment.TickCount64;
                Boot.Log($"[MoveToolGuide] 松开Shift缩放吸附生效：强制锁定尺寸 ({bbW * Math.Abs(newScaleX):0} × {bbH * Math.Abs(newScaleY):0})");
            }
            finally
            {
                this.isAdjusting = false;
            }
        }
        catch (Exception ex)
        {
            Boot.LogDiag("movetool-scale-shift-release", "ApplyLastScaleSnapOnShiftRelease 异常: " + ex.Message);
        }
    }

    private static int GetHandleTypeValue(object tc, object? handleObj)
    {
        if (handleObj == null) return -1;
        if (handleObj is int val) return val;
        if (handleObj.GetType().IsEnum) return Convert.ToInt32(handleObj);

        try
        {
            Type tcType = tc.GetType();
            MethodInfo? mGet = tcType.GetMethod("GetTransformHandleType", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (mGet != null)
            {
                object? res = mGet.Invoke(null, new object[] { handleObj });
                if (res != null) return Convert.ToInt32(res);
            }
        }
        catch {}

        return -1;
    }

    private static int GetScaleHandle(object tc, object? token, object? editTransform)
    {
        if (editTransform != null)
        {
            object? hObj = ReadProperty(editTransform, "ScaleHandle");
            if (hObj != null) return Convert.ToInt32(hObj);
        }
        if (token != null)
        {
            object? hObj = ReadProperty(token, "TriggerHandle");
            if (hObj != null) return Convert.ToInt32(hObj);
        }
        FieldInfo? fHandle = tc.GetType().GetField("dragHandle", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (fHandle != null)
        {
            object? hObj = fHandle.GetValue(tc);
            return GetHandleTypeValue(tc, hObj);
        }
        return -1;
    }

    private static void GetHandleEdgeScalers(int handle, out int left, out int top, out int right, out int bottom)
    {
        switch (handle)
        {
            case 6:  // ScaleEdgeW
            case 10: // ScaleW
                left = 1; top = 0; right = 0; bottom = 0; break;
            case 7:  // ScaleEdgeN
            case 11: // ScaleN
                left = 0; top = 1; right = 0; bottom = 0; break;
            case 8:  // ScaleEdgeE
            case 12: // ScaleE
                left = 0; top = 0; right = 1; bottom = 0; break;
            case 9:  // ScaleEdgeS
            case 13: // ScaleS
                left = 0; top = 0; right = 0; bottom = 1; break;
            case 14: // ScaleNW
                left = 1; top = 1; right = 0; bottom = 0; break;
            case 15: // ScaleNE
                left = 0; top = 1; right = 1; bottom = 0; break;
            case 16: // ScaleSW
                left = 1; top = 0; right = 0; bottom = 1; break;
            case 17: // ScaleSE
                left = 0; top = 0; right = 1; bottom = 1; break;
            default:
                left = 0; top = 0; right = 0; bottom = 0; break;
        }
    }

    private static bool TryGetLatestDragPoint(object tc, out PaintDotNet.Rendering.Point2Double point)
    {
        object? pointObj = ReadField(tc, "latestDragMovePt");
        if (pointObj is PaintDotNet.Rendering.Point2Double candidate &&
            double.IsFinite(candidate.X) && double.IsFinite(candidate.Y) &&
            Math.Abs(candidate.X) < 1e8 && Math.Abs(candidate.Y) < 1e8)
        {
            point = candidate;
            return true;
        }

        point = default;
        return false;
    }

    // 每次都从原始鼠标手势计算，绝不把已吸附的输出累加回输入。
    // Paint.NET 分别写入 ScaleX / ScaleY 时也会触发 EditChanged，因此不能依赖两轴的中间值。
    private static bool TryGetRawScaleInput(
        object tc, object? token, object editTransform, double bbW, double bbH,
        bool isShift, bool isAlt, out double scaleX, out double scaleY)
    {
        scaleX = scaleY = 1.0;
        int handle = GetScaleHandle(tc, token, editTransform);
        if (handle < 6 || handle > 17 || bbW <= 0 || bbH <= 0 ||
            !TryGetLatestDragPoint(tc, out var point) ||
            ReadField(tc, "dragBeginPt") is not PaintDotNet.Rendering.Point2Double begin ||
            ReadField(tc, "finalTxBeforeEditing") is not PaintDotNet.Rendering.Matrix3x2Double finalMatrix ||
            !double.IsFinite(begin.X) || !double.IsFinite(begin.Y))
        {
            return false;
        }

        var inverse = finalMatrix.Inverse;
        var localPoint = inverse.Transform(point);
        var localBegin = inverse.Transform(begin);
        double dx = localPoint.X - localBegin.X;
        double dy = localPoint.Y - localBegin.Y;
        if (!double.IsFinite(dx) || !double.IsFinite(dy)) return false;

        GetHandleEdgeScalers(handle, out int left, out int top, out int right, out int bottom);
        double multiplier = isAlt ? 2.0 : 1.0;
        double signedX = 1.0 + multiplier * (right - left) * dx / bbW;
        double signedY = 1.0 + multiplier * (bottom - top) * dy / bbH;
        double rawX = Math.Abs(signedX);
        double rawY = Math.Abs(signedY);
        double axisX = Math.Sqrt(finalMatrix.M11 * finalMatrix.M11 + finalMatrix.M12 * finalMatrix.M12);
        double axisY = Math.Sqrt(finalMatrix.M21 * finalMatrix.M21 + finalMatrix.M22 * finalMatrix.M22);
        if (!double.IsFinite(axisX) || !double.IsFinite(axisY) || axisX <= 0 || axisY <= 0) return false;

        // 与原生 Shift 约束一致，考虑拖动前已经存在的非等比缩放。
        if (isShift)
        {
            double uniform = Math.Min(
                left != 0 || right != 0 ? axisX * rawX : double.PositiveInfinity,
                top != 0 || bottom != 0 ? axisY * rawY : double.PositiveInfinity);
            rawX = uniform / axisX;
            rawY = uniform / axisY;
        }

        if (ReadProperty(tc, "IsInputPixelSnappingEnabled") is true)
        {
            int stepX = (isShift && left == 0 && right == 0) || (isAlt && (left != 0 || right != 0)) ? 2 : 1;
            int stepY = (isShift && top == 0 && bottom == 0) || (isAlt && (top != 0 || bottom != 0)) ? 2 : 1;
            rawX = QuantizeNativeScale(rawX, bbW * axisX, stepX);
            rawY = QuantizeNativeScale(rawY, bbH * axisY, stepY);
        }

        scaleX = Math.CopySign(Math.Max(1e-7, rawX), signedX);
        scaleY = Math.CopySign(Math.Max(1e-7, rawY), signedY);
        return double.IsFinite(scaleX) && double.IsFinite(scaleY);
    }

    private static double QuantizeNativeScale(double value, double dimension, int step)
    {
        double unit = step / dimension;
        int parity = (int)Math.Round(dimension) & (step - 1);
        return (Math.Round(value / unit) + parity / 2.0) * unit;
    }

    private static int SelectAxisSnap(double position, double[] targets, int lockedIndex, double enter, double exit)
    {
        // 迟滞作用于同一个目标，避免在重叠区域重新选择另一坐标。
        if (lockedIndex >= 0 && Math.Abs(position - targets[lockedIndex]) <= exit) return lockedIndex;
        int best = -1;
        double bestDistance = enter;
        for (int i = 0; i < targets.Length; i++)
        {
            double distance = Math.Abs(position - targets[i]);
            if (distance <= enter && (best < 0 || distance < bestDistance))
            {
                best = i;
                bestDistance = distance;
            }
        }
        return best;
    }

    private static double GetNearestPowerOf2(double dimension, out int exponent)
    {
        if (dimension <= 1.0)
        {
            exponent = 1;
            return 2.0;
        }

        double log2 = Math.Log2(dimension);
        int expFloor = (int)Math.Floor(log2);
        int expCeil = expFloor + 1;

        double p1 = Math.Pow(2, expFloor);
        double p2 = Math.Pow(2, expCeil);

        if (Math.Abs(dimension - p1) <= Math.Abs(dimension - p2))
        {
            exponent = expFloor;
            return p1;
        }
        else
        {
            exponent = expCeil;
            return p2;
        }
    }

    private static void GetPowerOf2SnapThresholds(
        double currentDim, double targetP, double scale,
        out double threshIn, out double threshOut)
    {
        double gap = currentDim < targetP ? targetP * 0.5 : targetP;
        double inCap = gap * ScaleSnapMaxInGapRatio;
        double outCap = gap * ScaleSnapMaxOutGapRatio;
        threshIn = Math.Min(ScaleSnapInScreenPx / scale, inCap);
        threshOut = Math.Min(ScaleSnapOutScreenPx / scale, outCap);
        // 小尺寸或低缩放比例下也不能让最小保持半径突破相邻幂间距的上限。
        threshOut = Math.Min(outCap, Math.Max(threshOut, threshIn + Math.Min(4.0 / scale, outCap - inCap)));
    }

    private void ProcessScaleSnappingAndGuides(
        object tc, object? ui, object? token, object editTransform,
        double bbW, double bbH, bool forceActive, bool isShift)
    {
        bool isAlt = (GetAsyncKeyState(0x12) & 0x8000) != 0 || ((Control.ModifierKeys & Keys.Alt) == Keys.Alt);
        bool isLButton = forceActive || this.isDragging || (GetAsyncKeyState(0x01) & 0x8000) != 0 || ((Control.MouseButtons & MouseButtons.Left) == MouseButtons.Left);

        if (!isLButton && !forceActive)
        {
            this.isScaleSnapLocked = false;
            this.lockedScaleSize = double.NaN;
            this.lockedTargetScale = double.NaN;
            this.lockedTargetScaleX = double.NaN;
            this.lockedTargetScaleY = double.NaN;
            this.overlayForm?.HideOverlay();
            return;
        }

        PropertyInfo? pScaleX = editTransform.GetType().GetProperty("ScaleX");
        PropertyInfo? pScaleY = editTransform.GetType().GetProperty("ScaleY");
        if (pScaleX == null || pScaleY == null) return;

        double nativeScaleX = Convert.ToDouble(pScaleX.GetValue(editTransform) ?? 1.0);
        double nativeScaleY = Convert.ToDouble(pScaleY.GetValue(editTransform) ?? 1.0);
        double curScaleX = nativeScaleX;
        double curScaleY = nativeScaleY;
        if (TryGetRawScaleInput(tc, token, editTransform, bbW, bbH, isShift, isAlt, out double rawX, out double rawY))
        {
            curScaleX = rawX;
            curScaleY = rawY;
        }
        double absScaleX = Math.Abs(curScaleX);
        double absScaleY = Math.Abs(curScaleY);

        int handle = GetScaleHandle(tc, token, editTransform);
        bool isCorner = (handle >= 14 && handle <= 17) || handle < 0;

        // 防点击即吸附：未发生实际拖拽缩放位移时不触发吸附
        if (!this.hasDragMoved && !forceActive)
        {
            if (Math.Abs(absScaleX - 1.0) >= 0.002 || Math.Abs(absScaleY - 1.0) >= 0.002)
            {
                this.hasDragMoved = true;
            }
            else
            {
                FieldInfo? fBegin = tc.GetType().GetField("dragBeginPt", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                FieldInfo? fLatest = tc.GetType().GetField("latestDragMovePt", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (fBegin?.GetValue(tc) is PaintDotNet.Rendering.Point2Double bPt &&
                    fLatest?.GetValue(tc) is PaintDotNet.Rendering.Point2Double lPt)
                {
                    double dmx = lPt.X - bPt.X;
                    double dmy = lPt.Y - bPt.Y;
                    if (dmx * dmx + dmy * dmy >= 9.0)
                    {
                        this.hasDragMoved = true;
                    }
                }
            }

            if (!this.hasDragMoved)
            {
                this.overlayForm?.HideOverlay();
                return;
            }
        }

        this.lastObservedScaleX = curScaleX;
        this.lastObservedScaleY = curScaleY;

        double scale = 1.0;
        object? scaleFactorObj = ReadProperty(this.workspace, "ScaleFactor");
        if (scaleFactorObj is not null)
        {
            scale = Convert.ToDouble(ReadProperty(scaleFactorObj, "Ratio") ?? 1.0);
        }
        if (scale <= 0.001) scale = 1.0;

        double edgeThreshIn = EdgeSnapInScreenPx / scale;
        double edgeThreshOut = EdgeSnapOutScreenPx / scale;

        Document? doc = this.document ?? PdnModel.TryGetDocument(this.workspace);
        if (doc is null) return;
        double docW = doc.Width;
        double docH = doc.Height;

        object? deltaTransform = (token != null ? ReadProperty(token, "OldDeltaTransform") : null)
                                ?? ReadProperty(tc, "DeltaTransform");
        object? baseTransform = (token != null ? ReadProperty(token, "BaseTransform") : null)
                               ?? ReadProperty(tc, "BaseTransform");

        object? baseBoundsObj = ReadProperty(tc, "BaseBounds");
        double bbX = Convert.ToDouble(ReadProperty(baseBoundsObj, "X") ?? 0);
        double bbY = Convert.ToDouble(ReadProperty(baseBoundsObj, "Y") ?? 0);

        GetActualRenderedBounds(bbX, bbY, bbW, bbH, baseTransform, deltaTransform,
            out double curCanvasX, out double curCanvasY,
            out double curActualW, out double curActualH);

        if (curActualW <= 0.0001) curActualW = bbW;
        if (curActualH <= 0.0001) curActualH = bbH;

        double targetScaleX = absScaleX;
        double targetScaleY = absScaleY;
        bool snapped = false;
        string badgeText = "";
        bool snapL = false, snapR = false, snapT = false, snapB = false;

        if (isCorner)
        {
            // 两轴输入来自同一鼠标点，不使用原生属性更新过程中的混合值。
            double curW = curActualW * absScaleX;
            double curH = curActualH * absScaleY;
            if (curW <= 0.0001 || curH <= 0.0001) return;
            double targetScale = 1.0;
            bool keepCornerTarget = false;

            // 1. 如果已吸附，检查是否脱离吸附
            if (this.isScaleSnapLocked && !double.IsNaN(this.lockedTargetScale) && !double.IsNaN(this.lockedScaleSize))
            {
                double targetDim = this.lockedScaleSize;
                double curDim = this.lockedScaleOnHeight ? curH : curW;
                GetPowerOf2SnapThresholds(curDim, targetDim, scale, out _, out double threshOut);

                double pullDist = Math.Abs(curDim - targetDim);
                if (pullDist <= threshOut)
                {
                    targetScaleX = this.lockedTargetScaleX;
                    targetScaleY = this.lockedTargetScaleY;
                    keepCornerTarget = true;
                    snapped = true;
                    badgeText = this.lastBadgeText;
                    if (string.IsNullOrEmpty(badgeText))
                    {
                        double finalW = Math.Round(curActualW * targetScaleX);
                        double finalH = Math.Round(curActualH * targetScaleY);
                        badgeText = $"尺寸吸附: {finalW:0} × {finalH:0}px";
                    }
                }
                else
                {
                    this.isScaleSnapLocked = false;
                    this.lockedScaleSize = double.NaN;
                    this.lockedTargetScale = double.NaN;
                    this.lockedTargetScaleX = double.NaN;
                    this.lockedTargetScaleY = double.NaN;
                    snapped = false;
                }
            }

            // 2. 如果未吸附且按住了 Shift，按当前尺寸检测是否触发 2 的整数幂吸附
            if (!snapped && isShift && !this.isScaleSnapLocked)
            {
                double nearestPW = GetNearestPowerOf2(curW, out int expW);
                double distW = Math.Abs(curW - nearestPW);
                GetPowerOf2SnapThresholds(curW, nearestPW, scale, out double threshInW, out _);

                double nearestPH = GetNearestPowerOf2(curH, out int expH);
                double distH = Math.Abs(curH - nearestPH);
                GetPowerOf2SnapThresholds(curH, nearestPH, scale, out double threshInH, out _);

                double deltaScaleW = distW / curActualW;
                double deltaScaleH = distH / curActualH;

                bool canSnapW = distW <= threshInW;
                bool canSnapH = distH <= threshInH;

                if (canSnapW && canSnapH)
                {
                    if (deltaScaleW <= deltaScaleH) canSnapH = false;
                    else canSnapW = false;
                }

                if (canSnapW)
                {
                    targetScale = nearestPW / curW;
                    snapped = true;
                    this.lockedScaleSize = nearestPW;
                    this.lockedScaleOnHeight = false;
                    double finalW = Math.Round(curW * targetScale);
                    double finalH = Math.Round(curH * targetScale);
                    badgeText = $"尺寸吸附: 宽={finalW:0}px (2^{expW}) 高={finalH:0}px";
                }
                else if (canSnapH)
                {
                    targetScale = nearestPH / curH;
                    snapped = true;
                    this.lockedScaleSize = nearestPH;
                    this.lockedScaleOnHeight = true;
                    double finalW = Math.Round(curW * targetScale);
                    double finalH = Math.Round(curH * targetScale);
                    badgeText = $"尺寸吸附: 高={finalH:0}px (2^{expH}) 宽={finalW:0}px";
                }
            }

            if (snapped && !keepCornerTarget)
            {
                targetScaleX = absScaleX * targetScale;
                targetScaleY = absScaleY * targetScale;
            }
        }
        else
        {
            // === 边缘把手 (6..13: ScaleW, ScaleE, ScaleN, ScaleS, ScaleEdgeW..S)：仅缩放对应单轴 ===
            bool isWest = handle == 6 || handle == 10;
            bool isEast = handle == 8 || handle == 12;
            bool isNorth = handle == 7 || handle == 11;
            bool isSouth = handle == 9 || handle == 13;

            if (isWest || isEast)
            {
                double curW = curActualW * absScaleX;

                // 1. 如果已吸附，检查是否脱离
                if (this.isScaleSnapLocked && !double.IsNaN(this.lockedTargetScaleX))
                {
                    double lockedW = curActualW * this.lockedTargetScaleX;
                    double pullDist = Math.Abs(curW - lockedW);
                    double currentOutThresh = edgeThreshOut;
                    if (!this.lastScaleSnappedL && !this.lastScaleSnappedR)
                    {
                        double targetDim = !double.IsNaN(this.lockedScaleSize) ? this.lockedScaleSize : lockedW;
                        GetPowerOf2SnapThresholds(curW, targetDim, scale, out _, out currentOutThresh);
                    }

                    if (pullDist <= currentOutThresh)
                    {
                        targetScaleX = this.lockedTargetScaleX;
                        badgeText = this.lastBadgeText;
                        snapL = this.lastScaleSnappedL;
                        snapR = this.lastScaleSnappedR;
                        snapped = true;
                    }
                    else
                    {
                        this.isScaleSnapLocked = false;
                        this.lockedScaleSize = double.NaN;
                        this.lockedTargetScaleX = double.NaN;
                        snapped = false;
                    }
                }

                // 2. 如果未吸附，检测吸附
                if (!snapped && !this.isScaleSnapLocked)
                {
                    double bestDist = double.MaxValue;
                    double candidateScaleX = absScaleX;
                    string candidateBadge = "";
                    bool candSnapL = false, candSnapR = false;
                    double candLockedSize = double.NaN;

                    // 画布边缘对齐吸附 (靠近画布边缘时吸附)
                    if (isEast)
                    {
                        double curR = curCanvasX + curW;
                        double distBorder = Math.Abs(curR - docW);
                        if (distBorder <= edgeThreshIn)
                        {
                            double targetW = Math.Max(1.0, docW - curCanvasX);
                            bestDist = distBorder;
                            candidateScaleX = targetW / curActualW;
                            candidateBadge = $"边缘吸附: 右边缘对齐画布 (宽={targetW:0}px)";
                            candSnapR = true;
                        }
                    }
                    else if (isWest)
                    {
                        double curL = curCanvasX + curActualW - curW;
                        double distBorder = Math.Abs(curL - 0.0);
                        if (distBorder <= edgeThreshIn)
                        {
                            double fixedR = curCanvasX + curActualW;
                            double targetW = Math.Max(1.0, fixedR);
                            bestDist = distBorder;
                            candidateScaleX = targetW / curActualW;
                            candidateBadge = $"边缘吸附: 左边缘对齐画布 (宽={targetW:0}px)";
                            candSnapL = true;
                        }
                    }

                    // 单轴 2 的整数幂吸附 (按住 Shift 触发)
                    if (isShift)
                    {
                        double nearestPW = GetNearestPowerOf2(curW, out int expW);
                        double distPW = Math.Abs(curW - nearestPW);
                        GetPowerOf2SnapThresholds(curW, nearestPW, scale, out double threshInW, out _);
                        if (distPW <= threshInW && distPW < bestDist)
                        {
                            bestDist = distPW;
                            candidateScaleX = nearestPW / curActualW;
                            candidateBadge = $"边长吸附: 宽={nearestPW:0}px (2^{expW})";
                            candSnapL = false;
                            candSnapR = false;
                            candLockedSize = nearestPW;
                        }
                    }

                    if (bestDist < double.MaxValue)
                    {
                        targetScaleX = candidateScaleX;
                        badgeText = candidateBadge;
                        snapL = candSnapL;
                        snapR = candSnapR;
                        this.lockedScaleSize = candLockedSize;
                        snapped = true;
                    }
                }
            }
            else if (isNorth || isSouth)
            {
                double curH = curActualH * absScaleY;

                // 1. 如果已吸附，检查是否脱离
                if (this.isScaleSnapLocked && !double.IsNaN(this.lockedTargetScaleY))
                {
                    double lockedH = curActualH * this.lockedTargetScaleY;
                    double pullDist = Math.Abs(curH - lockedH);
                    double currentOutThresh = edgeThreshOut;
                    if (!this.lastScaleSnappedT && !this.lastScaleSnappedB)
                    {
                        double targetDim = !double.IsNaN(this.lockedScaleSize) ? this.lockedScaleSize : lockedH;
                        GetPowerOf2SnapThresholds(curH, targetDim, scale, out _, out currentOutThresh);
                    }

                    if (pullDist <= currentOutThresh)
                    {
                        targetScaleY = this.lockedTargetScaleY;
                        badgeText = this.lastBadgeText;
                        snapT = this.lastScaleSnappedT;
                        snapB = this.lastScaleSnappedB;
                        snapped = true;
                    }
                    else
                    {
                        this.isScaleSnapLocked = false;
                        this.lockedScaleSize = double.NaN;
                        this.lockedTargetScaleY = double.NaN;
                        snapped = false;
                    }
                }

                // 2. 如果未吸附，检测吸附
                if (!snapped && !this.isScaleSnapLocked)
                {
                    double bestDist = double.MaxValue;
                    double candidateScaleY = absScaleY;
                    string candidateBadge = "";
                    bool candSnapT = false, candSnapB = false;
                    double candLockedSize = double.NaN;

                    // 画布边缘对齐吸附 (靠近画布边缘时吸附)
                    if (isSouth)
                    {
                        double curB = curCanvasY + curH;
                        double distBorder = Math.Abs(curB - docH);
                        if (distBorder <= edgeThreshIn)
                        {
                            double targetH = Math.Max(1.0, docH - curCanvasY);
                            bestDist = distBorder;
                            candidateScaleY = targetH / curActualH;
                            candidateBadge = $"边缘吸附: 底边缘对齐画布 (高={targetH:0}px)";
                            candSnapB = true;
                        }
                    }
                    else if (isNorth)
                    {
                        double curT = curCanvasY + curActualH - curH;
                        double distBorder = Math.Abs(curT - 0.0);
                        if (distBorder <= edgeThreshIn)
                        {
                            double fixedB = curCanvasY + curActualH;
                            double targetH = Math.Max(1.0, fixedB);
                            bestDist = distBorder;
                            candidateScaleY = targetH / curActualH;
                            candidateBadge = $"边缘吸附: 顶边缘对齐画布 (高={targetH:0}px)";
                            candSnapT = true;
                        }
                    }

                    // 单轴 2 的整数幂吸附 (按住 Shift 触发)
                    if (isShift)
                    {
                        double nearestPH = GetNearestPowerOf2(curH, out int expH);
                        double distPH = Math.Abs(curH - nearestPH);
                        GetPowerOf2SnapThresholds(curH, nearestPH, scale, out double threshInH, out _);
                        if (distPH <= threshInH && distPH < bestDist)
                        {
                            bestDist = distPH;
                            candidateScaleY = nearestPH / curActualH;
                            candidateBadge = $"边长吸附: 高={nearestPH:0}px (2^{expH})";
                            candSnapT = false;
                            candSnapB = false;
                            candLockedSize = nearestPH;
                        }
                    }

                    if (bestDist < double.MaxValue)
                    {
                        targetScaleY = candidateScaleY;
                        badgeText = candidateBadge;
                        snapT = candSnapT;
                        snapB = candSnapB;
                        this.lockedScaleSize = candLockedSize;
                        snapped = true;
                    }
                }
            }
        }

        if (snapped)
        {
            double signX = Math.Sign(curScaleX) != 0 ? Math.Sign(curScaleX) : 1.0;
            double signY = Math.Sign(curScaleY) != 0 ? Math.Sign(curScaleY) : 1.0;
            double newScaleX = signX * targetScaleX;
            double newScaleY = signY * targetScaleY;

            this.isScaleSnapLocked = true;
            this.lockedTargetScaleX = targetScaleX;
            this.lockedTargetScaleY = targetScaleY;
            this.lockedTargetScale = Math.Max(targetScaleX, targetScaleY);

            this.lastSnappedScaleX = newScaleX;
            this.lastSnappedScaleY = newScaleY;
            this.lastSnappedScaleFactor = this.lockedTargetScale;
            this.lastScaleSnapTimestamp = Environment.TickCount64;
            this.lastBadgeText = badgeText;

            this.lastScaleSnappedL = snapL;
            this.lastScaleSnappedR = snapR;
            this.lastScaleSnappedT = snapT;
            this.lastScaleSnappedB = snapB;

            if (Math.Abs(nativeScaleX - newScaleX) > 0.0000001 || Math.Abs(nativeScaleY - newScaleY) > 0.0000001)
            {
                this.isAdjusting = true;
                try
                {
                    using var batch = BeginTransformChanges(tc);
                    pScaleX.SetValue(editTransform, newScaleX);
                    pScaleY.SetValue(editTransform, newScaleY);
                    // 绝不篡改 tc.dragBeginPt，保持原始锚点纯净
                    NotifyTransformChanged(tc, ui, token, editTransform);
                }
                finally
                {
                    this.isAdjusting = false;
                }
                this.lastObservedScaleX = newScaleX;
                this.lastObservedScaleY = newScaleY;
            }

            if (this.canvasControl is not null && isShift)
            {
                if (this.overlayForm is null)
                {
                    this.overlayForm = new GuideOverlayForm(this.canvasControl);
                }

                object? visDocRectObj = ReadProperty(this.workspace, "VisibleDocumentRect");
                object? visScreenRectObj = ReadProperty(this.workspace, "VisibleDocumentScreenRect");

                double vdX = 0, vdY = 0, vdW = docW, vdH = docH;
                if (visDocRectObj != null)
                {
                    vdX = Convert.ToDouble(ReadProperty(visDocRectObj, "X") ?? 0);
                    vdY = Convert.ToDouble(ReadProperty(visDocRectObj, "Y") ?? 0);
                    vdW = Convert.ToDouble(ReadProperty(visDocRectObj, "Width") ?? docW);
                    vdH = Convert.ToDouble(ReadProperty(visDocRectObj, "Height") ?? docH);
                }
                if (vdW <= 0.001) vdW = docW;
                if (vdH <= 0.001) vdH = docH;

                Point screenOrigin = this.canvasControl.PointToScreen(Point.Empty);
                double vsX = screenOrigin.X;
                double vsY = screenOrigin.Y;
                double vsW = Math.Max(1, this.canvasControl.ClientSize.Width);
                double vsH = Math.Max(1, this.canvasControl.ClientSize.Height);
                if (visScreenRectObj != null)
                {
                    vsX = Convert.ToDouble(ReadProperty(visScreenRectObj, "X") ?? vsX);
                    vsY = Convert.ToDouble(ReadProperty(visScreenRectObj, "Y") ?? vsY);
                    vsW = Convert.ToDouble(ReadProperty(visScreenRectObj, "Width") ?? vsW);
                    vsH = Convert.ToDouble(ReadProperty(visScreenRectObj, "Height") ?? vsH);
                }

                GetHandleEdgeScalers(handle, out int hl, out int ht, out int hr, out int hb);
                double renderW = curActualW * targetScaleX;
                double renderH = curActualH * targetScaleY;
                double renderL = (hl == 1) ? (curCanvasX + curActualW - renderW) : curCanvasX;
                double renderT = (ht == 1) ? (curCanvasY + curActualH - renderH) : curCanvasY;

                if (snapL) renderL = 0.0;
                else if (snapR) renderL = docW - renderW;
                if (snapT) renderT = 0.0;
                else if (snapB) renderT = docH - renderH;

                PointF badgeDocPt = new PointF((float)(renderL + renderW / 2.0), (float)(renderT + renderH / 2.0));

                this.overlayForm.UpdateGuideState(
                    docW, docH,
                    renderL, renderT, renderW, renderH,
                    snapL, snapR, false,
                    snapT, snapB, false,
                    badgeText, badgeDocPt,
                    vdX, vdY, vdW, vdH,
                    vsX, vsY, vsW, vsH,
                    this.canvasControl,
                    isScaleSnapped: true);
            }
        }
        else
        {
            this.isScaleSnapLocked = false;
            this.lockedScaleSize = double.NaN;
            this.lockedTargetScale = double.NaN;
            this.lockedTargetScaleX = double.NaN;
            this.lockedTargetScaleY = double.NaN;
            this.lastScaleSnappedL = false;
            this.lastScaleSnappedR = false;
            this.lastScaleSnappedT = false;
            this.lastScaleSnappedB = false;
            this.lastSnappedScaleFactor = double.NaN;
            this.lastScaleSnapTimestamp = 0;
            // 脱离吸附的这一帧立即恢复鼠标对应的原始缩放，两轴都写回。
            // 不能等下一次原生事件，否则第二轴可能仍是上一帧的吸附值。
            if (Math.Abs(nativeScaleX - curScaleX) > 0.0000001 || Math.Abs(nativeScaleY - curScaleY) > 0.0000001)
            {
                this.isAdjusting = true;
                try
                {
                    using var batch = BeginTransformChanges(tc);
                    pScaleX.SetValue(editTransform, curScaleX);
                    pScaleY.SetValue(editTransform, curScaleY);
                    NotifyTransformChanged(tc, ui, token, editTransform);
                }
                finally
                {
                    this.isAdjusting = false;
                }
            }

            this.overlayForm?.HideOverlay();
        }
    }



    private static TransformEditBatch? BeginTransformChanges(object tc)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        MethodInfo? begin = tc.GetType().GetMethod("BeginEditChanges", flags, Type.EmptyTypes);
        MethodInfo? end = tc.GetType().GetMethod("EndEditChanges", flags, new[] { typeof(bool) });
        if (begin == null || end == null) return null;
        begin.Invoke(tc, null);
        return new TransformEditBatch(tc, end);
    }

    private sealed class TransformEditBatch(object tc, MethodInfo end) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (this.disposed) return;
            this.disposed = true;
            // 此时 isAdjusting 仍为 true：插件不再入，原生渲染只看到完整的两轴结果。
            end.Invoke(tc, new object[] { true });
        }
    }

    private static void NotifyTransformChanged(object tc, object? ui, object? token, object? editTransform)
    {
        try
        {
            if (tc is System.Windows.Threading.DispatcherObject d && !d.CheckAccess())
            {
                d.Dispatcher.BeginInvoke(new Action(() => NotifyTransformChanged(tc, ui, token, editTransform)));
                return;
            }

            if (token != null && editTransform != null)
            {
                try
                {
                    PropertyInfo? pTokenEdit = token.GetType().GetProperty("EditTransform");
                    pTokenEdit?.SetValue(token, editTransform);
                }
                catch {}
            }
            if (editTransform != null)
            {
                try
                {
                    PropertyInfo? pTcEdit = tc.GetType().GetProperty("EditTransform");
                    pTcEdit?.SetValue(tc, editTransform);
                }
                catch {}
            }
            if (ui != null && editTransform != null)
            {
                try
                {
                    PropertyInfo? pUiEdit = ui.GetType().GetProperty("EditTransform");
                    pUiEdit?.SetValue(ui, editTransform);
                }
                catch {}
            }

            Type tcType = tc.GetType();
            MethodInfo? mUpdate = tcType.GetMethod("UpdateFinalTransform", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            mUpdate?.Invoke(tc, null);

            MethodInfo? mInval = tcType.GetMethod("InvalidateVisual", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            mInval?.Invoke(tc, null);
        }
        catch (Exception ex)
        {
            Boot.LogDiag("movetool-notify", "NotifyTransformChanged 异常: " + ex.Message);
        }
    }

    private static object? ReadProperty(object? instance, string propName)
    {
        if (instance is null) return null;
        for (Type? t = instance.GetType(); t != null; t = t.BaseType)
        {
            try
            {
                PropertyInfo? pi = t.GetProperty(
                    propName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (pi != null)
                {
                    return pi.GetValue(instance);
                }
            }
            catch {}
        }
        return null;
    }

    private static object? ReadField(object? instance, string fieldName)
    {
        if (instance is null) return null;
        for (Type? t = instance.GetType(); t != null; t = t.BaseType)
        {
            try
            {
                FieldInfo? fi = t.GetField(
                    fieldName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (fi != null)
                {
                    return fi.GetValue(instance);
                }
            }
            catch {}
        }
        return null;
    }

    public void Dispose()
    {
        this.Detach();
    }

    public void Detach()
    {
        Application.RemoveMessageFilter(this);
        try
        {
            System.Windows.Interop.ComponentDispatcher.ThreadFilterMessage -= this.OnThreadFilterMessage;
        }
        catch {}
        this.UnhookCurrentTool();
        if (this.overlayForm != null)
        {
            try { this.overlayForm.Close(); this.overlayForm.Dispose(); } catch {}
            this.overlayForm = null;
        }
        this.canvasControl = null;
        this.workspace = null;
        this.document = null;
    }
}

internal sealed class GuideOverlayForm : Form
{
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [System.Runtime.InteropServices.DllImport("imm32.dll")]
    private static extern IntPtr ImmAssociateContext(IntPtr hWnd, IntPtr hIMC);

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;

    private readonly Control canvasControl;
    private readonly IDisposable? themeSubscription;

    private double docW, docH;
    private double curL, curT, curR, curB;
    private bool snapL, snapR, snapCenterX;
    private bool snapT, snapB, snapCenterY;
    private string snapBadgeText = "";
    private PointF snapBadgeDocPt = PointF.Empty;
    private double vdX, vdY, vdW, vdH;
    private double vsX, vsY, vsW, vsH;
    private bool isVisibleState;
    private bool isScaleSnapped;

    public GuideOverlayForm(Control canvasControl)
    {
        this.canvasControl = canvasControl;
        this.themeSubscription = UiStyle.WatchTheme(this, () => this.Invalidate());
        this.FormBorderStyle = FormBorderStyle.None;
        this.ShowInTaskbar = false;
        this.StartPosition = FormStartPosition.Manual;
        this.BackColor = Color.Magenta;
        this.TransparencyKey = Color.Magenta;
        this.DoubleBuffered = true;
        Form? parentForm = canvasControl.FindForm();
        if (parentForm != null)
        {
            this.Owner = parentForm;
        }
        else
        {
            this.TopMost = true;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) this.themeSubscription?.Dispose();
        base.Dispose(disposing);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT
            cp.ExStyle |= 0x00080000; // WS_EX_LAYERED
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            ImmAssociateContext(this.Handle, IntPtr.Zero);
        }
        catch {}
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_MOUSEACTIVATE = 0x0021;
        const int MA_NOACTIVATE = 3;
        const int WM_NCHITTEST = 0x0084;
        const int HTTRANSPARENT = -1;
        const int WM_SETFOCUS = 0x0007;
        const int WM_IME_SETCONTEXT = 0x0281;

        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = (IntPtr)MA_NOACTIVATE;
            return;
        }
        if (m.Msg == WM_NCHITTEST)
        {
            m.Result = (IntPtr)HTTRANSPARENT;
            return;
        }
        if (m.Msg == WM_SETFOCUS)
        {
            return;
        }
        if (m.Msg == WM_IME_SETCONTEXT)
        {
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    public bool IsOverlayVisible => this.isVisibleState;

    public void EnsureShown()
    {
        try
        {
            if (this.IsDisposed || this.canvasControl.IsDisposed || !this.canvasControl.IsHandleCreated) return;
            if (this.Owner is null)
            {
                Form? parentForm = this.canvasControl.FindForm();
                if (parentForm != null)
                {
                    this.Owner = parentForm;
                }
            }
            if (!this.IsHandleCreated)
            {
                this.CreateHandle();
            }
            if (!this.Visible)
            {
                Point screenOrigin = this.canvasControl.PointToScreen(Point.Empty);
                Rectangle rect = new Rectangle(screenOrigin, this.canvasControl.ClientSize);
                if (rect.Width <= 0) rect.Width = 100;
                if (rect.Height <= 0) rect.Height = 100;
                this.Bounds = rect;
                this.Show();
            }
        }
        catch {}
    }

        public void UpdateGuideState(
        double docW, double docH,
        double curL, double curT, double actualW, double actualH,
        bool snapL, bool snapR, bool snapCenterX,
        bool snapT, bool snapB, bool snapCenterY,
        string badgeText, PointF badgeDocPt,
        double vdX, double vdY, double vdW, double vdH,
        double vsX, double vsY, double vsW, double vsH,
        Control canvas,
        bool isScaleSnapped = false)
    {
        this.docW = docW;
        this.docH = docH;
        this.curL = curL;
        this.curT = curT;
        this.curR = curL + actualW;
        this.curB = curT + actualH;
        this.snapL = snapL;
        this.snapR = snapR;
        this.snapCenterX = snapCenterX;
        this.snapT = snapT;
        this.snapB = snapB;
        this.snapCenterY = snapCenterY;
        this.snapBadgeText = badgeText;
        this.snapBadgeDocPt = badgeDocPt;
        this.vdX = vdX;
        this.vdY = vdY;
        this.vdW = vdW;
        this.vdH = vdH;
        this.vsX = vsX;
        this.vsY = vsY;
        this.vsW = vsW;
        this.vsH = vsH;
        this.isScaleSnapped = isScaleSnapped;

        if (canvas.IsDisposed || !canvas.IsHandleCreated)
        {
            return;
        }

        this.EnsureShown();

        Point screenOrigin = canvas.PointToScreen(Point.Empty);
        Rectangle expectedBounds = new Rectangle(screenOrigin, canvas.ClientSize);

        if (this.Bounds != expectedBounds && expectedBounds.Width > 0 && expectedBounds.Height > 0)
        {
            this.Bounds = expectedBounds;
        }

        // 附着在宿主 canvas 窗口之上，不重复调用 SetWindowPos(HWND_TOPMOST) 避免 Direct2D 画面闪烁

        this.isVisibleState = true;
        this.Invalidate();
    }

    public void HideOverlay()
    {
        // 关键：永远不调用 this.Hide()！
        // 频繁 Show/Hide 顶层 Form 会导致 Windows TSF / IME 输入语言频繁在窗口之间重置与切换，造成“疯狂切换输入语言”。
        // 保持 Form 窗口句柄常驻显示，仅将 isVisibleState 设为 false，OnPaint 不画任何内容，
        // 依赖 BackColor=Magenta 和 TransparencyKey=Magenta 使全窗体保持 100% 纯透明无感穿透！
        if (this.isVisibleState)
        {
            this.isVisibleState = false;
            this.Invalidate();
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // 必须以 Magenta 填充背景，使 TransparencyKey 生效并将全层抠成 100% 绝对纯透明！
        e.Graphics.Clear(Color.Magenta);
    }

    private PointF DocToClient(double dx, double dy)
    {
        double screenX = this.vsX + ((dx - this.vdX) * (this.vsW / this.vdW));
        double screenY = this.vsY + ((dy - this.vdY) * (this.vsH / this.vdH));
        Point screenPt = new Point((int)Math.Round(screenX), (int)Math.Round(screenY));
        Point clientPt = this.PointToClient(screenPt);
        return new PointF(clientPt.X, clientPt.Y);
    }

        protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        if (!this.isVisibleState || this.docW <= 0 || this.docH <= 0 || this.vdW <= 0 || this.vdH <= 0)
        {
            return;
        }

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        PointF pTL = this.DocToClient(0, 0);
        PointF pTR = this.DocToClient(this.docW, 0);
        PointF pBL = this.DocToClient(0, this.docH);
        PointF pBR = this.DocToClient(this.docW, this.docH);

        PointF imgTL = this.DocToClient(this.curL, this.curT);
        PointF imgTR = this.DocToClient(this.curR, this.curT);
        PointF imgBL = this.DocToClient(this.curL, this.curB);
        PointF imgBR = this.DocToClient(this.curR, this.curB);

        var canvas = RectangleF.FromLTRB(pTL.X, pTL.Y, pBR.X, pBR.Y);
        var image = RectangleF.FromLTRB(Math.Min(imgTL.X, imgBR.X), Math.Min(imgTL.Y, imgBR.Y),
            Math.Max(imgTL.X, imgBR.X), Math.Max(imgTL.Y, imgBR.Y));
        string label = this.isScaleSnapped
            ? $"{Math.Round(this.curR - this.curL):0} × {Math.Round(this.curB - this.curT):0} px · 尺寸吸附"
            : this.snapBadgeText.Replace("吸附:", "").Replace("对齐:", "").Replace(" (0, 0)", "").Trim();
        GuidePainter.Draw(g, this.ClientSize, UiStyle.Current(this.canvasControl), canvas, image,
            this.snapL, this.snapR, this.snapCenterX, this.snapT, this.snapB, this.snapCenterY,
            this.isScaleSnapped, label, this.DocToClient(this.snapBadgeDocPt.X, this.snapBadgeDocPt.Y));
    }
}
