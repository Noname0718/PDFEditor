using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Documents;

namespace PDFEditor.Shapes
{
    public class SelectionToolManager
    {
        private class SelectionState
        {
            public UIElement SelectedElement;
            public bool IsDragging;
            public Point LastMousePosition;

            public AdornerLayer AdornerLayer;
            public SelectionAdorner Adorner;
        }

        private readonly Dictionary<InkCanvas, SelectionState> _states
            = new Dictionary<InkCanvas, SelectionState>();

        // 전체 선택 도구 on/off
        private bool _enabled = false;

        public void AttachCanvas(InkCanvas canvas)
        {
            if (canvas == null || _states.ContainsKey(canvas))
                return;

            var state = new SelectionState();
            state.AdornerLayer = AdornerLayer.GetAdornerLayer(canvas);

            _states[canvas] = state;

            canvas.PreviewMouseLeftButtonDown += Canvas_PreviewMouseLeftButtonDown;
            canvas.PreviewMouseMove += Canvas_PreviewMouseMove;
            canvas.PreviewMouseLeftButtonUp += Canvas_PreviewMouseLeftButtonUp;
        }

        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;

            foreach (var kv in _states)
            {
                var state = kv.Value;

                state.IsDragging = false;

                if (!enabled)
                {
                    // 선택 해제 + Adorner 제거
                    if (state.AdornerLayer != null && state.Adorner != null)
                    {
                        state.AdornerLayer.Remove(state.Adorner);
                        state.Adorner = null;
                    }
                    state.SelectedElement = null;
                }
            }
        }


        private void Canvas_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_enabled) return;

            var canvas = (InkCanvas)sender;
            var state = _states[canvas];

            Point pos = e.GetPosition(canvas);

            var element = FindSelectableElement(canvas, pos);

            if (element != null)
            {
                SetSelection(state, element);
                state.IsDragging = true;
                state.LastMousePosition = pos;

                if (canvas.Children.Contains(element))
                {
                    canvas.Children.Remove(element);
                    canvas.Children.Add(element);
                }

                e.Handled = true;
            }
            else
            {
                ClearSelectionInternal(state);
            }
        }

        private void SetSelection(SelectionState state, UIElement element)
        {
            if (state.SelectedElement == element)
                return;

            // 기존 Adorner 제거
            if (state.AdornerLayer != null && state.Adorner != null)
            {
                state.AdornerLayer.Remove(state.Adorner);
                state.Adorner = null;
            }

            state.SelectedElement = element;

            if (state.AdornerLayer != null && element is FrameworkElement fe)
            {
                state.Adorner = new SelectionAdorner(fe);
                state.AdornerLayer.Add(state.Adorner);
            }
        }

        private void ClearSelectionInternal(SelectionState state)
        {
            state.IsDragging = false;

            if (state.AdornerLayer != null && state.Adorner != null)
            {
                state.AdornerLayer.Remove(state.Adorner);
                state.Adorner = null;
            }

            state.SelectedElement = null;
        }


        private void Canvas_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_enabled) return;

            var canvas = (InkCanvas)sender;
            var state = _states[canvas];

            if (!state.IsDragging || state.SelectedElement == null)
                return;

            if (e.LeftButton != MouseButtonState.Pressed)
                return;

            Point pos = e.GetPosition(canvas);
            Vector delta = pos - state.LastMousePosition;
            if (delta.X == 0 && delta.Y == 0)
                return;

            MoveElement(state.SelectedElement, delta);

            state.LastMousePosition = pos;

            if (state.Adorner != null)
            {
                state.Adorner.InvalidateArrange();
                state.Adorner.InvalidateVisual();
            }

            e.Handled = true;
        }

        private void Canvas_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_enabled) return;

            var canvas = (InkCanvas)sender;
            var state = _states[canvas];

            if (!state.IsDragging) return;

            state.IsDragging = false;
            // 선택 상태는 유지 (원하면 여기서 SelectedElement도 null로)
            e.Handled = true;
        }

        private UIElement FindSelectableElement(InkCanvas canvas, Point position)
        {
            for (int i = canvas.Children.Count - 1; i >= 0; i--)
            {
                if (canvas.Children[i] is UIElement element)
                {
                    if (IsPointOverElement(canvas, element, position))
                        return element;
                }
            }

            return null;
        }

        private bool IsPointOverElement(InkCanvas canvas, UIElement element, Point canvasPoint)
        {
            GeneralTransform transform = element.TransformToVisual(canvas);
            if (transform == null || !transform.Inverse.TryTransform(canvasPoint, out Point localPoint))
                return false;

            Rect bounds = VisualTreeHelper.GetDescendantBounds(element);
            return bounds.Contains(localPoint);
        }

        private void MoveElement(UIElement element, Vector delta)
        {
            // 기본: Canvas.Left/Top 사용 가능한 애들 (Rectangle, Ellipse, Image, TextBox 등)
            if (element is Line line)
            {
                line.X1 += delta.X;
                line.X2 += delta.X;
                line.Y1 += delta.Y;
                line.Y2 += delta.Y;
            }
            else if (element is Polygon polygon)
            {
                var pts = polygon.Points;
                for (int i = 0; i < pts.Count; i++)
                {
                    pts[i] = new Point(pts[i].X + delta.X, pts[i].Y + delta.Y);
                }
            }
            else
            {
                double left = InkCanvas.GetLeft(element);
                double top = InkCanvas.GetTop(element);

                if (double.IsNaN(left)) left = 0;
                if (double.IsNaN(top)) top = 0;

                InkCanvas.SetLeft(element, left + delta.X);
                InkCanvas.SetTop(element, top + delta.Y);
            }
        }

        public UIElement GetSelectedElement(InkCanvas canvas)
        {
            if (canvas == null) return null;
            SelectionState state;
            if (_states.TryGetValue(canvas, out state))
            {
                return state.SelectedElement;
            }
            return null;
        }

        public void SelectElement(InkCanvas canvas, UIElement element)
        {
            if (canvas == null) return;
            SelectionState state;
            if (_states.TryGetValue(canvas, out state))
            {
                SetSelection(state, element);
            }
        }

        public void ClearSelection(InkCanvas canvas)
        {
            if (canvas == null) return;
            SelectionState state;
            if (_states.TryGetValue(canvas, out state))
            {
                ClearSelectionInternal(state);
            }
        }
    }
}
