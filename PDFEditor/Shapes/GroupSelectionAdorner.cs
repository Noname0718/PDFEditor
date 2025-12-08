using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace PDFEditor.Shapes
{
    /// <summary>
    /// 여러 요소/스트로크 선택 시 InkCanvas 위에 나타나는 점선 박스 + 리사이즈 핸들.
    /// </summary>
    public class GroupSelectionAdorner : Adorner
    {
        private const double ThumbSize = 5;
        private const double MinSize = 6;

        private readonly VisualCollection _visuals;
        private readonly List<HandleInfo> _handles = new List<HandleInfo>();
        private Rect _bounds;
        private bool _isVisible;

        private class HandleInfo
        {
            public Thumb Thumb;
            public int Horizontal;
            public int Vertical;
        }

        public event Action<Rect, Rect> BoundsChanged;

        public GroupSelectionAdorner(UIElement adornedElement)
            : base(adornedElement)
        {
            _visuals = new VisualCollection(this);

            AddHandle(-1, -1, Cursors.SizeNWSE);
            AddHandle(0, -1, Cursors.SizeNS);
            AddHandle(+1, -1, Cursors.SizeNESW);
            AddHandle(+1, 0, Cursors.SizeWE);
            AddHandle(+1, +1, Cursors.SizeNWSE);
            AddHandle(0, +1, Cursors.SizeNS);
            AddHandle(-1, +1, Cursors.SizeNESW);
            AddHandle(-1, 0, Cursors.SizeWE);

            IsHitTestVisible = true;
            Visibility = Visibility.Collapsed;
        }

        public void UpdateBounds(Rect bounds)
        {
            _bounds = bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                _isVisible = false;
                Visibility = Visibility.Collapsed;
            }
            else
            {
                _isVisible = true;
                Visibility = Visibility.Visible;
            }
            InvalidateArrange();
            InvalidateVisual();
        }

        public void Hide()
        {
            _isVisible = false;
            Visibility = Visibility.Collapsed;
        }

        private void AddHandle(int horizontal, int vertical, Cursor cursor)
        {
            var thumb = new Thumb
            {
                Width = ThumbSize,
                Height = ThumbSize,
                Background = Brushes.White,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = cursor
            };
            thumb.Tag = new Point(horizontal, vertical);
            thumb.DragDelta += Thumb_DragDelta;

            _visuals.Add(thumb);
            _handles.Add(new HandleInfo
            {
                Thumb = thumb,
                Horizontal = horizontal,
                Vertical = vertical
            });
        }

        private void Thumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (!(sender is Thumb thumb))
                return;
            if (!(thumb.Tag is Point hv))
                return;

            HandleResize(e, (int)hv.X, (int)hv.Y);
        }

        private void HandleResize(DragDeltaEventArgs e, int horizontal, int vertical)
        {
            if (!_isVisible)
                return;

            Rect oldBounds = _bounds;
            Rect newBounds = oldBounds;

            double dx = e.HorizontalChange;
            double dy = e.VerticalChange;

            if (horizontal < 0)
            {
                double newLeft = newBounds.Left + dx;
                double newWidth = newBounds.Width - dx;
                if (newWidth >= MinSize)
                {
                    newBounds.X = newLeft;
                    newBounds.Width = newWidth;
                }
            }
            else if (horizontal > 0)
            {
                double newWidth = newBounds.Width + dx;
                if (newWidth >= MinSize)
                {
                    newBounds.Width = newWidth;
                }
            }

            if (vertical < 0)
            {
                double newTop = newBounds.Top + dy;
                double newHeight = newBounds.Height - dy;
                if (newHeight >= MinSize)
                {
                    newBounds.Y = newTop;
                    newBounds.Height = newHeight;
                }
            }
            else if (vertical > 0)
            {
                double newHeight = newBounds.Height + dy;
                if (newHeight >= MinSize)
                {
                    newBounds.Height = newHeight;
                }
            }

            _bounds = newBounds;
            InvalidateArrange();
            InvalidateVisual();

            if (BoundsChanged != null)
            {
                BoundsChanged(oldBounds, newBounds);
            }

            e.Handled = true;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (!_isVisible)
                return finalSize;

            foreach (var handle in _handles)
            {
                double relX = (handle.Horizontal + 1) / 2.0;
                double relY = (handle.Vertical + 1) / 2.0;

                double x = _bounds.Left + _bounds.Width * relX - ThumbSize / 2;
                double y = _bounds.Top + _bounds.Height * relY - ThumbSize / 2;

                handle.Thumb.Arrange(new Rect(x, y, ThumbSize, ThumbSize));
            }

            return finalSize;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            if (!_isVisible)
                return;

            var pen = new Pen(Brushes.DodgerBlue, 1.0)
            {
                DashStyle = DashStyles.Dash
            };
            drawingContext.DrawRectangle(null, pen, _bounds);
        }

        protected override int VisualChildrenCount => _visuals.Count;

        protected override Visual GetVisualChild(int index) => _visuals[index];
    }
}
