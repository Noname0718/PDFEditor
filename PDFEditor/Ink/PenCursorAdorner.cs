using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;


namespace PDFEditor.Ink
{
    /// <summary>
    /// InkCanvas 위에 반투명한 펜/지우개 커서를 직접 그려주는 Adorner.
    /// </summary>
    internal sealed class PenCursorAdorner : Adorner
    {
        internal enum CursorVisual
        {
            Pen,
            Eraser
        }

        private bool _visible = false;
        private Point _position;
        private double _diameter = 3;
        private CursorVisual _visual = CursorVisual.Pen;

        public PenCursorAdorner(UIElement adornedElement) : base(adornedElement)
        {
            IsHitTestVisible = false;
        }

        /// <summary>
        /// 커서가 위치할 좌표를 업데이트하고 즉시 다시 그린다.
        /// </summary>
        public void UpdatePosition(Point position)
        {
            _position = position;
            _visible = true;
            InvalidateVisual();
        }

        /// <summary>
        /// 외부에서 커서 숨김을 요청했을 때 호출된다.
        /// </summary>
        public void Hide()
        {
            _visible = false;
            InvalidateVisual();
        }

        /// <summary>
        /// 펜 굵기에 맞게 시각적인 원/사각형 크기를 조절한다.
        /// </summary>
        public void UpdateThickness(double thickness)
        {
            _diameter = Math.Max(2, thickness);
            InvalidateVisual();
        }

        /// <summary>
        /// 펜/지우개 모드에 따라 그릴 모양을 선택한다.
        /// </summary>
        public void SetVisual(CursorVisual visual)
        {
            _visual = visual;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            if (!_visible) return;

            double radius = _diameter / 2;
            switch (_visual)
            {
                case CursorVisual.Pen:
                    drawingContext.DrawEllipse(
                        new SolidColorBrush(Color.FromArgb(140, 240, 240, 100)),
                        new Pen(Brushes.DarkOliveGreen, 1),
                        _position,
                        radius,
                        radius);
                    break;
                case CursorVisual.Eraser:
                    var rect = new Rect(_position.X - radius, _position.Y - radius, _diameter, _diameter);
                    drawingContext.DrawRectangle(
                        new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                        new Pen(Brushes.Gray, 1),
                        rect);
                    break;
            }
        }
    }
}
