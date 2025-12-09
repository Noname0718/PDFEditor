using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Threading;

namespace PDFEditor.Text
{
    /// <summary>
    /// InkCanvas 위에서 TextBox를 생성/편집하는 도구 관리자.
    /// 클릭으로 TextBox를 추가하고, 더블클릭/Enter로 편집 진입, ESC/포커스 아웃으로 확정한다.
    /// </summary>
    public class TextToolManager
    {
        public const string TextElementTag = "TextToolElement";

        public event Action<InkCanvas, TextBox> TextBoxCreated;
        public event Action<InkCanvas, TextBox, string, string> TextCommitted;
        public event Action<InkCanvas, TextBox> TextBoxRemoved;
        public event Action<TextBox> ActiveTextBoxChanged;
        public event Action<TextBox, bool> EditingStateChanged;

        private readonly Dictionary<InkCanvas, CanvasState> _canvasStates = new Dictionary<InkCanvas, CanvasState>();
        private readonly Dictionary<TextBox, InkCanvas> _textOwners = new Dictionary<TextBox, InkCanvas>();

        private readonly SolidColorBrush _inactiveBorderBrush;
        private readonly SolidColorBrush _activeBorderBrush;

        private TextBox _activeTextBox;
        private TextBox _editingTextBox;
        private string _editingOriginalText;
        private bool _isDraggingActiveTextBox;
        private bool _isPreparingDrag;
        private Point _dragStartPoint;
        private double _dragStartLeft;
        private double _dragStartTop;
        private InkCanvas _dragCanvas;
        private TextBox _dragTarget;

        public bool IsActive { get; private set; }
        public double DefaultFontSize { get; private set; } = 18;
        public Brush DefaultForeground { get; private set; } = Brushes.Black;
        public FontFamily DefaultFontFamily { get; private set; } = new FontFamily("Malgun Gothic");

        private class CanvasState
        {
            public InkCanvas Canvas;
        }

        public TextToolManager()
        {
            _inactiveBorderBrush = Brushes.Transparent;

            _activeBorderBrush = new SolidColorBrush(Color.FromArgb(200, 30, 144, 255));
            _activeBorderBrush.Freeze();
        }

        /// <summary>
        /// InkCanvas에 PreviewMouseDown 이벤트를 연결해 클릭으로 TextBox를 추가할 수 있게 한다.
        /// </summary>
        public void AttachCanvas(InkCanvas canvas)
        {
            if (canvas == null || _canvasStates.ContainsKey(canvas))
                return;

            var state = new CanvasState { Canvas = canvas };
            canvas.PreviewMouseLeftButtonDown += Canvas_PreviewMouseLeftButtonDown;
            _canvasStates[canvas] = state;
        }

        /// <summary>
        /// 모든 캔버스/텍스트박스 상태를 정리하고 이벤트를 해제한다.
        /// 주석을 다른 PDF에 적용할 때 호출된다.
        /// </summary>
        public void Clear()
        {
            foreach (var kv in _canvasStates)
            {
                kv.Key.PreviewMouseLeftButtonDown -= Canvas_PreviewMouseLeftButtonDown;
            }
            _canvasStates.Clear();

            foreach (var kv in _textOwners)
            {
                UnwireTextBox(kv.Key);
            }
            _textOwners.Clear();

            _activeTextBox = null;
            _editingTextBox = null;
            _editingOriginalText = null;
            _dragCanvas = null;
            _isDraggingActiveTextBox = false;
            _isPreparingDrag = false;
            _dragTarget = null;
        }

        /// <summary>
        /// 텍스트 도구가 선택되었는지 여부를 알린다.
        /// 비활성화될 때는 편집 중인 박스를 자동으로 커밋한다.
        /// </summary>
        public void SetActive(bool active)
        {
            if (!active)
            {
                CommitEditing();
            }

            IsActive = active;
            UpdateBorder(_activeTextBox);
        }

        /// <summary>
        /// 새 텍스트 박스에 사용할 기본 폰트 크기를 지정한다.
        /// 최소값 이하가 들어오면 안전한 값으로 대체한다.
        /// </summary>
        public void SetDefaultFontSize(double size)
        {
            if (size <= 0)
                size = 12;
            DefaultFontSize = size;
        }

