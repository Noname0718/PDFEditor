using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PDFEditor.Shapes
{
    public class SelectionAdorner : Adorner
    {
        private const double ThumbSize = 10;
        private const double MinSize = 20;

        private readonly VisualCollection _visuals;
        private readonly Thumb _topLeft;
        private readonly Thumb _topRight;
        private readonly Thumb _bottomLeft;
        private readonly Thumb _bottomRight;

        public SelectionAdorner(UIElement adornedElement)
            : base(adornedElement)
        {
            _topLeft = CreateThumb();
            _topRight = CreateThumb();
            _bottomLeft = CreateThumb();
            _bottomRight = CreateThumb();

            _topLeft.DragDelta += (s, e) => HandleResize(e, horizontal: -1, vertical: -1);
            _topRight.DragDelta += (s, e) => HandleResize(e, horizontal: +1, vertical: -1);
            _bottomLeft.DragDelta += (s, e) => HandleResize(e, horizontal: -1, vertical: +1);
            _bottomRight.DragDelta += (s, e) => HandleResize(e, horizontal: +1, vertical: +1);

            _visuals = new VisualCollection(this)
            {
                _topLeft, _topRight, _bottomLeft, _bottomRight
            };

            IsHitTestVisible = true;
        }

        private Thumb CreateThumb()
        {
            return new Thumb
            {
                Width = ThumbSize,
                Height = ThumbSize,
                Background = Brushes.White,
                BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.SizeAll
            };
        }

        /// <summary>
        /// 드래그 방향(horizontal, vertical)이 -1 / +1 인지에 따라
        /// 어느 모서리에서 리사이즈하는지 결정.
        /// </summary>
        private void HandleResize(DragDeltaEventArgs e, int horizontal, int vertical)
        {
            if (!(AdornedElement is FrameworkElement fe))
                return;

            // InkCanvas 위에 올려진 요소라고 가정
            var parent = fe.Parent as FrameworkElement;
            while (parent != null && !(parent is InkCanvas))
            {
                parent = parent.Parent as FrameworkElement;
            }

            if (!(parent is InkCanvas canvas))
                return;

            double left = InkCanvas.GetLeft(fe);
            double top = InkCanvas.GetTop(fe);
            if (double.IsNaN(left)) left = 0;
            if (double.IsNaN(top)) top = 0;

            double width = fe.Width;
            double height = fe.Height;

            // Width / Height가 0이면 실제 RenderSize를 사용
            if (double.IsNaN(width) || width <= 0)
                width = fe.RenderSize.Width;
            if (double.IsNaN(height) || height <= 0)
                height = fe.RenderSize.Height;

            double dx = e.HorizontalChange;
            double dy = e.VerticalChange;

            // 수평 리사이즈
            if (horizontal < 0) // 왼쪽 잡고 움직일 때
            {
                double newLeft = left + dx;
                double newWidth = width - dx;
                if (newWidth >= MinSize)
                {
                    left = newLeft;
                    width = newWidth;
                }
            }
            else if (horizontal > 0) // 오른쪽 잡고
            {
                double newWidth = width + dx;
                if (newWidth >= MinSize)
                {
                    width = newWidth;
                }
            }

            // 수직 리사이즈
            if (vertical < 0) // 위쪽
            {
                double newTop = top + dy;
                double newHeight = height - dy;
                if (newHeight >= MinSize)
                {
                    top = newTop;
                    height = newHeight;
                }
            }
            else if (vertical > 0) // 아래쪽
            {
                double newHeight = height + dy;
                if (newHeight >= MinSize)
                {
                    height = newHeight;
                }
            }

            // 선(Line) / Polygon 같은 특수 도형은 여기서 안 다룸.
            // (Rect/Ellipse/Image/TextBox 등만 리사이즈 대상으로 한다.)
            if (fe is Line || fe is Polygon)
                return;

            InkCanvas.SetLeft(fe, left);
            InkCanvas.SetTop(fe, top);
            fe.Width = width;
            fe.Height = height;

            e.Handled = true;

            InvalidateArrange(); // 핸들 위치 다시 계산
            InvalidateVisual();  // 선택 박스 다시 그림
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (!(AdornedElement is FrameworkElement fe))
                return finalSize;

            double width = fe.RenderSize.Width;
            double height = fe.RenderSize.Height;
            if (width <= 0) width = fe.ActualWidth;
            if (height <= 0) height = fe.ActualHeight;

            double half = ThumbSize / 2;

            // 각 모서리에 Thumb 배치 (Adorner 좌표계는 요소의 좌상단을 원점으로 사용)
            _topLeft.Arrange(new Rect(-half, -half, ThumbSize, ThumbSize));
            _topRight.Arrange(new Rect(width - half, -half, ThumbSize, ThumbSize));
            _bottomLeft.Arrange(new Rect(-half, height - half, ThumbSize, ThumbSize));
            _bottomRight.Arrange(new Rect(width - half, height - half, ThumbSize, ThumbSize));

            return finalSize;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);

            Size size = AdornedElement.RenderSize;
            if (size.Width <= 0 || size.Height <= 0)
            {
                if (AdornedElement is FrameworkElement fe)
                {
                    size = new Size(fe.ActualWidth, fe.ActualHeight);
                }
            }

            var rect = new Rect(new Point(0, 0), size);
            var pen = new Pen(Brushes.DeepSkyBlue, 1.5)
            {
                DashStyle = DashStyles.Dash
            };

            drawingContext.DrawRectangle(null, pen, rect);
        }

        protected override int VisualChildrenCount => _visuals.Count;

        protected override Visual GetVisualChild(int index) => _visuals[index];
    }
}
