using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Documents;
using PDFEditor.Text;

namespace PDFEditor.Shapes
{
    /// <summary>
    /// InkCanvas 위의 도형(Text 포함)과 Stroke를 박스 선택/드래그/리사이즈할 수 있게 해주는 도구 관리자.
    /// </summary>
    public class SelectionToolManager
    {
        public event Action<InkCanvas, TextBox> TextElementDoubleClicked;

        private class SelectionState
        {
            public InkCanvas Canvas;
            public List<UIElement> SelectedElements = new List<UIElement>();
            public StrokeCollection SelectedStrokes = new StrokeCollection();
            public bool IsDragging;
            public Point LastMousePosition;
            public bool IsBoxSelecting;
            public Point BoxStart;
            public Rectangle SelectionBox;
            public GroupSelectionAdorner GroupAdorner;
            public AdornerLayer AdornerLayer;
        }

        private readonly Dictionary<InkCanvas, SelectionState> _states
            = new Dictionary<InkCanvas, SelectionState>();

        // 전체 선택 도구 on/off
        private bool _enabled = false;

        /// <summary>
        /// InkCanvas에 SelectionBox/AdornerLayer를 준비하고 마우스 이벤트를 바인딩한다.
        /// </summary>
        public void AttachCanvas(InkCanvas canvas)
        {
            if (canvas == null || _states.ContainsKey(canvas))
                return;

            var state = new SelectionState();
            state.Canvas = canvas;
            state.AdornerLayer = AdornerLayer.GetAdornerLayer(canvas);

            var selectionBox = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromArgb(200, 120, 120, 120)),
                Fill = new SolidColorBrush(Color.FromArgb(70, 160, 160, 160)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 2 },
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            state.SelectionBox = selectionBox;
            canvas.Children.Add(selectionBox);
            Panel.SetZIndex(selectionBox, int.MaxValue);

            _states[canvas] = state;

            canvas.PreviewMouseLeftButtonDown += Canvas_PreviewMouseLeftButtonDown;
            canvas.PreviewMouseMove += Canvas_PreviewMouseMove;
            canvas.PreviewMouseLeftButtonUp += Canvas_PreviewMouseLeftButtonUp;
        }

        /// <summary>
        /// 선택 도구 활성화 여부를 지정한다. 꺼질 때는 현재 선택과 드래그 상태를 초기화한다.
        /// </summary>
        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;

