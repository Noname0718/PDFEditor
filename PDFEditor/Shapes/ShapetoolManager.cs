using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Ink;

namespace PDFEditor.Shapes
{
    public enum ShapeType
    {
        None,
        Rectangle,
        Ellipse,
        Line,
        Triangle
    }

    public class ShapeToolManager
    {
        private const string ShapeElementTag = "ShapeToolElement";

        public ShapeType CurrentShape { get; private set; } = ShapeType.None;
        public Brush StrokeBrush { get; private set; } = Brushes.Black;
        public double StrokeThickness { get; private set; } = 3.0;

        private class ShapeDrawingState
        {
            public bool IsDrawing;
            public Point StartPoint;
            public Shape PreviewShape;
            public bool IsErasingShapes;
        }

        private readonly Dictionary<InkCanvas, ShapeDrawingState> _states
            = new Dictionary<InkCanvas, ShapeDrawingState>();

        // 🔹 도형 지우개 모드 플래그
        private bool _eraseShapeMode = false;

        public void AttachCanvas(InkCanvas canvas)
        {
            if (canvas == null || _states.ContainsKey(canvas))
                return;

            _states[canvas] = new ShapeDrawingState();

            canvas.PreviewMouseLeftButtonDown += Canvas_MouseLeftButtonDown;
            canvas.PreviewMouseMove += Canvas_MouseMove;
            canvas.PreviewMouseLeftButtonUp += Canvas_MouseLeftButtonUp;
        }

        public void SetShape(ShapeType type)
        {
            CurrentShape = type;
        }

        // 🔹 MainWindow에서 호출: "지우개가 도형도 지우게 할지" 설정
        public void SetShapeEraseMode(bool enabled)
        {
            _eraseShapeMode = enabled;
        }

        public void SetStroke(Brush brush, double thickness)
        {
            StrokeBrush = brush ?? Brushes.Black;
            StrokeThickness = thickness;
        }

        /// <summary>
        /// 도형 드래그 시작. 지우개 모드면 해당 지점의 도형을 즉시 삭제한다.
        /// </summary>
        private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var canvas = (InkCanvas)sender;
            var state = _states[canvas];

            // 🔸 도형 지우개 모드일 때: 누르는 순간 그 위치의 도형 제거 + 드래그 플래그 on
            if (_eraseShapeMode)
            {
                state.IsErasingShapes = true;
                TryEraseShape(canvas, e.GetPosition(canvas));
                canvas.CaptureMouse();
                return;
            }

            // 도형이 선택되지 않았으면 아무 것도 안 함 (펜/형광펜은 InkToolManager가 처리)
            if (CurrentShape == ShapeType.None)
                return;

            state.IsDrawing = true;
            state.StartPoint = e.GetPosition(canvas);

            var shape = CreateShape();
            shape.Stroke = StrokeBrush;
            shape.StrokeThickness = StrokeThickness;
            shape.Fill = Brushes.Transparent;

            state.PreviewShape = shape;
            canvas.Children.Add(shape);

            // 위치/크기는 MouseMove에서만 결정
            canvas.CaptureMouse();
            e.Handled = true;
        }

        private Shape CreateShape()
        {
            Shape shape;
            switch (CurrentShape)
            {
                case ShapeType.Rectangle:
                    shape = new Rectangle();
                    break;
                case ShapeType.Ellipse:
                    shape = new Ellipse();
                    break;
                case ShapeType.Line:
                    shape = new Line();
                    break;
                case ShapeType.Triangle:
                    shape = new Polygon();
                    break;
                default:
                    shape = new Rectangle();
                    break;
            }

            shape.Tag = ShapeElementTag; // 지울 때 구분용
            return shape;
        }

        /// <summary>
        /// 드래그 중 미리보기 도형의 위치/크기를 업데이트하거나 지우개 모드에서는 연속 삭제를 수행한다.
        /// </summary>
        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            var canvas = (InkCanvas)sender;
            var state = _states[canvas];

