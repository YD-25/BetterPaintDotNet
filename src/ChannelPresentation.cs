using PaintDotNet;

namespace LayerChannelLive;

internal static class ChannelPresentation
{
    // 灰阶/通道掩码只属于显示状态。读取被屏蔽或重新排列的像素时，保留完整数据源。
    public static unsafe void MergeVisibleEdits(Surface displayed, Surface truth, IList<ChannelKind> order, bool[] visibility)
    {
        if (displayed.Width != truth.Width || displayed.Height != truth.Height) return;
        int[] offsets = { 2, 1, 0, 3 };
        for (int y = 0; y < truth.Height; y++)
        {
            byte* input = (byte*)displayed.Scan0.Pointer + (long)y * displayed.Stride;
            byte* target = (byte*)truth.Scan0.Pointer + (long)y * truth.Stride;
            for (int x = 0; x < truth.Width; x++)
            {
                // 先读齐源像素，防止输入和数据源相同或通道交叉时被本循环写入影响。
                byte* s = input + x * 4;
                byte* d = target + x * 4;
                byte r = s[2], g = s[1], b = s[0], a = s[3];
                for (int slot = 0; slot < 4; slot++)
                {
                    int kind = (int)order[slot];
                    if (!visibility[kind]) continue;
                    d[offsets[kind]] = slot switch { 0 => r, 1 => g, 2 => b, _ => a };
                }
            }
        }
    }
}