            foreach (var kv in _states)
            {
                var state = kv.Value;

                state.IsDragging = false;
                state.IsBoxSelecting = false;
                state.Canvas?.ReleaseMouseCapture();
                if (state.SelectionBox != null)
                    state.SelectionBox.Visibility = Visibility.Collapsed;
                if (state.GroupAdorner != null)
                    state.GroupAdorner.Hide();

                if (!enabled)
                {
                    ClearSelectionInternal(state);
                }
            }
        }


        /// <summary>
        /// 클릭 시 단일 선택/박스 선택/드래그 시작 여부를 결정한다.
        /// </summary>
        private void Canvas_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_enabled) return;

            var canvas = (InkCanvas)sender;
            var state = _states[canvas];

            Point pos = e.GetPosition(canvas);

            var element = FindSelectableElement(state, pos);
            if (element is TextBox textElement)
            {
                var tag = textElement.Tag as string;
                if (tag == TextToolManager.TextElementTag && e.ClickCount >= 2)
                {
                    TextElementDoubleClicked?.Invoke(canvas, textElement);
                    e.Handled = true;
                    return;
                }
            }

            if (element != null)
            {
                bool alreadySelected = state.SelectedElements.Contains(element);
                if (!alreadySelected || state.SelectedElements.Count <= 1)
                {
                    SetSingleSelection(state, element);
                }

                state.IsDragging = true;
                state.LastMousePosition = pos;

                if (canvas.Children.Contains(element))
                {
                    canvas.Children.Remove(element);
                    canvas.Children.Add(element);
                }

                e.Handled = true;
                return;
            }

            if (IsPointInsideSelection(state, pos))
            {
                state.IsDragging = true;
                state.LastMousePosition = pos;
                e.Handled = true;
            }
            else
            {
                ClearSelectionInternal(state);
                StartBoxSelection(state, pos);
                e.Handled = true;
            }
        }

        /// <summary>
        /// 기존 선택을 모두 비우고 단일 요소만 선택 상태로 만든다.
        /// </summary>
        private void SetSingleSelection(SelectionState state, UIElement element)
        {
            if (state == null)
                return;

            ClearSelectionInternal(state);
            if (element == null)
                return;

            state.SelectedElements.Add(element);
            UpdateGroupAdornerBounds(state);
        }

        /// <summary>
        /// 선택된 UIElement와 Stroke, SelectionBox, Adorner를 모두 초기화한다.
        /// </summary>
        private void ClearSelectionInternal(SelectionState state)
        {
            state.IsDragging = false;

            state.SelectedElements.Clear();
            state.SelectedStrokes.Clear();
            if (state.SelectionBox != null && !state.IsBoxSelecting)
            {
                state.SelectionBox.Visibility = Visibility.Collapsed;
            }
            if (state.GroupAdorner != null)
            {
                state.GroupAdorner.Hide();
            }
        }


        /// <summary>
        /// 드래그 중일 때 요소를 이동하거나 박스 선택 사각형을 갱신한다.
        /// </summary>
        private void Canvas_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_enabled) return;

            var canvas = (InkCanvas)sender;
            var state = _states[canvas];

            if (state.IsBoxSelecting)
            {
                UpdateSelectionBox(state, e.GetPosition(canvas));
                e.Handled = true;
                return;
            }

            bool hasSelection = (state.SelectedElements.Count > 0) || (state.SelectedStrokes.Count > 0);
            if (!state.IsDragging || !hasSelection)
                return;

            if (e.LeftButton != MouseButtonState.Pressed)
                return;

            Point pos = e.GetPosition(canvas);
            Vector delta = pos - state.LastMousePosition;
            if (delta.X == 0 && delta.Y == 0)
                return;

            MoveSelectedElements(state, delta);
            MoveSelectedStrokes(state, delta);

            state.LastMousePosition = pos;

            UpdateGroupAdornerBounds(state);

            e.Handled = true;
        }

        /// <summary>
        /// 마우스를 떼면 드래그/박스 선택을 종료하고 캡처를 해제한다.
        /// </summary>
        private void Canvas_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_enabled) return;

            var canvas = (InkCanvas)sender;
            var state = _states[canvas];

            if (state.IsBoxSelecting)
            {
                CompleteBoxSelection(state, e.GetPosition(canvas));
                e.Handled = true;
                return;
            }

            if (!state.IsDragging) return;

            state.IsDragging = false;
            // 선택 상태는 유지 (원하면 여기서 SelectedElement도 null로)
            e.Handled = true;
        }

        /// <summary>
        /// 클릭 지점에서 선택 가능한 UIElement를 역순으로 찾는다.
        /// </summary>
        private UIElement FindSelectableElement(SelectionState state, Point position)
        {
            var canvas = state.Canvas;
            if (canvas == null) return null;

            for (int i = canvas.Children.Count - 1; i >= 0; i--)
            {
                if (canvas.Children[i] is UIElement element)
                {
                    if (ReferenceEquals(element, state.SelectionBox))
                        continue;
                    if (!IsSelectableElement(element))
                        continue;
                    if (IsPointOverElement(canvas, element, position))
                        return element;
                }
            }

            return null;
        }

        /// <summary>
        /// 주어진 위치가 요소의 Bounds 안에 있는지 확인한다.
        /// </summary>
        private bool IsPointOverElement(InkCanvas canvas, UIElement element, Point canvasPoint)
        {
            GeneralTransform transform = element.TransformToVisual(canvas);
            if (transform == null || !transform.Inverse.TryTransform(canvasPoint, out Point localPoint))
                return false;

            Rect bounds = VisualTreeHelper.GetDescendantBounds(element);
            return bounds.Contains(localPoint);
        }

        /// <summary>
        /// ShapeTool/TextTool이 생성한 요소인지 파악한다.
        /// </summary>
        private bool IsSelectableElement(UIElement element)
        {
            if (element is Shape shape)
            {
                var tag = shape.Tag as string;
                return tag == ShapeToolManager.ShapeElementTag;
            }

            if (element is TextBox textBox)
            {
                var tag = textBox.Tag as string;
                return tag == TextToolManager.TextElementTag;
            }

            return false;
        }

        /// <summary>
        /// Canvas 좌표에서 UIElement를 delta 만큼 이동시킨다.
        /// </summary>
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

        /// <summary>
        /// 현재 선택된 모든 UIElement에 동일한 이동량을 적용한다.
        /// </summary>
        private void MoveSelectedElements(SelectionState state, Vector delta)
        {
            if (state.SelectedElements == null)
                return;

            foreach (var element in state.SelectedElements)
            {
                MoveElement(element, delta);
            }
        }

        /// <summary>
        /// 선택된 Stroke들을 Translate 메서드로 이동시킨다.
        /// </summary>
        private void MoveSelectedStrokes(SelectionState state, Vector delta)
        {
            if (state.SelectedStrokes == null || state.SelectedStrokes.Count == 0)
                return;

            Matrix matrix = Matrix.Identity;
            matrix.Translate(delta.X, delta.Y);

            foreach (var stroke in state.SelectedStrokes)
            {
                stroke.Transform(matrix, false);
            }
        }

        /// <summary>
        /// 박스 선택 모드를 시작하고 SelectionBox를 화면에 보이게 한다.
        /// </summary>
        private void StartBoxSelection(SelectionState state, Point start)
        {
            state.IsBoxSelecting = true;
            state.BoxStart = start;
            if (state.SelectionBox != null)
            {
                UpdateSelectionBoxVisual(state, start, start);
                state.SelectionBox.Visibility = Visibility.Visible;
            }
            if (state.GroupAdorner != null)
            {
                state.GroupAdorner.Hide();
            }
            state.Canvas?.CaptureMouse();
        }

        /// <summary>
        /// 마우스 이동에 맞게 SelectionBox의 크기를 조절한다.
        /// </summary>
        private void UpdateSelectionBox(SelectionState state, Point current)
        {
            if (state.SelectionBox == null)
                return;
            UpdateSelectionBoxVisual(state, state.BoxStart, current);
        }

        /// <summary>
        /// Rectangle 컨트롤에 bounding box 좌표를 적용한다.
        /// </summary>
        private void UpdateSelectionBoxVisual(SelectionState state, Point start, Point current)
        {
            var rect = new Rect(start, current);
            if (state.SelectionBox == null)
                return;

            InkCanvas.SetLeft(state.SelectionBox, rect.X);
            InkCanvas.SetTop(state.SelectionBox, rect.Y);
            state.SelectionBox.Width = rect.Width;
            state.SelectionBox.Height = rect.Height;
        }

        /// <summary>
        /// 마우스를 떼면 사각형 내부 요소/스트로크를 모두 선택한다.
        /// </summary>
        private void CompleteBoxSelection(SelectionState state, Point end)
        {
            state.IsBoxSelecting = false;
            state.Canvas?.ReleaseMouseCapture();

            if (state.SelectionBox != null)
                state.SelectionBox.Visibility = Visibility.Collapsed;

            Rect rect = new Rect(state.BoxStart, end);
            if (rect.Width < 2 && rect.Height < 2)
                return;

            SelectElementsInRectangle(state, rect);
        }

        /// <summary>
        /// 주어진 사각형 안에 들어온 UIElement와 Stroke를 찾아 Selection 목록에 추가한다.
        /// </summary>
        private void SelectElementsInRectangle(SelectionState state, Rect rect)
        {
            var canvas = state.Canvas;
            if (canvas == null) return;

            state.SelectedElements.Clear();
            state.SelectedStrokes.Clear();

            foreach (UIElement child in canvas.Children)
            {
                if (ReferenceEquals(child, state.SelectionBox))
                    continue;
                if (!IsSelectableElement(child))
                    continue;

                Rect bounds = GetElementBounds(canvas, child);
                if (bounds.IntersectsWith(rect) || rect.Contains(bounds))
                {
                    state.SelectedElements.Add(child);
                }
            }

            foreach (var stroke in canvas.Strokes)
            {
                Rect bounds = stroke.GetBounds();
                if (bounds.IntersectsWith(rect))
                {
                    state.SelectedStrokes.Add(stroke);
                }
            }

            UpdateGroupAdornerBounds(state);
        }

        /// <summary>
        /// InkCanvas 좌표계 기준으로 요소의 Bounds를 계산한다.
        /// </summary>
        private Rect GetElementBounds(InkCanvas canvas, UIElement element)
        {
            GeneralTransform transform = element.TransformToVisual(canvas);
            Rect bounds = VisualTreeHelper.GetDescendantBounds(element);
            if (transform != null)
            {
                bounds = transform.TransformBounds(bounds);
            }
            return bounds;
        }

        /// <summary>
        /// 현재 선택 그룹을 둘러싸는 Adorner가 없다면 생성한다.
        /// </summary>
        private GroupSelectionAdorner EnsureGroupAdorner(SelectionState state)
        {
            if (state.GroupAdorner != null)
                return state.GroupAdorner;

            if (state.Canvas == null)
                return null;

            if (state.AdornerLayer == null)
                state.AdornerLayer = AdornerLayer.GetAdornerLayer(state.Canvas);

            if (state.AdornerLayer == null)
                return null;

            var adorner = new GroupSelectionAdorner(state.Canvas);
            adorner.BoundsChanged += (oldBounds, newBounds) => ResizeSelection(state, oldBounds, newBounds);
            state.AdornerLayer.Add(adorner);
            state.GroupAdorner = adorner;
            return adorner;
        }

        /// <summary>
        /// 그룹 Adorner에서 드래그된 새 Bounds를 토대로 요소/스트로크를 동시에 스케일링한다.
        /// </summary>
        private void ResizeSelection(SelectionState state, Rect oldBounds, Rect newBounds)
        {
            double sx = oldBounds.Width <= 0 ? 1 : newBounds.Width / oldBounds.Width;
            double sy = oldBounds.Height <= 0 ? 1 : newBounds.Height / oldBounds.Height;

            foreach (var element in state.SelectedElements)
            {
                ResizeElement(state, element, oldBounds, newBounds, sx, sy);
            }

            if (state.SelectedStrokes.Count > 0)
            {
                Matrix matrix = Matrix.Identity;
                matrix.Translate(-oldBounds.X, -oldBounds.Y);
                matrix.Scale(sx, sy);
                matrix.Translate(newBounds.X, newBounds.Y);
                foreach (var stroke in state.SelectedStrokes)
                {
                    stroke.Transform(matrix, false);
                }
            }

            UpdateGroupAdornerBounds(state);
        }

        /// <summary>
        /// 개별 UIElement에 대해 스케일 변환을 적용한다. TextBox와 Shape를 구분하여 처리한다.
        /// </summary>
        private void ResizeElement(SelectionState state, UIElement element, Rect oldBounds, Rect newBounds, double sx, double sy)
        {
            if (element is Line line)
            {
                line.X1 = newBounds.X + (line.X1 - oldBounds.X) * sx;
                line.X2 = newBounds.X + (line.X2 - oldBounds.X) * sx;
                line.Y1 = newBounds.Y + (line.Y1 - oldBounds.Y) * sy;
                line.Y2 = newBounds.Y + (line.Y2 - oldBounds.Y) * sy;
                return;
            }

            if (element is Polygon polygon)
            {
                for (int i = 0; i < polygon.Points.Count; i++)
                {
                    Point pt = polygon.Points[i];
                    double newX = newBounds.X + (pt.X - oldBounds.X) * sx;
                    double newY = newBounds.Y + (pt.Y - oldBounds.Y) * sy;
                    polygon.Points[i] = new Point(newX, newY);
                }
                return;
            }

            var canvas = state.Canvas;
            if (canvas == null)
                return;

            double left = InkCanvas.GetLeft(element);
            double top = InkCanvas.GetTop(element);
            if (double.IsNaN(left) || double.IsNaN(top))
            {
                Rect elementBounds = GetElementBounds(canvas, element);
                if (double.IsNaN(left)) left = elementBounds.X;
                if (double.IsNaN(top)) top = elementBounds.Y;
            }

            double width = 0;
            double height = 0;
            if (element is FrameworkElement fe)
            {
                width = fe.Width;
                height = fe.Height;
                if (double.IsNaN(width) || width <= 0) width = fe.RenderSize.Width;
                if (double.IsNaN(height) || height <= 0) height = fe.RenderSize.Height;
                if (width <= 0 || height <= 0)
                {
                    Rect bounds = GetElementBounds(canvas, element);
                    if (width <= 0) width = bounds.Width;
                    if (height <= 0) height = bounds.Height;
                }
            }
            else
            {
                Rect bounds = GetElementBounds(canvas, element);
                width = bounds.Width;
                height = bounds.Height;
            }

            double offsetX = left - oldBounds.X;
            double offsetY = top - oldBounds.Y;

            double newLeft = newBounds.X + offsetX * sx;
            double newTop = newBounds.Y + offsetY * sy;
            double newWidth = width * sx;
            double newHeight = height * sy;

            InkCanvas.SetLeft(element, newLeft);
            InkCanvas.SetTop(element, newTop);

            if (element is FrameworkElement frameworkElement)
            {
                frameworkElement.Width = newWidth;
                frameworkElement.Height = newHeight;
            }
        }

        /// <summary>
        /// 선택된 요소/스트로크의 전체 Bounds를 다시 계산해 Adorner에 전달한다.
        /// </summary>
        private void UpdateGroupAdornerBounds(SelectionState state)
        {
            var bounds = GetSelectionBounds(state);
            if (!bounds.HasValue || bounds.Value.Width <= 0 || bounds.Value.Height <= 0)
            {
                if (state.GroupAdorner != null)
                    state.GroupAdorner.Hide();
                return;
            }

            var adorner = EnsureGroupAdorner(state);
            if (adorner != null)
            {
                adorner.UpdateBounds(bounds.Value);
            }
        }

        /// <summary>
        /// 현재 선택된 모든 요소와 스트로크를 둘러싸는 Rect를 계산한다.
        /// </summary>
        private Rect? GetSelectionBounds(SelectionState state)
        {
            Rect? union = null;
            var canvas = state.Canvas;
            if (canvas == null)
                return null;

            foreach (var element in state.SelectedElements)
            {
                Rect elementBounds = GetElementBounds(canvas, element);
                union = union.HasValue ? Rect.Union(union.Value, elementBounds) : elementBounds;
            }

            foreach (var stroke in state.SelectedStrokes)
            {
                Rect strokeBounds = stroke.GetBounds();
                union = union.HasValue ? Rect.Union(union.Value, strokeBounds) : strokeBounds;
            }

            return union;
        }

        /// <summary>
        /// 클릭 지점이 현재 Selection 그룹 내부인지 확인한다.
        /// </summary>
        private bool IsPointInsideSelection(SelectionState state, Point point)
        {
            var bounds = GetSelectionBounds(state);
            if (!bounds.HasValue)
                return false;
            return bounds.Value.Contains(point);
        }

        /// <summary>
        /// 단일 선택된 요소를 반환한다. 여러 개면 null.
        /// </summary>
        public UIElement GetSelectedElement(InkCanvas canvas)
        {
            if (canvas == null) return null;
            SelectionState state;
            if (_states.TryGetValue(canvas, out state))
            {
                if (state.SelectedElements.Count > 0)
                    return state.SelectedElements[0];
            }
            return null;
        }

        /// <summary>
        /// 외부에서 특정 요소를 선택 상태로 만들고 UI를 반영한다.
        /// </summary>
        public void SelectElement(InkCanvas canvas, UIElement element)
        {
            if (canvas == null) return;
            SelectionState state;
            if (_states.TryGetValue(canvas, out state))
            {
                SetSingleSelection(state, element);
            }
        }

        /// <summary>
        /// 주어진 캔버스의 선택 상태를 초기화한다.
        /// </summary>
        public void ClearSelection(InkCanvas canvas)
        {
            if (canvas == null) return;
            SelectionState state;
            if (_states.TryGetValue(canvas, out state))
            {
                ClearSelectionInternal(state);
            }
        }

        /// <summary>
        /// 현재 선택된 UIElement 목록의 복사본을 반환한다 (Clipboard 기능 등에서 사용).
        /// </summary>
        public IReadOnlyList<UIElement> GetSelectedElementsSnapshot(InkCanvas canvas)
        {
            if (canvas == null)
                return Array.Empty<UIElement>();

            SelectionState state;
            if (_states.TryGetValue(canvas, out state) && state.SelectedElements.Count > 0)
            {
                return new List<UIElement>(state.SelectedElements);
            }

            return Array.Empty<UIElement>();
        }

        /// <summary>
        /// 선택된 StrokeCollection의 복사본을 반환한다.
        /// </summary>
        public IReadOnlyList<Stroke> GetSelectedStrokesSnapshot(InkCanvas canvas)
        {
            if (canvas == null)
                return Array.Empty<Stroke>();

            SelectionState state;
            if (_states.TryGetValue(canvas, out state) && state.SelectedStrokes.Count > 0)
            {
                return new List<Stroke>(state.SelectedStrokes);
            }

            return Array.Empty<Stroke>();
        }

        /// <summary>
        /// 외부에서 elements/strokes를 지정해 Selection 상태를 맞춰준다(붙여넣기 이후 등).
        /// </summary>
        public void SelectItems(InkCanvas canvas, IEnumerable<UIElement> elements, IEnumerable<Stroke> strokes)
        {
            if (canvas == null)
                return;

            SelectionState state;
            if (!_states.TryGetValue(canvas, out state))
                return;

            state.SelectedElements.Clear();
            state.SelectedStrokes.Clear();

            if (elements != null)
            {
                foreach (var element in elements)
                {
                    if (element == null)
                        continue;
                    if (!canvas.Children.Contains(element))
                        continue;
                    if (!IsSelectableElement(element))
                        continue;
                    state.SelectedElements.Add(element);
                }
            }

            if (strokes != null)
            {
                foreach (var stroke in strokes)
                {
                    if (stroke == null)
                        continue;
                    if (!canvas.Strokes.Contains(stroke))
                        continue;
                    state.SelectedStrokes.Add(stroke);
                }
            }

            UpdateGroupAdornerBounds(state);
        }
    }
}
