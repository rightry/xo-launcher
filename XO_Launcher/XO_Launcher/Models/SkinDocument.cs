using System;

namespace XO_Launcher.Models
{
    /// <summary>
    /// 皮膚文檔：把「像素 + 歷史 + 髒標記」綁成一個單元。
    /// 原先這些狀態散落在 <c>SkinsPage.xaml.cs</c> 的四五個欄位裡
    /// （_canvas / _history / _dirty），換皮膚、導入、清空、撤銷時很容易漏清其中一個。
    /// 現在文檔是唯一真相：任何改動都走文檔的方法，文檔負責推歷史與標髒。
    /// 純邏輯，不依賴 WinUI，可單元測試。
    /// </summary>
    public sealed class SkinDocument
    {
        private readonly SkinCanvas _canvas;
        private readonly SkinEditHistory _history;

        public SkinDocument(int maxHistory = 100)
        {
            _canvas = new SkinCanvas();
            _history = new SkinEditHistory(_canvas, maxHistory);
        }

        public SkinCanvas Canvas => _canvas;

        public SkinEditHistory History => _history;

        public byte[] Pixels => _canvas.Pixels;

        public bool IsSlim
        {
            get => _canvas.IsSlim;
            set => _canvas.IsSlim = value;
        }

        /// <summary>自上次載入 / 保存後是否有未落盤改動。</summary>
        public bool IsDirty { get; private set; }

        public bool CanUndo => _history.CanUndo;

        public bool CanRedo => _history.CanRedo;

        public event Action? Changed;

        public void LoadPixels(byte[] bgra, bool resetDirty = true)
        {
            _canvas.LoadPixels(bgra);
            _history.Reset();
            if (resetDirty) IsDirty = false;
            Changed?.Invoke();
        }

        public void LoadEmpty()
        {
            LoadPixels(new byte[SkinCanvas.ByteCount]);
        }

        /// <summary>一次用戶手勢開始前呼叫：留快照並清空重做分支。</summary>
        public void BeginStroke()
        {
            _history.PushSnapshot();
        }

        public void DrawLine(int x0, int y0, int x1, int y1,
            byte b, byte g, byte r, byte a, SkinRegion scope, bool mirrorX)
        {
            _canvas.DrawLine(x0, y0, x1, y1, b, g, r, a, scope, mirrorX);
            IsDirty = true;
        }

        /// <summary>
        /// 帶筆刷粗細的一筆（含拖動連線）。以落點為中心鋪開 brushSize×brushSize，
        /// 逐偏移走 DrawLine，從而複用同一套圖層範圍 + 鏡像邏輯。
        /// </summary>
        public void StrokeSegment(int x0, int y0, int x1, int y1,
            byte b, byte g, byte r, byte a, SkinRegion scope, bool mirrorX, int brushSize)
        {
            brushSize = Math.Clamp(brushSize, 1, 4);
            int offset = -(brushSize / 2);
            for (int oy = 0; oy < brushSize; oy++)
                for (int ox = 0; ox < brushSize; ox++)
                    _canvas.DrawLine(x0 + offset + ox, y0 + offset + oy,
                        x1 + offset + ox, y1 + offset + oy, b, g, r, a, scope, mirrorX);
            IsDirty = true;
        }

        /// <summary>矩形邊框（空心），線寬 = brushSize。右鍵 / 橡皮時傳 a=0 即為擦除框。</summary>
        public void StrokeRect(int x0, int y0, int x1, int y1,
            byte b, byte g, byte r, byte a, SkinRegion scope, bool mirrorX, int brushSize)
        {
            StrokeSegment(x0, y0, x1, y0, b, g, r, a, scope, mirrorX, brushSize);
            StrokeSegment(x1, y0, x1, y1, b, g, r, a, scope, mirrorX, brushSize);
            StrokeSegment(x1, y1, x0, y1, b, g, r, a, scope, mirrorX, brushSize);
            StrokeSegment(x0, y1, x0, y0, b, g, r, a, scope, mirrorX, brushSize);
        }

        /// <summary>明暗筆：沿線把每個命中像素的 RGB 乘上 factor（&lt;1 加深、&gt;1 變亮），Alpha 不動。</summary>
        public void ShadeSegment(int x0, int y0, int x1, int y1,
            double factor, SkinRegion scope, bool mirrorX, int brushSize)
        {
            brushSize = Math.Clamp(brushSize, 1, 4);
            int offset = -(brushSize / 2);
            for (int oy = 0; oy < brushSize; oy++)
                for (int ox = 0; ox < brushSize; ox++)
                    ShadeLine(x0 + offset + ox, y0 + offset + oy,
                        x1 + offset + ox, y1 + offset + oy, factor, scope, mirrorX);
            IsDirty = true;
        }

