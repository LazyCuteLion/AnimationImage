using SkiaSharp;
using System;

namespace AnimationImage.Apng
{
    /// <summary>
    /// APNG 主画布合成器：将解码后的子帧按 <see cref="ApngDisposeOp"/> / <see cref="ApngBlendOp"/> 应用到主画布。
    /// 规范流程：先处理上一帧的 dispose_op（清区域 / 恢复快照），再按 blend_op 混合当前子帧。
    /// </summary>
    internal sealed class ApngCompositor : IDisposable
    {
        private readonly SKBitmap _canvas;
        private readonly SKCanvas _canvasWrap;               // 长期持有，复用避免每帧 new
        private readonly SKPaint _srcPaint = new() { BlendMode = SKBlendMode.Src };
        private readonly SKPaint _srcOverPaint = new() { BlendMode = SKBlendMode.SrcOver };
        private SKBitmap? _snapshot;                         // 仅在遇到 dispose=Previous 时按需分配
        private SKCanvas? _snapshotWrap;                     // 与 _snapshot 生命周期同步
        private ApngDisposeOp _prevDispose;
        private SKRectI _prevRect;
        private bool _hasPrev;

        public int Width { get; }
        public int Height { get; }

        /// <summary>主画布：BGRA Premul，与 <see cref="AnimatableBitmap"/> 输出格式一致。</summary>
        public SKBitmap Canvas => _canvas;

        public ApngCompositor(int width, int height)
        {
            Width = width;
            Height = height;
            _canvas = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            _canvas.Erase(SKColors.Transparent);
            _canvasWrap = new SKCanvas(_canvas);
        }

        /// <summary>把已解码的子帧应用到主画布；返回值即 <see cref="Canvas"/>。</summary>
        public SKBitmap Compose(SKBitmap subFrame, ApngFrameEntry frame)
        {
            ApplyPrevDispose();

            var rect = new SKRectI(
                frame.OffsetX,
                frame.OffsetY,
                frame.OffsetX + frame.Width,
                frame.OffsetY + frame.Height);

            // 若「本帧」dispose 是 Previous，需在应用本帧前保存画布快照
            if (frame.DisposeOp == ApngDisposeOp.Previous)
                SaveSnapshot(rect);

            // 按 blend_op 把子帧绘制到画布上（复用池化 SKPaint / SKCanvas）
            var paint = frame.BlendOp == ApngBlendOp.Source ? _srcPaint : _srcOverPaint;
            _canvasWrap.DrawBitmap(subFrame, frame.OffsetX, frame.OffsetY, paint);

            _prevDispose = frame.DisposeOp;
            _prevRect = rect;
            _hasPrev = true;
            return _canvas;
        }

        /// <summary>
        /// 快捷路径提交：解码器已经直接把像素解到主画布（全覆盖 + Source blend 场景），<br/>
        /// 此方法仅推进 dispose 状态，跳过 sub→canvas 的 DrawBitmap。<br/>
        /// 调用前置条件由 <see cref="ApngCodec"/> 保证：dispose=None + blend=Source + 子帧铺满整幅画布。
        /// </summary>
        public void MarkComposed(ApngFrameEntry frame)
        {
            // 全覆盖 + Source 会彻底替换上一帧内容，无需再 ApplyPrevDispose
            _prevDispose = frame.DisposeOp;
            _prevRect = new SKRectI(
                frame.OffsetX,
                frame.OffsetY,
                frame.OffsetX + frame.Width,
                frame.OffsetY + frame.Height);
            _hasPrev = true;
        }

        /// <summary>播放循环回到首帧时调用，重置画布与合成状态。</summary>
        public void Reset()
        {
            _canvas.Erase(SKColors.Transparent);
            _hasPrev = false;
            _prevDispose = ApngDisposeOp.None;
            _prevRect = default;
        }

        private void ApplyPrevDispose()
        {
            if (!_hasPrev) return;
            switch (_prevDispose)
            {
                case ApngDisposeOp.None:
                    break;
                case ApngDisposeOp.Background:
                    ClearRect(_prevRect);
                    break;
                case ApngDisposeOp.Previous:
                    if (_snapshot != null)
                        RestoreSnapshot(_prevRect);
                    else
                        ClearRect(_prevRect); // 无可回滚快照（如首帧即 Previous）→ 按 Background 处理
                    break;
            }
        }

        private void ClearRect(SKRectI rect)
        {
            int state = _canvasWrap.Save();
            _canvasWrap.ClipRect(SKRect.Create(rect.Left, rect.Top, rect.Width, rect.Height));
            _canvasWrap.Clear(SKColors.Transparent);
            _canvasWrap.RestoreToCount(state);
        }

        private void SaveSnapshot(SKRectI rect)
        {
            if (_snapshot == null)
            {
                _snapshot = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
                _snapshotWrap = new SKCanvas(_snapshot);
            }
            _snapshotWrap!.Clear(SKColors.Transparent);
            // 仅复制受影响矩形即可，其他区域下次 Restore 时不会用到
            _snapshotWrap.DrawBitmap(_canvas, rect, rect, _srcPaint);
        }

        private void RestoreSnapshot(SKRectI rect)
        {
            if (_snapshot == null) return;
            _canvasWrap.DrawBitmap(_snapshot, rect, rect, _srcPaint);
        }

        public void Dispose()
        {
            _canvasWrap.Dispose();
            _canvas.Dispose();
            _snapshotWrap?.Dispose();
            _snapshot?.Dispose();
            _srcPaint.Dispose();
            _srcOverPaint.Dispose();
        }
    }
}
