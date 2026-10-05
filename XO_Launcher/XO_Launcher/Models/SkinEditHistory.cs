using System;
using System.Collections.Generic;

namespace XO_Launcher.Models
{
    /// <summary>
    /// 基于快照的撤销/重做历史，作用于一张 <see cref="SkinCanvas"/>。
    /// 模型：_past 是「可回退到的状态」栈，_future 是「可重做的状态」栈。
    /// 用法：每次「用户操作」开始前调用 <see cref="PushSnapshot"/> 记录当前画布，
    /// 随后直接修改画布；<see cref="Undo"/> 回退到最近一次快照并把被撤销的画布存入 _future 供重做。
    /// 不依赖任何 WinUI / WIC / Windows.* 类型。
    /// </summary>
    public sealed class SkinEditHistory
    {
        private readonly SkinCanvas _canvas;
        private readonly int _maxSteps;
        private readonly List<byte[]> _past = new List<byte[]>();      // 可撤销到的状态
        private readonly Stack<byte[]> _future = new Stack<byte[]>();  // 可重做的状态

        /// <summary>构造历史。maxSteps 为可保留的最大步数（含当前），至少为 1。</summary>
        public SkinEditHistory(SkinCanvas canvas, int maxSteps = 100)
        {
            _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
            if (maxSteps < 1) throw new ArgumentOutOfRangeException(nameof(maxSteps), "maxSteps 必须 ≥ 1。");
            _maxSteps = maxSteps;
        }

        /// <summary>是否还可以撤销。</summary>
        public bool CanUndo => _past.Count > 0;

        /// <summary>是否还可以重做。</summary>
        public bool CanRedo => _future.Count > 0;

        /// <summary>在每一次「用户操作」开始前调用，记录当前画布状态，并清空重做分支。</summary>
        public void PushSnapshot()
        {
            _future.Clear();
            _past.Add(_canvas.ClonePixels());
            // 超过上限时淘汰最旧的一步
            while (_past.Count > _maxSteps)
                _past.RemoveAt(0);
            Changed?.Invoke();
        }

        /// <summary>撤销一步：恢复到上一次快照，返回是否真的撤销了。到底（无可撤销）时返回 false 且不改动画布。</summary>
        public bool Undo()
        {
            if (_past.Count == 0) return false;
            // 先保存当前画布（被撤销的状态），供 Redo 恢复
            _future.Push(_canvas.ClonePixels());
            _canvas.LoadPixels(_past[_past.Count - 1]);
            _past.RemoveAt(_past.Count - 1);
            Changed?.Invoke();
            return true;
        }

        /// <summary>重做一步：恢复最近一次被撤销的状态，返回是否真的重做了。无可重做时返回 false 且不改动画布。</summary>
        public bool Redo()
        {
            if (_future.Count == 0) return false;
            _past.Add(_canvas.ClonePixels());
            // 超出上限时同样淘汰最旧的一步
            while (_past.Count > _maxSteps)
                _past.RemoveAt(0);
            _canvas.LoadPixels(_future.Pop());
            Changed?.Invoke();
            return true;
        }

        /// <summary>清空历史（换皮肤时用），同时清空撤销与重做分支。</summary>
        public void Reset()
        {
            _past.Clear();
            _future.Clear();
            Changed?.Invoke();
        }

        /// <summary>Undo / Redo / PushSnapshot / Reset 之后触发，供界面刷新按钮可用态。</summary>
        public event Action? Changed;
    }
}
