using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace PDFEditor.Ink
{
    /// <summary>
    /// 펜/형광펜 입력 시 InkCanvas 위에 명확한 커서 원을 표시한다.
    /// </summary>
    public class PenCursorManager
    {
        private class Entry
        {
            public InkCanvas Canvas;
            public PenCursorAdorner Adorner;
            public AdornerLayer Layer;
        }

        private readonly Dictionary<InkCanvas, Entry> _entries = new Dictionary<InkCanvas, Entry>();
        private double _thickness = 3.0;
        private bool _enabled = true;

        public enum CursorMode
        {
            Pen,
            Eraser,
            Hidden
        }

        private CursorMode _mode = CursorMode.Pen;
        private Cursor _fallbackCursor = Cursors.Arrow;

        /// <summary>
        /// InkCanvas가 생성될 때까지 기다렸다가 PenCursorAdorner를 붙이고 마우스/스타일러스 이벤트를 감시한다.
        /// </summary>
        public void AttachCanvas(InkCanvas canvas)
        {
            if (canvas == null || _entries.ContainsKey(canvas))
                return;

            RoutedEventHandler loadedHandler = null;
            loadedHandler = (sender, args) =>
            {
                canvas.Loaded -= loadedHandler;
                InitializeCanvasEntry(canvas);
            };

            if (canvas.IsLoaded)
            {
                InitializeCanvasEntry(canvas);
            }
            else
            {
                canvas.Loaded += loadedHandler;
            }
        }

        /// <summary>
        /// 커서 표시 기능을 켜거나 끈다. 끄면 모든 Adorner를 숨기고 기본 커서를 되돌린다.
        /// </summary>
        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            foreach (Entry entry in _entries.Values)
            {
                if (!enabled)
                {
                    entry.Adorner.Hide();
                }

                UpdateCursorState(entry);
            }
        }

        /// <summary>
        /// 펜 두께가 바뀌었을 때 커서 미리보기의 직경을 즉시 갱신한다.
        /// </summary>
        public void SetThickness(double thickness)
        {
            _thickness = thickness;
            foreach (Entry entry in _entries.Values)
            {
                entry.Adorner.UpdateThickness(thickness);
            }
        }

        /// <summary>
        /// 모든 InkCanvas에서 이벤트 핸들러와 Adorner를 제거한다.
        /// </summary>
        public void Clear()
        {
            foreach (Entry entry in _entries.Values)
            {
                entry.Canvas.MouseEnter -= Canvas_MouseEnter;
                entry.Canvas.MouseMove -= Canvas_MouseMove;
                entry.Canvas.MouseLeave -= Canvas_MouseLeave;

                entry.Canvas.StylusEnter -= Canvas_StylusEnter;
                entry.Canvas.StylusMove -= Canvas_StylusMove;
                entry.Canvas.StylusLeave -= Canvas_StylusLeave;

                entry.Layer.Remove(entry.Adorner);
                entry.Canvas.UseCustomCursor = false;
                entry.Canvas.ClearValue(InkCanvas.CursorProperty);
            }

            _entries.Clear();
        }

        /// <summary>
        /// 커서 모드를 펜/지우개/숨김 중 하나로 전환한다.
        /// fallbackCursor는 숨김 모드일 때 사용할 시스템 커서다.
        /// </summary>
        public void SetMode(CursorMode mode, Cursor fallbackCursor = null)
        {
            _mode = mode;
            _fallbackCursor = fallbackCursor ?? Cursors.Arrow;

            foreach (Entry entry in _entries.Values)
            {
                UpdateCursorState(entry);
            }
        }

        /// <summary>
        /// 마우스가 InkCanvas 위에 들어올 때 커서 위치를 업데이트한다.
        /// </summary>
        private void Canvas_MouseEnter(object sender, MouseEventArgs e)
        {
            if (!_enabled || _mode == CursorMode.Hidden) return;
            InkCanvas canvas = sender as InkCanvas;
            Entry entry;
            if (canvas != null && _entries.TryGetValue(canvas, out entry))
            {
                entry.Adorner.UpdatePosition(e.GetPosition(canvas));
            }
        }

        /// <summary>
        /// 이동 중에도 커서 위치를 계속 맞춰준다.
        /// </summary>
        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_enabled || _mode == CursorMode.Hidden) return;
            InkCanvas canvas = sender as InkCanvas;
            Entry entry;
            if (canvas != null && _entries.TryGetValue(canvas, out entry))
            {
                entry.Adorner.UpdatePosition(e.GetPosition(canvas));
            }
        }

        /// <summary>
        /// 마우스가 InkCanvas 밖으로 나가면 미리보기 원을 숨긴다.
        /// </summary>
        private void Canvas_MouseLeave(object sender, MouseEventArgs e)
        {
            InkCanvas canvas = sender as InkCanvas;
            Entry entry;
            if (canvas != null && _entries.TryGetValue(canvas, out entry))
            {
                entry.Adorner.Hide();
            }
        }

        /// <summary>
        /// 스타일러스 입력도 동일하게 처리한다.
        /// </summary>
        private void Canvas_StylusEnter(object sender, StylusEventArgs e)
        {
            if (!_enabled || _mode == CursorMode.Hidden) return;
            InkCanvas canvas = sender as InkCanvas;
            Entry entry;
            if (canvas != null && _entries.TryGetValue(canvas, out entry))
            {
                entry.Adorner.UpdatePosition(e.GetPosition(canvas));
            }
        }

        /// <summary>
        /// 스타일러스 이동 이벤트 처리기.
        /// </summary>
        private void Canvas_StylusMove(object sender, StylusEventArgs e)
        {
            if (!_enabled || _mode == CursorMode.Hidden) return;
            InkCanvas canvas = sender as InkCanvas;
            Entry entry;
            if (canvas != null && _entries.TryGetValue(canvas, out entry))
            {
                entry.Adorner.UpdatePosition(e.GetPosition(canvas));
            }
        }

        /// <summary>
        /// 스타일러스가 나갈 때도 Adorner를 숨긴다.
        /// </summary>
        private void Canvas_StylusLeave(object sender, StylusEventArgs e)
        {
            InkCanvas canvas = sender as InkCanvas;
            Entry entry;
            if (canvas != null && _entries.TryGetValue(canvas, out entry))
            {
                entry.Adorner.Hide();
            }
        }

        /// <summary>
        /// 현재 모드/활성화 상태에 따라 InkCanvas.Cursor와 Adorner 시각을 조정한다.
        /// </summary>
        private void UpdateCursorState(Entry entry)
        {
            var canvas = entry.Canvas;
            if (_mode == CursorMode.Hidden || !_enabled)
            {
                entry.Adorner.Hide();
                canvas.UseCustomCursor = false;
                canvas.Cursor = _fallbackCursor ?? Cursors.Arrow;
            }
            else
            {
                canvas.UseCustomCursor = true;
                canvas.Cursor = Cursors.None;
                entry.Adorner.SetVisual(_mode == CursorMode.Eraser
                    ? PenCursorAdorner.CursorVisual.Eraser
                    : PenCursorAdorner.CursorVisual.Pen);
            }
        }

        /// <summary>
        /// InkCanvas마다 AdornerLayer를 찾아 PenCursorAdorner를 추가하고 이벤트를 연결한다.
        /// </summary>
        private void InitializeCanvasEntry(InkCanvas canvas)
        {
            if (canvas == null)
                return;

            AdornerLayer layer = AdornerLayer.GetAdornerLayer(canvas);
            if (layer == null)
                return;

            var adorner = new PenCursorAdorner(canvas);
            adorner.UpdateThickness(_thickness);
            if (!_enabled) adorner.Hide();

            layer.Add(adorner);

            var entry = new Entry
            {
                Canvas = canvas,
                Adorner = adorner,
                Layer = layer
            };

            _entries[canvas] = entry;

            canvas.MouseEnter += Canvas_MouseEnter;
            canvas.MouseMove += Canvas_MouseMove;
            canvas.MouseLeave += Canvas_MouseLeave;

            canvas.StylusEnter += Canvas_StylusEnter;
            canvas.StylusMove += Canvas_StylusMove;
            canvas.StylusLeave += Canvas_StylusLeave;

            UpdateCursorState(entry);
        }
    }
}
