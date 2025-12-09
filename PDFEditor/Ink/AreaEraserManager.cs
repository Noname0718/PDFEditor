using PDFEditor.Shapes;
using PDFEditor.Text;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PDFEditor.Ink
{
    /// <summary>
    /// 펜/형광펜 지우개 외에 텍스트/도형 요소를 한 번에 삭제하기 위한 간단한 영역 지우개.
    /// </summary>
    public class AreaEraserManager
    {
        public event Action<InkCanvas, UIElement> ElementErased;

        private readonly Dictionary<InkCanvas, CanvasState> _states = new Dictionary<InkCanvas, CanvasState>();
        private bool _enabled = false;
        private double _radius = 12;

        private class CanvasState
        {
            public InkCanvas Canvas;
            public bool IsErasing;
        }

        public void AttachCanvas(InkCanvas canvas)
        {
            if (canvas == null || _states.ContainsKey(canvas))
                return;

            var state = new CanvasState { Canvas = canvas };
            _states[canvas] = state;

            canvas.PreviewMouseLeftButtonDown += Canvas_PreviewMouseLeftButtonDown;
            canvas.PreviewMouseMove += Canvas_PreviewMouseMove;
            canvas.PreviewMouseLeftButtonUp += Canvas_PreviewMouseLeftButtonUp;
            canvas.MouseLeave += Canvas_MouseLeave;
        }

        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;

            if (!enabled)
            {
                foreach (var state in _states.Values)
                {
                    if (state.IsErasing)
                    {
                        state.Canvas.ReleaseMouseCapture();
                    }
                    state.IsErasing = false;
                }
            }
        }

        public void SetRadius(double radius)
        {
            _radius = Math.Max(4, radius);
        }

        private void Canvas_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!_enabled)
                return;

            if (!(sender is InkCanvas canvas) || !_states.TryGetValue(canvas, out CanvasState state))
                return;

            state.IsErasing = true;
            canvas.CaptureMouse();
            EraseAt(state, e.GetPosition(canvas));
        }

        private void Canvas_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!(sender is InkCanvas canvas) || !_states.TryGetValue(canvas, out CanvasState state))
                return;

            if (!_enabled || !state.IsErasing)
                return;

            if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
            {
                StopErasing(state);
                return;
            }

            EraseAt(state, e.GetPosition(canvas));
        }

        private void Canvas_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!(sender is InkCanvas canvas) || !_states.TryGetValue(canvas, out CanvasState state))
                return;

            if (!state.IsErasing)
                return;

            StopErasing(state);
        }

        private void Canvas_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is InkCanvas canvas && _states.TryGetValue(canvas, out CanvasState state))
            {
                StopErasing(state);
            }
        }

        private void StopErasing(CanvasState state)
        {
            if (state == null || !state.IsErasing)
                return;

            state.IsErasing = false;
            state.Canvas?.ReleaseMouseCapture();
        }

        private void EraseAt(CanvasState state, Point canvasPoint)
        {
            if (state?.Canvas == null)
                return;

            double diameter = _radius;
            Rect eraseRect = new Rect(canvasPoint.X - diameter / 2, canvasPoint.Y - diameter / 2, diameter, diameter);
            var targets = new List<UIElement>();

            foreach (UIElement child in state.Canvas.Children)
            {
                if (!IsErasableElement(child))
                    continue;

                Rect bounds = GetElementBounds(state.Canvas, child);
                if (bounds.IntersectsWith(eraseRect))
                {
                    targets.Add(child);
                }
            }

            if (targets.Count == 0)
                return;

            foreach (var element in targets)
            {
                ElementErased?.Invoke(state.Canvas, element);
            }
        }

        private bool IsErasableElement(UIElement element)
        {
            if (element is TextBox tb)
            {
                return Equals(tb.Tag as string, TextToolManager.TextElementTag);
            }

            if (element is Shape shape)
            {
                return Equals(shape.Tag, ShapeToolManager.ShapeElementTag);
            }

            return false;
        }

        private Rect GetElementBounds(InkCanvas canvas, UIElement element)
        {
            Rect bounds = VisualTreeHelper.GetDescendantBounds(element);
            GeneralTransform transform = element.TransformToVisual(canvas);
            if (transform != null)
            {
                bounds = transform.TransformBounds(bounds);
            }
            return bounds;
        }
    }
}
