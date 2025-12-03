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
    // 사용자가 선택 가능한 도형 타입 정의
    public enum ShapeType
    {
        None,
        Rectangle,
        Ellipse,
        Line,
        Triangle
    }

    /// <summary>
    /// InkCanvas 위에 사각형/원/선 등을 직접 그려 주는 도구 관리자.
    /// 도형 드래그, 미리보기, 태그 기반 지우개 기능을 모두 담당한다.
    /// </summary>
    public class ShapeToolManager
    {
        // InkCanvas.Children에서 다른 요소와 구분하기 위해 부여하는 태그
        private const string ShapeElementTag = "ShapeToolElement";

        public ShapeType CurrentShape { get; private set; } = ShapeType.None;
        public Brush StrokeBrush { get; private set; } = Brushes.Black;
        public double StrokeThickness { get; private set; } = 3.0;

        // InkCanvas 별 드래깅 상태 저장
        private class ShapeDrawingState
        {
            public bool IsDrawing;
            public Point StartPoint;
            public Shape PreviewShape;
        }

        private readonly Dictionary<InkCanvas, ShapeDrawingState> _states
            = new Dictionary<InkCanvas, ShapeDrawingState>();
        private bool _eraseShapeMode = false;

        /// <summary>
        /// InkCanvas에 도형 도구를 붙여 마우스 이벤트를 가로챈다.
        /// Preview 이벤트를 사용해야 InkCanvas 지우개 모드에서도 동작한다.
        /// </summary>
        public void AttachCanvas(InkCanvas canvas)
        {
            if (canvas == null || _states.ContainsKey(canvas))
                return;

            _states[canvas] = new ShapeDrawingState();

            canvas.PreviewMouseLeftButtonDown += Canvas_MouseLeftButtonDown;
            canvas.PreviewMouseMove += Canvas_MouseMove;
            canvas.PreviewMouseLeftButtonUp += Canvas_MouseLeftButtonUp;
        }

        /// <summary>
        /// 현재 그릴 도형 종류 지정.
        /// </summary>
        public void SetShape(ShapeType type)
        {
            CurrentShape = type;
        }

        /// <summary>
        /// 도형 전용 지우개 모드를 설정.
        /// InkCanvas 지우개가 동작할 때 화면의 도형을 함께 지우는 데 사용.
        /// </summary>
        public void SetShapeEraseMode(bool enabled)
        {
            _eraseShapeMode = enabled;
        }

        /// <summary>
        /// 도형 외곽선 색/두께 적용.
        /// </summary>
        public void SetStroke(Brush brush, double thickness)
        {
            StrokeBrush = brush ?? Brushes.Black;
            StrokeThickness = thickness;
        }

        private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var canvas = (InkCanvas)sender;
            if (_eraseShapeMode)
            {
                TryEraseShape(canvas, e.GetPosition(canvas)); // 지우개 모드일 때는 즉시 지우기
                return;
            }

            if (CurrentShape == ShapeType.None)
                return;

            var state = _states[canvas];

            state.IsDrawing = true;
            state.StartPoint = e.GetPosition(canvas);

            var shape = CreateShape();   // 미리보기용 Shape 생성
            shape.Stroke = StrokeBrush;
            shape.StrokeThickness = StrokeThickness;
            shape.Fill = Brushes.Transparent;

            state.PreviewShape = shape;
            canvas.Children.Add(shape);

            // ✅ 위치는 여기서 잡지 않고, MouseMove에서만 계산
            canvas.CaptureMouse();
            e.Handled = true;
        }

        /// <summary>
        /// 현재 ShapeType에 맞는 Shape 인스턴스를 생성하고 태그를 붙인다.
        /// </summary>
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

            shape.Tag = ShapeElementTag;
            return shape;
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            var canvas = (InkCanvas)sender;
            if (_eraseShapeMode)
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                {
                    TryEraseShape(canvas, e.GetPosition(canvas)); // 드래그 중에도 지우기
                }
                return;
            }

            if (CurrentShape == ShapeType.None)
                return;

            var state = _states[canvas];

            if (!state.IsDrawing || state.PreviewShape == null)
                return;

            Point current = e.GetPosition(canvas);
            UpdateShapeGeometry(state, current);

            e.Handled = true;
        }

        private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (CurrentShape == ShapeType.None)
                return;

            var canvas = (InkCanvas)sender;
            var state = _states[canvas];

            if (!state.IsDrawing)
                return;

            state.IsDrawing = false;
            state.PreviewShape = null;
            canvas.ReleaseMouseCapture();

            e.Handled = true;
        }

        /// <summary>
        /// 시작/현재 포인트를 기준으로 bounding box를 만들고
        /// Shape 타입에 맞춰 위치/크기/점 정보를 갱신한다.
        /// </summary>
        private void UpdateShapeGeometry(ShapeDrawingState state, Point current)
        {
            Point start = state.StartPoint;

            // ✅ 시작점과 현재점으로 bounding box 생성 (왼쪽/위 드래그 자동 처리)
            Rect box = new Rect(start, current);
            double left = box.X;
            double top = box.Y;
            double width = box.Width;
            double height = box.Height;

            // 너무 작으면 안 보일 수 있으니 최소값
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
                    // 드래그 박스 기준에 맞춘 삼각형 (위/좌하/우하)
                    var p1 = new Point(left + width / 2, top);          // 위 중앙
                    var p2 = new Point(left, top + height);             // 좌하
                    var p3 = new Point(left + width, top + height);     // 우하
                    polygon.Points = new PointCollection { p1, p2, p3 };
                    break;
            }
        }

        /// <summary>
        /// 마우스 좌표 아래의 Shape를 찾고 태그가 맞으면 InkCanvas에서 제거한다.
        /// </summary>
        private void TryEraseShape(InkCanvas canvas, Point point)
        {
            if (canvas == null) return;

            var hit = VisualTreeHelper.HitTest(canvas, point);
            if (hit == null) return;

            var shape = FindShapeFromHit(hit.VisualHit);
            if (shape == null) return;
            if (!Equals(shape.Tag, ShapeElementTag)) return;
            if (!canvas.Children.Contains(shape)) return;

            canvas.Children.Remove(shape);
        }

        /// <summary>
        /// 히트 테스트 결과에서 Shape가 나올 때까지 시각 트리를 거슬러 올라간다.
        /// </summary>
        private Shape FindShapeFromHit(DependencyObject visual)
        {
            while (visual != null && !(visual is Shape))
            {
                visual = VisualTreeHelper.GetParent(visual);
            }

            return visual as Shape;
        }
    }
}
