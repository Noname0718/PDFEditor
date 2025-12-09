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

        /// <summary>
        /// InkCanvas에 Preview 이벤트를 연결하고 도구별 상태를 저장한다.
        /// </summary>
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

        /// <summary>
        /// 영역 지우개의 활성화 여부를 지정한다. 꺼질 때는 모든 캔버스의 드래그 상태를 즉시 종료한다.
        /// </summary>
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

        /// <summary>
        /// 마우스 주변을 감지할 원형 지우개 반지름을 조정한다.
        /// </summary>
        public void SetRadius(double radius)
        {
            _radius = Math.Max(4, radius);
        }

        /// <summary>
        /// 마우스를 누르면 해당 위치에서 바로 삭제가 가능한지 검사한다.
        /// </summary>
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

        /// <summary>
        /// 누른 상태에서 이동하면 이동 경로를 따라 반복적으로 삭제를 수행한다.
        /// </summary>
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

        /// <summary>
        /// 버튼을 떼면 삭제 플래그 해제.
        /// </summary>
        private void Canvas_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!(sender is InkCanvas canvas) || !_states.TryGetValue(canvas, out CanvasState state))
                return;

            if (!state.IsErasing)
                return;

            StopErasing(state);
        }

        /// <summary>
        /// 커서가 캔버스 밖으로 나갔을 때도 안정적으로 삭제 모드를 끈다.
        /// </summary>
        private void Canvas_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is InkCanvas canvas && _states.TryGetValue(canvas, out CanvasState state))
            {
                StopErasing(state);
            }
        }

        /// <summary>
        /// 공통으로 사용되는 삭제 종료 루틴. Capture를 해제하여 다른 UI와의 충돌을 막는다.
        /// </summary>
        private void StopErasing(CanvasState state)
        {
            if (state == null || !state.IsErasing)
                return;

            state.IsErasing = false;
            state.Canvas?.ReleaseMouseCapture();
        }

        /// <summary>
        /// 지정 좌표를 중심으로 히트 테스트하여 TextBox/Shape 요소를 삭제한다.
        /// </summary>
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

        /// <summary>
        /// TextTool/ShapeTool에서 추가된 요소인지 확인한다.
        /// </summary>
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

        /// <summary>
        /// InkCanvas 좌표계 기준으로 UIElement의 Bounding box를 구한다.
        /// </summary>
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