        /// <summary>
        /// 기본 전경색(글자색)을 지정하고 Freezable 속성을 최대한 활용한다.
        /// </summary>
        public void SetDefaultForeground(Brush brush)
        {
            if (brush == null)
                brush = Brushes.Black;

            DefaultForeground = brush.CloneCurrentValue();
            if (DefaultForeground.CanFreeze)
                DefaultForeground.Freeze();
        }

        /// <summary>
        /// 사용자가 마지막으로 선택한 TextBox를 반환한다.
        /// </summary>
        public TextBox GetActiveTextBox()
        {
            return _activeTextBox;
        }

        /// <summary>
        /// 외부에서 특정 TextBox에 대해 즉시 편집 모드를 시작하고 필요 시 전체 선택한다.
        /// </summary>
        public void BeginEditingExistingTextBox(TextBox textBox, bool selectAll)
        {
            if (textBox == null)
                return;

            SetActiveTextBox(textBox);
            BeginEditing(textBox, selectAll);
        }

        /// <summary>
        /// 현재 편집 중인 TextBox를 반환한다. 없으면 null.
        /// </summary>
        public TextBox GetEditingTextBox()
        {
            return _editingTextBox;
        }

        /// <summary>
        /// Enter/Escape/Ctrl+Enter 등의 단축키를 처리해 텍스트 편집을 제어한다.
        /// true를 반환하면 상위 PreviewKeyDown에서 e.Handled를 설정해야 한다.
        /// </summary>
        public bool HandleKeyDown(KeyEventArgs e)
        {
            if (e == null)
                return false;

            if (IsActive && e.Key == Key.Enter && _activeTextBox != null && _editingTextBox == null)
            {
                BeginEditing(_activeTextBox, selectAll: false);
                return true;
            }

            if (_editingTextBox != null && e.Key == Key.Escape)
            {
                CommitEditing();
                return true;
            }

            return false;
        }

        /// <summary>
        /// TextBox가 속한 InkCanvas를 조회한다.
        /// </summary>
        public bool TryGetOwnerCanvas(TextBox textBox, out InkCanvas canvas)
        {
            if (textBox != null && _textOwners.TryGetValue(textBox, out canvas))
            {
                return true;
            }
            canvas = null;
            return false;
        }

        /// <summary>
        /// 주석 파일에서 복원된 TextBox에 이벤트/스타일을 다시 설정한다.
        /// </summary>
        public void RegisterExistingTextBox(InkCanvas canvas, TextBox textBox)
        {
            if (canvas == null || textBox == null)
                return;

            if (!_textOwners.ContainsKey(textBox))
            {
                InitializeTextBoxAppearance(textBox, applyDefaultStyle: false);
                WireTextBoxEvents(textBox);
            }

            _textOwners[textBox] = canvas;
            UpdateBorder(textBox);
            UpdateAutoSize(textBox);
        }

        /// <summary>
        /// InkCanvas.Children에서 TextBox가 제거되었음을 알리는 콜백.
        /// 내부 사전과 활성 상태를 함께 정리한다.
        /// </summary>
        public void NotifyElementRemoved(UIElement element)
        {
            if (element is TextBox textBox && _textOwners.ContainsKey(textBox))
            {
                UnwireTextBox(textBox);
                _textOwners.Remove(textBox);

                if (_activeTextBox == textBox)
                {
                    StopActiveTextDrag();
                    _activeTextBox = null;
                    ActiveTextBoxChanged?.Invoke(_activeTextBox);
                }
                if (_editingTextBox == textBox)
                {
                    _editingTextBox = null;
                    _editingOriginalText = null;
                    EditingStateChanged?.Invoke(textBox, false);
                }
            }
        }

        /// <summary>
        /// TextBox에 강제로 폰트 크기를 적용하고 자동 크기 조정을 다시 계산한다.
        /// </summary>
        public void ApplyFontSize(TextBox textBox, double size)
        {
            if (textBox == null || size <= 0)
                return;

            textBox.FontSize = size;
        }

        /// <summary>
        /// TextBox 폰트 색상을 설정한다. 입력 Brush가 Freezable이면 복제해서 보관한다.
        /// </summary>
        public void ApplyForeground(TextBox textBox, Brush brush)
        {
            if (textBox == null || brush == null)
                return;

            var actual = brush.CloneCurrentValue();
            if (actual.CanFreeze)
                actual.Freeze();
            textBox.Foreground = actual;
        }