            // 🔸 도형 지우개 모드: 드래그 플래그가 켜져 있을 때 계속 삭제
            if (_eraseShapeMode)
            {
                if (state.IsErasingShapes)
                {
                    TryEraseShape(canvas, e.GetPosition(canvas));
                }
                return;
            }

            if (CurrentShape == ShapeType.None)
                return;

            if (!state.IsDrawing || state.PreviewShape == null)
                return;

            Point current = e.GetPosition(canvas);
            UpdateShapeGeometry(state, current);

            e.Handled = true;
        }

        /// <summary>
        /// 마우스를 떼면 드래그 상태를 종료하고 Capture를 해제한다.
        /// </summary>
        private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var canvas = (InkCanvas)sender;
            var state = _states[canvas];

            // 🔸 도형 지우개 모드: 드래그 종료 처리
            if (_eraseShapeMode)
            {
                state.IsErasingShapes = false;
                canvas.ReleaseMouseCapture();
                return;
            }

            if (CurrentShape == ShapeType.None)
                return;

            if (!state.IsDrawing)
                return;

            state.IsDrawing = false;
            state.PreviewShape = null;
            canvas.ReleaseMouseCapture();

            e.Handled = true;
        }

        private void UpdateShapeGeometry(ShapeDrawingState state, Point current)
        {
            Point start = state.StartPoint;

            // 시작점과 현재점으로 bounding box 생성
            Rect box = new Rect(start, current);
            double left = box.X;
            double top = box.Y;
            double width = box.Width;
            double height = box.Height;

            if (width < 1) width = 1;
            if (height < 1) height = 1;

            switch (state.PreviewShape)
            {
                case Rectangle rect:
                    InkCanvas.SetLeft(rect, left);
                    InkCanvas.SetTop(rect, top);
                    rect.Width = width;
                    rect.Height = height;
                    break;

                case Ellipse ellipse:
                    InkCanvas.SetLeft(ellipse, left);
                    InkCanvas.SetTop(ellipse, top);
                    ellipse.Width = width;
                    ellipse.Height = height;
                    break;

                case Line line:
                    line.X1 = start.X;
                    line.Y1 = start.Y;
                    line.X2 = current.X;
                    line.Y2 = current.Y;
                    break;

                case Polygon polygon:
                    var p1 = new Point(left + width / 2, top);          // 위 중앙
                    var p2 = new Point(left, top + height);             // 좌하
                    var p3 = new Point(left + width, top + height);     // 우하
                    polygon.Points = new PointCollection { p1, p2, p3 };
                    break;
            }
        }

        // ===============================
        //   🔻 여기부터 도형 지우기 로직
        // ===============================
        private void TryEraseShape(InkCanvas canvas, Point point)
        {
            if (canvas == null) return;

            Shape shape = FindShapeAtPoint(canvas, point);
            if (shape == null) return;

            canvas.Children.Remove(shape);
        }

        private Shape FindShapeAtPoint(InkCanvas canvas, Point canvasPoint)
        {
            for (int i = canvas.Children.Count - 1; i >= 0; i--)
            {
                if (canvas.Children[i] is Shape shape && Equals(shape.Tag, ShapeElementTag))
                {
                    if (IsPointInsideShape(canvas, shape, canvasPoint))
                        return shape;
                }
            }

            return null;
        }

        private bool IsPointInsideShape(InkCanvas canvas, Shape shape, Point canvasPoint)
        {
            if (shape == null) return false;

            GeneralTransform transform = shape.TransformToVisual(canvas);
            if (transform == null)
                return false;

            GeneralTransform inverse = transform.Inverse;
            if (inverse == null || !inverse.TryTransform(canvasPoint, out Point localPoint))
                return false;
            var geometry = shape.RenderedGeometry;
            if (geometry == null)
                return false;

            if (geometry.FillContains(localPoint))
                return true;

            double thickness = Math.Max(shape.StrokeThickness, 1);
            var pen = new Pen(shape.Stroke ?? Brushes.Black, thickness + 4);

            return geometry.StrokeContains(pen, localPoint);
        }
    }
}