        private void ShadeLine(int x0, int y0, int x1, int y1, double factor, SkinRegion scope, bool mirrorX)
        {
            int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;
            while (true)
            {
                ShadePoint(x0, y0, factor, scope, mirrorX);
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x0 += sx; }
                if (e2 < dx) { err += dx; y0 += sy; }
            }
        }

        private void ShadePoint(int x, int y, double factor, SkinRegion scope, bool mirrorX)
        {
            ShadeOne(x, y, factor, scope);
            if (mirrorX && SkinCanvas.MirrorPointAt(x, y) is (int mx, int my))
                ShadeOne(mx, my, factor, scope);
        }

        private void ShadeOne(int x, int y, double factor, SkinRegion scope)
        {
            if (!SkinCanvas.InBounds(x, y)) return;
            if (scope != SkinRegion.Unused && SkinCanvas.RegionAt(x, y) != scope) return;
            _canvas.GetPixel(x, y, out byte b, out byte g, out byte r, out byte a);
            if (a == 0) return;
            _canvas.SetPixel(x, y,
                (byte)Math.Clamp(b * factor, 0, 255),
                (byte)Math.Clamp(g * factor, 0, 255),
                (byte)Math.Clamp(r * factor, 0, 255), a);
        }

        /// <summary>
        /// 只填落點所在的單個立方體面。裁剪框取自 <see cref="SkinCanvas.FaceBoundsAt"/>，
        /// 呼叫方不用再記得這件事 —— 原先 SkinsPage.Stroke 裡要手寫一次，容易漏。
        /// </summary>
        public bool FloodFillFace(int x, int y, byte b, byte g, byte r, byte a, SkinRegion scope, bool mirrorX)
        {
            var clip = SkinCanvas.FaceBoundsAt(x, y);
            bool changed = _canvas.FloodFill(x, y, b, g, r, a, 0, scope, mirrorX,
                clip.X1, clip.Y1, clip.X2, clip.Y2);
            if (changed) IsDirty = true;
            return changed;
        }

        public void ClearLayer(SkinRegion layer)
        {
            _history.PushSnapshot();
            _canvas.ClearRegion(layer, 0, 0, 0, 0);
            IsDirty = true;
            Changed?.Invoke();
        }

        /// <summary>實心矩形填充（含筆刷範圍裁剪與圖層 / 鏡像過濾），供矩形工具使用。</summary>
        public void FillRect(int x0, int y0, int x1, int y1,
            byte b, byte g, byte r, byte a, SkinRegion scope, bool mirrorX)
        {
            int xa = Math.Min(x0, x1), xb = Math.Max(x0, x1);
            int ya = Math.Min(y0, y1), yb = Math.Max(y0, y1);
            for (int y = ya; y <= yb; y++)
                for (int x = xa; x <= xb; x++)
                    Plot(x, y, b, g, r, a, scope, mirrorX);
            IsDirty = true;
        }

        private void Plot(int x, int y, byte b, byte g, byte r, byte a, SkinRegion scope, bool mirrorX)
        {
            if (SkinCanvas.InBounds(x, y) && (scope == SkinRegion.Unused || SkinCanvas.RegionAt(x, y) == scope))
                _canvas.SetPixel(x, y, b, g, r, a);
            if (mirrorX && SkinCanvas.MirrorPointAt(x, y) is (int mx, int my))
            {
                if (SkinCanvas.InBounds(mx, my) && (scope == SkinRegion.Unused || SkinCanvas.RegionAt(mx, my) == scope))
                    _canvas.SetPixel(mx, my, b, g, r, a);
            }
        }

        public bool Undo()
        {
            if (!_history.Undo()) return false;
            IsDirty = true;
            Changed?.Invoke();
            return true;
        }

        public bool Redo()
        {
            if (!_history.Redo()) return false;
            IsDirty = true;
            Changed?.Invoke();
            return true;
        }

        public void MarkSaved()
        {
            IsDirty = false;
            Changed?.Invoke();
        }

        public void MarkDirty()
        {
            IsDirty = true;
            Changed?.Invoke();
        }
    }
}