        private void Canvas_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!IsActive)
                return;

            var canvas = sender as InkCanvas;
            if (canvas == null)
                return;

            Point position = e.GetPosition(canvas);
            var hit = HitTestTextBox(canvas, position);
            if (hit != null)
            {
                SetActiveTextBox(hit);
                if (e.ClickCount >= 2)
                {
                    BeginEditing(hit, selectAll: true);
                }
                e.Handled = true;
                return;
            }

            var textBox = CreateTextBox(canvas, position);
            SetActiveTextBox(textBox);
            BeginEditing(textBox, selectAll: true);
            e.Handled = true;
        }

        private TextBox CreateTextBox(InkCanvas canvas, Point position)
        {
            var textBox = new TextBox();
            InitializeTextBoxAppearance(textBox, applyDefaultStyle: true);

            InkCanvas.SetLeft(textBox, Math.Max(0, position.X));
            InkCanvas.SetTop(textBox, Math.Max(0, position.Y));

            canvas.Children.Add(textBox);
            RegisterExistingTextBox(canvas, textBox);
            TextBoxCreated?.Invoke(canvas, textBox);
            return textBox;
        }

        private void InitializeTextBoxAppearance(TextBox textBox, bool applyDefaultStyle)
        {
            if (textBox == null)
                return;

            textBox.Tag = TextElementTag;
            textBox.Text = textBox.Text ?? string.Empty;
            textBox.MinWidth = Math.Max(120, DefaultFontSize * 3);
            textBox.MinHeight = 32;
            if (double.IsNaN(textBox.Width) || textBox.Width == 0)
                textBox.Width = textBox.MinWidth;
            if (double.IsNaN(textBox.Height) || textBox.Height == 0)
                textBox.Height = textBox.MinHeight;

            textBox.Padding = new Thickness(4);
            textBox.BorderThickness = new Thickness(1);
            textBox.Background = Brushes.Transparent;
            textBox.AcceptsReturn = true;
            textBox.TextWrapping = TextWrapping.Wrap;
            textBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            textBox.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            textBox.HorizontalContentAlignment = HorizontalAlignment.Left;
            textBox.VerticalContentAlignment = VerticalAlignment.Top;
            textBox.FocusVisualStyle = null;
            textBox.Cursor = Cursors.IBeam;
            textBox.IsReadOnly = true;

            if (applyDefaultStyle || textBox.Foreground == null)
            {
                var foreground = DefaultForeground.CloneCurrentValue();
                if (foreground.CanFreeze) foreground.Freeze();
                textBox.Foreground = foreground;
            }

            if (applyDefaultStyle || textBox.FontFamily == null)
            {
                textBox.FontFamily = DefaultFontFamily;
            }

            if (applyDefaultStyle || double.IsNaN(textBox.FontSize) || textBox.FontSize <= 0)
            {
                textBox.FontSize = DefaultFontSize;
            }
        }

        private void WireTextBoxEvents(TextBox textBox)
        {
            textBox.PreviewKeyDown += TextBox_PreviewKeyDown;
            textBox.TextChanged += TextBox_TextChanged;
            textBox.SizeChanged += TextBox_SizeChanged;
            textBox.PreviewMouseLeftButtonDown += TextBox_PreviewMouseLeftButtonDown;
            textBox.PreviewMouseMove += TextBox_PreviewMouseMove;
            textBox.PreviewMouseLeftButtonUp += TextBox_PreviewMouseLeftButtonUp;
            textBox.LostMouseCapture += TextBox_LostMouseCapture;
        }

        private void UnwireTextBox(TextBox textBox)
        {
            textBox.PreviewKeyDown -= TextBox_PreviewKeyDown;
            textBox.TextChanged -= TextBox_TextChanged;
            textBox.SizeChanged -= TextBox_SizeChanged;
            textBox.PreviewMouseLeftButtonDown -= TextBox_PreviewMouseLeftButtonDown;
            textBox.PreviewMouseMove -= TextBox_PreviewMouseMove;
            textBox.PreviewMouseLeftButtonUp -= TextBox_PreviewMouseLeftButtonUp;
            textBox.LostMouseCapture -= TextBox_LostMouseCapture;
        }

        private void TextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender != _editingTextBox)
                return;

            if (e.Key == Key.Escape)
            {
                CommitEditing();
                e.Handled = true;
            }
        }

        private void BeginEditing(TextBox textBox, bool selectAll)
        {
            if (textBox == null)
                return;

            if (_editingTextBox != null && _editingTextBox != textBox)
            {
                CommitEditing();
            }

            _editingTextBox = textBox;
            _editingOriginalText = textBox.Text ?? string.Empty;
            StopActiveTextDrag();

            textBox.IsReadOnly = false;
            textBox.Focus();
            Keyboard.Focus(textBox);

            if (selectAll)
            {
                textBox.Dispatcher.BeginInvoke(new Action(textBox.SelectAll));
            }

            UpdateBorder(textBox);
            EditingStateChanged?.Invoke(textBox, true);
        }

        private void CommitEditing()
        {
            if (_editingTextBox == null)
                return;

            var textBox = _editingTextBox;
            string original = _editingOriginalText ?? string.Empty;
            string current = textBox.Text ?? string.Empty;
            bool textChanged = !string.Equals(original, current, StringComparison.Ordinal);

            _editingTextBox = null;
            _editingOriginalText = null;

            textBox.IsReadOnly = true;
            textBox.Select(0, 0);
            UpdateBorder(textBox);
            EditingStateChanged?.Invoke(textBox, false);

            if (string.IsNullOrWhiteSpace(current))
            {
                RemoveTextBox(textBox);
                return;
            }

            UpdateAutoSize(textBox);

            if (textChanged)
            {
                if (_textOwners.TryGetValue(textBox, out InkCanvas canvas))
                {
                    TextCommitted?.Invoke(canvas, textBox, original, current);
                }
            }
        }

        private void SetActiveTextBox(TextBox textBox)
        {
            if (_activeTextBox == textBox)
                return;

            _activeTextBox = textBox;
            BringToFront(textBox);
            UpdateBorder(textBox);
            ActiveTextBoxChanged?.Invoke(_activeTextBox);
            UpdateAutoSize(textBox);
        }

        private void BringToFront(TextBox textBox)
        {
            if (textBox == null)
                return;

            if (_textOwners.TryGetValue(textBox, out InkCanvas canvas))
            {
                if (canvas.Children.Contains(textBox))
                {
                    canvas.Children.Remove(textBox);
                    canvas.Children.Add(textBox);
                }
            }
        }

        private TextBox HitTestTextBox(InkCanvas canvas, Point point)
        {
            for (int i = canvas.Children.Count - 1; i >= 0; i--)
            {
                if (canvas.Children[i] is TextBox textBox)
                {
                    var tag = textBox.Tag as string;
                    if (tag != TextElementTag)
                        continue;

                    Rect bounds = VisualTreeHelper.GetDescendantBounds(textBox);
                    GeneralTransform transform = textBox.TransformToVisual(canvas);
                    if (transform != null)
                    {
                        Rect transformed = transform.TransformBounds(bounds);
                        if (transformed.Contains(point))
                            return textBox;
                    }
                }
            }

            return null;
        }

        private void UpdateBorder(TextBox textBox)
        {
            if (textBox == null)
                return;

            bool highlight = textBox == _editingTextBox || (IsActive && textBox == _activeTextBox);
            textBox.BorderBrush = highlight ? _activeBorderBrush : _inactiveBorderBrush;
        }

        private void TextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                UpdateAutoSize(textBox);
            }
        }

        private void TextBox_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                UpdateAutoSize(textBox);
            }
        }

        /// <summary>
        /// 폰트 크기 변경 등으로 텍스트 박스 크기가 달라졌을 때 AutoSize를 다시 계산한다.
        /// </summary>
        public void RefreshLayout(TextBox textBox)
        {
            UpdateAutoSize(textBox);
        }

        private void UpdateAutoSize(TextBox textBox)
        {
            if (textBox == null)
                return;

            double minWidth = Math.Max(80, textBox.FontSize * 2.5);
            if (textBox.MinWidth < minWidth)
                textBox.MinWidth = minWidth;
            if (double.IsNaN(textBox.Width) || textBox.Width < textBox.MinWidth)
                textBox.Width = textBox.MinWidth;

            textBox.Dispatcher.BeginInvoke(new Action(() =>
            {
                double desiredHeight = textBox.ExtentHeight + textBox.Padding.Top + textBox.Padding.Bottom + 6;
                if (double.IsNaN(desiredHeight) || desiredHeight < textBox.MinHeight)
                    desiredHeight = textBox.MinHeight;
                double currentHeight = double.IsNaN(textBox.Height) ? 0 : textBox.Height;
                if (Math.Abs(currentHeight - desiredHeight) > 0.5)
                    textBox.Height = desiredHeight;

                double availableWidth = textBox.ActualWidth - textBox.Padding.Left - textBox.Padding.Right;
                if (availableWidth > textBox.FontSize * 1.2)
                {
                    textBox.TextWrapping = TextWrapping.Wrap;
                }
                else
                {
                    textBox.TextWrapping = TextWrapping.WrapWithOverflow;
                    double enforcedWidth = Math.Max(textBox.MinWidth, textBox.FontSize * 1.6);
                    if (double.IsNaN(textBox.Width) || textBox.Width < enforcedWidth)
                        textBox.Width = enforcedWidth;
                }
            }), DispatcherPriority.Background);
        }

        private void TextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is TextBox textBox))
                return;

            if (!IsActive || _activeTextBox != textBox)
                return;

            if (_editingTextBox == textBox)
                return;

            if (!_textOwners.TryGetValue(textBox, out InkCanvas canvas))
                return;

            _isPreparingDrag = true;
            _dragCanvas = canvas;
            _dragTarget = textBox;
            _dragStartPoint = e.GetPosition(canvas);
            _dragStartLeft = InkCanvas.GetLeft(textBox);
            _dragStartTop = InkCanvas.GetTop(textBox);
            if (double.IsNaN(_dragStartLeft)) _dragStartLeft = _dragStartPoint.X;
            if (double.IsNaN(_dragStartTop)) _dragStartTop = _dragStartPoint.Y;
        }

        private void TextBox_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!(sender is TextBox textBox))
                return;

            if (_isPreparingDrag && _dragCanvas != null && _dragTarget == textBox && e.LeftButton == MouseButtonState.Pressed)
            {
                Point current = e.GetPosition(_dragCanvas);
                if (Math.Abs(current.X - _dragStartPoint.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(current.Y - _dragStartPoint.Y) >= SystemParameters.MinimumVerticalDragDistance)
                {
                    _isDraggingActiveTextBox = true;
                    textBox.CaptureMouse();
                    e.Handled = true;
                }
            }

            if (!_isDraggingActiveTextBox || _dragCanvas == null || textBox != _dragTarget)
                return;

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                StopActiveTextDrag();
                return;
            }

            Point moveCurrent = e.GetPosition(_dragCanvas);
            Vector delta = moveCurrent - _dragStartPoint;
            InkCanvas.SetLeft(textBox, _dragStartLeft + delta.X);
            InkCanvas.SetTop(textBox, _dragStartTop + delta.Y);
            e.Handled = true;
        }

        private void TextBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingActiveTextBox)
            {
                StopActiveTextDrag();
                e.Handled = true;
            }
            else
            {
                _isPreparingDrag = false;
                _dragCanvas = null;
                _dragTarget = null;
            }
        }

        private void TextBox_LostMouseCapture(object sender, MouseEventArgs e)
        {
            StopActiveTextDrag();
        }

        private void StopActiveTextDrag()
        {
            if (_isDraggingActiveTextBox)
            {
                _dragTarget?.ReleaseMouseCapture();
            }

            _isDraggingActiveTextBox = false;
            _isPreparingDrag = false;
            _dragCanvas = null;
            _dragTarget = null;
        }

        private void RemoveTextBox(TextBox textBox)
        {
            if (textBox == null)
                return;

            if (!_textOwners.TryGetValue(textBox, out InkCanvas canvas) || canvas == null)
                return;

            if (canvas.Children.Contains(textBox))
            {
                canvas.Children.Remove(textBox);
            }

            TextBoxRemoved?.Invoke(canvas, textBox);
            NotifyElementRemoved(textBox);
        }
    }
}
