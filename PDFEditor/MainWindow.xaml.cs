using Microsoft.Win32;
using PDFEditor.Ink;
using PDFEditor.Shapes;
using PdfiumViewer;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace PDFEditor
{
    public partial class MainWindow : Window
    {
        private PdfDocument _pdf;                                  // PdfiumViewer 문서 핸들
        private int _currentPage = 0;                              // 현재 페이지 (0-based)
        private double _baseScale = 1.0;                           // 폭 맞춤 기준 배율
        private bool _fitWidthReady = false;                      // 기준 배율 계산 여부
        private double _pageImageWidth = 0;                       // 첫 페이지 실제 픽셀 폭
        private InkToolManager _inkTool = new InkToolManager();    // 펜/형광펜/지우개 제어
        private Dictionary<int, InkCanvas> _pageInkCanvases = new Dictionary<int, InkCanvas>(); // 페이지별 InkCanvas
        private ShapeToolManager _shapeTool = new ShapeToolManager(); // 도형 그리기/지우기 제어
        private SelectionToolManager _selectionTool = new SelectionToolManager();
        private PenCursorManager _penCursorManager = new PenCursorManager();
        private Dictionary<InkCanvas, int> _inkCanvasPageIndex = new Dictionary<InkCanvas, int>();
        private Stack<IUndoRedoAction> _undoStack = new Stack<IUndoRedoAction>();
        private Stack<IUndoRedoAction> _redoStack = new Stack<IUndoRedoAction>();
        private bool _recordHistory = true;
        private class CopiedShapeInfo
        {
            public string Xaml;
            public double Left;
            public double Top;
        }

        private List<CopiedShapeInfo> _copiedShapes = new List<CopiedShapeInfo>();
        private List<Stroke> _copiedStrokes = new List<Stroke>();
        public MainWindow()
        {
            InitializeComponent();
            this.SizeChanged += MainWindow_SizeChanged;
            this.PreviewKeyDown += MainWindow_PreviewKeyDown;
            PdfScrollViewer.PreviewMouseWheel += PdfScrollViewer_PreviewMouseWheel;
            _shapeTool.ShapeCreated += ShapeTool_ShapeCreated;
            _shapeTool.ShapeRemoved += ShapeTool_ShapeRemoved;
            _penCursorManager.SetThickness(ThicknessSlider?.Value ?? 3.0);
            _penCursorManager.SetEnabled(true);
        }

        /// <summary>
        /// 창 크기가 바뀌면 현재 페이지 위치가 틀어질 수 있으므로 다시 스크롤.
        /// </summary>
        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_pdf == null) return;
            ScrollToPage(_currentPage);
        }
        /// <summary>
        /// PDF 파일 선택 후 PdfiumViewer로 로드하고 UI 초기화.
        /// </summary>
        private void OpenPdf_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "PDF Files|*.pdf"
            };

            if (dlg.ShowDialog() == true)
            {
                _pdf?.Dispose();
                _pdf = PdfDocument.Load(dlg.FileName);
                _currentPage = 0;

                RenderAllPages();
                UpdatePageInfo();

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    FitWidthToViewer();
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        /// <summary>
        /// PDF 페이지 전체를 Image + InkCanvas 조합으로 메모리에 렌더링한다.
        /// 페이지마다 InkCanvas를 따로 저장하여 도형/필기 상태를 유지.
        /// </summary>
        private void RenderAllPages()
        {
            PagesPanel.Children.Clear();
            _pageInkCanvases.Clear();
            _penCursorManager.Clear();
            _inkCanvasPageIndex.Clear();
            ClearHistory();
            _copiedShapes.Clear();
            _copiedStrokes.Clear();

            // 줌 초기화
            ZoomTransform.ScaleX = 1.0;
            ZoomTransform.ScaleY = 1.0;
            _baseScale = 1.0;
            _fitWidthReady = false;

            if (_pdf == null) return;

            int pageCount = _pdf.PageCount;
            float dpi = 300f;   // PPT 글씨까지 선명하게 보이도록

            for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
            {
                using (var img = _pdf.Render(pageIndex, dpi, dpi, false))
                {
                    var bitmapSource = ImageToImageSource(img);

                    // 첫 페이지 이미지 폭 저장
                    if (pageIndex == 0)
                    {
                        _pageImageWidth = bitmapSource.PixelWidth;
                    }

                    //실제 페이지 크기 (DIP 단위)
                    double pageWidth = bitmapSource.Width;
                    double pageHeight = bitmapSource.Height;

                    var wpfImage = new Image
                    {
                        Source = bitmapSource,
                        Width = pageWidth,
                        Height = pageHeight,
                        Stretch = Stretch.Fill,
                        HorizontalAlignment = HorizontalAlignment.Center
                    };
                    RenderOptions.SetBitmapScalingMode(wpfImage, BitmapScalingMode.HighQuality);

                    var ink = new InkCanvas
                    {
                        Background = Brushes.Transparent,
                        Width = pageWidth,
                        Height = pageHeight,
                        ClipToBounds = true //필기 밖으로 나가는 부분 자르기
                    };
                    ink.RequestBringIntoView += SuppressBringIntoView; // 페이지 클릭 시 자동 스크롤 방지
                    ink.StrokeCollected += InkCanvas_StrokeCollected;
                    ink.Strokes.StrokesChanged += InkCanvas_StrokesChanged;

                    _inkTool.SetTool(_inkTool.CurrentTool, ink); // 현재 선택된 필기 도구 적용
                    ApplyCurrentColorAndThicknessToInkCanvas(ink); // 색/두께 동기화

                    _shapeTool.AttachCanvas(ink); // 도형 드로잉/지우기 이벤트 연결
                    _selectionTool.AttachCanvas(ink); // 선택 도구 이벤트 연결
                    _penCursorManager.AttachCanvas(ink); // 펜 위치 표시 연결

                    var pageGrid = new Grid 
                    {
                        Width = pageWidth,
                        Height = pageHeight,
                        ClipToBounds = true
                    };
                    pageGrid.Children.Add(wpfImage);
                    pageGrid.Children.Add(ink);

                    var pageBorder = new Border
                    {
                        Width = pageWidth, //패딩 포함한 "용지" 사이즈"
                        Height = pageHeight,
                        Background = Brushes.White,
                        BorderBrush = Brushes.LightGray,
                        BorderThickness = new Thickness(1),
                        Margin = new Thickness(0, 10, 0, 10),
                        Padding = new Thickness(0),
                        HorizontalAlignment = HorizontalAlignment.Center
                    };
                    pageBorder.RequestBringIntoView += SuppressBringIntoView;

                    pageBorder.Child = pageGrid;

                    PagesPanel.Children.Add(pageBorder);

                    _pageInkCanvases[pageIndex] = ink;
                    _inkCanvasPageIndex[ink] = pageIndex;
                }
            }
            bool penLikeTool = _inkTool.CurrentTool == DrawTool.Pen || _inkTool.CurrentTool == DrawTool.Highlighter;
            _penCursorManager.SetEnabled(penLikeTool);

            ScrollToPage(0);
        }

        /// <summary>
        /// PdfiumViewer가 제공하는 System.Drawing.Image를 BitmapImage로 변환한다.
        /// InkCanvas 위에 덮을 Image 컨트롤은 WPF BitmapSource만 지원하므로 메모리 스트림을 경유한다.
        /// </summary>
        private BitmapImage ImageToImageSource(System.Drawing.Image image)
        {
            using (var ms = new MemoryStream())
            {
                image.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ms.Position = 0;

                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.StreamSource = ms;
                bitmapImage.EndInit();
                return bitmapImage;
            }
        }

        /// <summary>
        /// 현재 페이지 표시 텍스트 업데이트 (ex: 3 / 12)
        /// </summary>
        private void UpdatePageInfo()
        {
            if (_pdf == null)
            {
                PageInfoText.Text = "";
                return;
            }

            PageInfoText.Text = $"{_currentPage + 1} / {_pdf.PageCount}";
            PageInput.Text = (_currentPage + 1).ToString();
        }

        /// <summary>
        /// 특정 페이지 인덱스를 현재 페이지로 설정하고 페이지 정보 및 InkCanvas 상태를 갱신한다.
        /// PdfScrollViewer의 실제 스크롤 이동은 Border.RequestBringIntoView 억제 로직으로 제어한다.
        /// </summary>
        private void ScrollToPage(int pageIndex)
        {
            if (_pdf == null) return;
            if (pageIndex < 0 || pageIndex >= _pdf.PageCount) return;

            _currentPage = pageIndex;
            UpdatePageInfo();

            // 페이지가 바뀌면 InkCanvas 레퍼런스도 달라지므로 현재 선택된 도구를 다시 적용한다.
            _inkTool.SetTool(_inkTool.CurrentTool, GetCurrentInkCanvas());

            ScrollCurrentPageIntoView();
        }

        /// <summary>
        /// 다음 페이지 버튼 클릭 시 한 페이지 앞으로 이동한다.
        /// 실제 스크롤 이동 대신 페이지 인덱스 및 InkCanvas 상태만 갱신한다.
        /// </summary>
        private void NextPage_Click(object sender, RoutedEventArgs e)
        {
            if (_pdf == null) return;
            ScrollToPage(_currentPage + 1);
        }

        /// <summary>
        /// 이전 페이지 버튼 클릭 시 한 페이지 뒤로 이동한다.
        /// </summary>
        private void PrevPage_Click(object sender, RoutedEventArgs e)
        {
            if (_pdf == null) return;
            ScrollToPage(_currentPage - 1);
        }

        /// <summary>
        /// 페이지 번호 입력 후 이동 버튼.
        /// 잘못된 번호면 아무 작업도 하지 않는다(추가 검증/메시지 필요 시 여기서 처리).
        /// </summary>
        private void GoToPage_Click(object sender, RoutedEventArgs e)
        {
            if (_pdf == null) return;

            if (int.TryParse(PageInput.Text, out int pageNumber))
            {
                // 사용자는 1부터 입력하니까 0-based로 변환
                int targetIndex = pageNumber - 1;
                ScrollToPage(targetIndex);
            }
            else
            {
                MessageBox.Show("페이지 번호를 올바르게 입력하세요.", "오류",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        /// <summary>
        /// 줌 슬라이더 변경 시 ApplyZoom 호출.
        /// </summary>
        private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsInitialized) return;
            if (!_fitWidthReady) return;

            double percent = e.NewValue;
            ApplyZoom(percent);
        }

        /// <summary>
        /// 줌 텍스트 박스에서 Enter 입력 시 슬라이더 값 변경.
        /// </summary>
        private void ZoomTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (!_fitWidthReady) return;

            if (double.TryParse(ZoomTextBox.Text, out double percent))
            {
                if (percent < ZoomSlider.Minimum) percent = ZoomSlider.Minimum;
                if (percent > ZoomSlider.Maximum) percent = ZoomSlider.Maximum;

                ZoomSlider.Value = percent;   // 슬라이더를 통해 ApplyZoom 호출
            }
        }

        /// <summary>
        /// ScrollViewer 폭에 맞춰 기준 배율(_baseScale)을 계산하고 슬라이더 초기화.
        /// </summary>
        private void FitWidthToViewer()
        {
            if (_pdf == null) return;
            if (PagesPanel.Children.Count == 0) return;
            if (_pageImageWidth <= 0) return;     // 🔴 이미지 폭이 안 잡힌 경우

            // 레이아웃 갱신
            PdfScrollViewer.UpdateLayout();
            PagesPanel.UpdateLayout();

            double viewportWidth = PdfScrollViewer.ViewportWidth;
            if (viewportWidth <= 0) return;

            // 좌우 여백(스크롤바, Border padding 등) 약간 빼줌
            double effectiveViewport = viewportWidth;
            if (effectiveViewport <= 0) return;

            const double baseZoomFactor = 1.5; //1.0 = 딱 맞음, 1.2 = 20% 확대

            // ✅ "폭에 맞추기"에 해당하는 기준 배율 = 화면폭 / 이미지폭
            _baseScale = (effectiveViewport / _pageImageWidth)*baseZoomFactor;
            if (_baseScale <= 0) _baseScale = 1.0;

            _fitWidthReady = true;

            // 슬라이더/텍스트를 100%로 초기화
            ZoomSlider.Minimum = 50;
            ZoomSlider.Maximum = 300;
            ZoomSlider.Value = 100;
            ZoomTextBox.Text = "100";

            ApplyZoom(100);   // 100% = "폭에 맞추기"
        }

        /// <summary>
        /// 줌 슬라이더 값(%)을 받아 StackPanel LayoutTransform에 반영.
        /// </summary>
        private void ApplyZoom(double percent)
        {
            if (!_fitWidthReady) return;      // 기준 배율 아직 없음
            if (ZoomTransform == null) return;

            // 100%일 때 baseScale = 폭에 맞는 크기
            double scale = _baseScale * (percent / 100.0);

            ZoomTransform.ScaleX = scale;
            ZoomTransform.ScaleY = scale;

            if (ZoomTextBox != null)
                ZoomTextBox.Text = ((int)percent).ToString();
        }

        //현재 페이지 InkCanvas 가져오기
        private InkCanvas GetCurrentInkCanvas()
        {
            if (_pageInkCanvases.TryGetValue(_currentPage, out InkCanvas inkCanvas))
            {
                return inkCanvas;
            }

            return null;
        }

        private InkCanvas GetInkCanvas(int pageIndex)
        {
            if (_pageInkCanvases.TryGetValue(pageIndex, out InkCanvas inkCanvas))
            {
                return inkCanvas;
            }
            return null;
        }

        private bool TryGetPageIndex(InkCanvas canvas, out int pageIndex)
        {
            if (canvas != null && _inkCanvasPageIndex.TryGetValue(canvas, out pageIndex))
            {
                return true;
            }
            pageIndex = -1;
            return false;
        }
        /// <summary>
        /// 필기/도형/지우개 토글 버튼 공용 핸들러.
        /// 필기 도구 선택 시 모든 페이지 InkCanvas에 EditingMode를 일괄 적용한다.
        /// 도형 도구 선택 시 ShapeToolManager가 직접 InkCanvas 이벤트를 잡는다.
        /// </summary>
        private void ToolButton_Click(object sender, RoutedEventArgs e)
        {
            var clicked = sender as ToggleButton;
            if (clicked == null) return;

            // 1) 모든 버튼 체크 해제
            if (SelectButton != null) SelectButton.IsChecked = false;
            if (PenButton != null) PenButton.IsChecked = false;
            if (HighlighterButton != null) HighlighterButton.IsChecked = false;
            if (EraserButton != null) EraserButton.IsChecked = false;
            if (RectButton != null) RectButton.IsChecked = false;
            if (EllipseButton != null) EllipseButton.IsChecked = false;
            if (LineButton != null) LineButton.IsChecked = false;
            if (TriangleButton != null) TriangleButton.IsChecked = false;

            clicked.IsChecked = true;

            string tag = clicked.Tag as string ?? "";
            // 🔹 1) 선택 도구
            if (tag == "Select")
            {
                // 펜/도형 모드는 모두 비활성화
                _shapeTool.SetShape(ShapeType.None);
                _shapeTool.SetShapeEraseMode(false);
                _selectionTool.SetEnabled(true);
                _penCursorManager.SetEnabled(false);

                // InkCanvas 필기/지우개 모두 끄기 (선택만 할 수 있게)
                foreach (InkCanvas canvas in _pageInkCanvases.Values)
                {
                    canvas.EditingMode = InkCanvasEditingMode.None;
                }

                return;
            }
            else
            {
                _selectionTool.SetEnabled(false);
            }
            // 🔹 2) 펜 / 형광펜 / 지우개
            if (tag == "Pen" || tag == "Highlighter" || tag == "Eraser")
            {
                // 도형 그리기 모드는 끄기
                _shapeTool.SetShape(ShapeType.None);

                // 도형 지우개 모드는 "지우개일 때만" 켜기
                _shapeTool.SetShapeEraseMode(tag == "Eraser");

                foreach (InkCanvas canvas in _pageInkCanvases.Values)
                {
                    if (tag == "Pen")
                        _inkTool.SetTool(DrawTool.Pen, canvas);
                    else if (tag == "Highlighter")
                        _inkTool.SetTool(DrawTool.Highlighter, canvas);
                    else if (tag == "Eraser")
                        _inkTool.SetTool(DrawTool.Eraser, canvas);
                }

                _penCursorManager.SetEnabled(tag == "Pen" || tag == "Highlighter");
                return;
            }

            // 🔹 3) 도형 도구들 (사각형/원/선/삼각형)
            foreach (InkCanvas canvas in _pageInkCanvases.Values)
            {
                // 도형 그릴 때는 InkCanvas 필기 막기
                canvas.EditingMode = InkCanvasEditingMode.None;
            }

            // 도형 선택 시에는 도형 지우개 모드 끔
            _shapeTool.SetShapeEraseMode(false);
            _penCursorManager.SetEnabled(false);

            switch (tag)
            {
                case "Rectangle":
                    _shapeTool.SetShape(ShapeType.Rectangle);
                    break;
                case "Ellipse":
                    _shapeTool.SetShape(ShapeType.Ellipse);
                    break;
                case "Line":
                    _shapeTool.SetShape(ShapeType.Line);
                    break;
                case "Triangle":
                    _shapeTool.SetShape(ShapeType.Triangle);
                    break;
                default:
                    _shapeTool.SetShape(ShapeType.None);
                    break;
            }
        }

        /// <summary>
        /// 색상 콤보 선택 시 InkToolManager/ShapeToolManager에 현재 색상을 반영한다.
        /// </summary>
        private void ColorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_inkTool == null)
                return;
        
            ComboBoxItem item = ColorComboBox.SelectedItem as ComboBoxItem;
            if (item == null || item.Tag == null)
                return;

            string colorName = item.Tag.ToString();                 // "Red", "Blue" 같은 문자열
            Color color = (Color)ColorConverter.ConvertFromString(colorName);

            //상태만 먼저 저장
            _inkTool.SetColor(color, null);

            var brush = new SolidColorBrush(color);
            _shapeTool.SetStroke(brush, ThicknessSlider?.Value ?? 3.0);

            // 모든 페이지의 InkCanvas에 적용
            foreach (InkCanvas canvas in _pageInkCanvases.Values)
            {
                _inkTool.SetColor(color, canvas);
            }
        }
        /// <summary>
        /// 두께 슬라이더 변경 시 펜 도구/도형 도구 두께를 동시에 갱신한다.
        /// </summary>
        private void ThicknessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_inkTool == null)
                return;

            double thickness = e.NewValue;

            // 두께 상태를 InkToolManager에 반영
            _inkTool.SetThickness(thickness,null);

            Color color = Colors.Black;
            ComboBoxItem colorItem = ColorComboBox.SelectedItem as ComboBoxItem;

            if (colorItem != null && colorItem.Tag != null)
            {
                string colorName = colorItem.Tag.ToString();
                color = (Color)ColorConverter.ConvertFromString(colorName);
            }
            _shapeTool.SetStroke(new SolidColorBrush(color), thickness);
            _penCursorManager.SetThickness(thickness);

            // 모든 페이지의 InkCanvas에 적용
            foreach (InkCanvas canvas in _pageInkCanvases.Values)
            {
                _inkTool.SetThickness(thickness, canvas);
            }

            // UI에 표시용 텍스트 업데이트
            if (ThicknessValueText != null)
            {
                ThicknessValueText.Text = ((int)thickness).ToString() + "px";
            }
        }

        /// <summary>
        /// 새로 생성된 InkCanvas에 현재 색상/두께를 동기화한다.
        /// </summary>
        private void ApplyCurrentColorAndThicknessToInkCanvas(InkCanvas ink)
        {
            if (ink == null) return;

            // 색상
            Color color = Colors.Black;
            ComboBoxItem colorItem = ColorComboBox.SelectedItem as ComboBoxItem;
            if (colorItem != null && colorItem.Tag != null)
            {
                string colorName = colorItem.Tag.ToString();
                color = (Color)ColorConverter.ConvertFromString(colorName);
            }

            // 두께
            double thickness = 3;
            if (ThicknessSlider != null)
            {
                thickness = ThicknessSlider.Value;
            }

            // InkToolManager에 반영
            _inkTool.SetColor(color, ink);
            _inkTool.SetThickness(thickness, ink);

            _shapeTool.SetStroke(new SolidColorBrush(color), thickness);
        }

        /// <summary>
        /// PDF 페이지 클릭 시 ScrollViewer가 강제 스크롤되는 기본 동작을 방지.
        /// </summary>
        private bool _allowBringIntoView = false;
        private void SuppressBringIntoView(object sender, RequestBringIntoViewEventArgs e)
        {
            if (!_allowBringIntoView)
            {
                e.Handled = true;
            }
        }

        /// <summary>
        /// 현재 선택된 페이지가 ScrollViewer 가운데쯤 보이도록 스크롤한다.
        /// </summary>
        private void ScrollCurrentPageIntoView()
        {
            if (PdfScrollViewer == null || PagesPanel == null) return;
            if (_currentPage < 0 || _currentPage >= PagesPanel.Children.Count) return;

            if (PagesPanel.Children[_currentPage] is FrameworkElement pageElement)
            {
                // 레이아웃이 최신 상태가 아니면 위치 계산이 틀어지므로 갱신
                PdfScrollViewer.UpdateLayout();
                PagesPanel.UpdateLayout();

                if (!pageElement.IsLoaded)
                {
                    Dispatcher.BeginInvoke(new Action(ScrollCurrentPageIntoView),
                        System.Windows.Threading.DispatcherPriority.Loaded);
                    return;
                }

                try
                {
                    _allowBringIntoView = true;
                    pageElement.BringIntoView();
                }
                finally
                {
                    _allowBringIntoView = false;
                }
            }
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            ModifierKeys mods = Keyboard.Modifiers;
            bool ctrl = (mods & ModifierKeys.Control) == ModifierKeys.Control;
            if (ctrl)
            {
                switch (e.Key)
                {
                    case Key.Z:
                        if ((mods & ModifierKeys.Shift) == ModifierKeys.Shift)
                        {
                            PerformRedo();
                        }
                        else
                        {
                            PerformUndo();
                        }
                        e.Handled = true;
                        break;
                    case Key.C:
                        CopySelectedShape();
                        e.Handled = true;
                        break;
                    case Key.V:
                        PasteCopiedShape();
                        e.Handled = true;
                        break;
                    case Key.Add:
                    case Key.OemPlus:
                        AdjustZoom(+10);
                        e.Handled = true;
                        break;
                    case Key.Subtract:
                    case Key.OemMinus:
                        AdjustZoom(-10);
                        e.Handled = true;
                        break;
                }
            }

            if (e.Handled)
                return;

            if (e.Key == Key.Delete || e.Key == Key.Back)
            {
                var focusedElement = FocusManager.GetFocusedElement(this);
                if (focusedElement is TextBoxBase || focusedElement is PasswordBox)
                    return;

                DeleteCurrentSelection();
                e.Handled = true;
            }
        }

        private void PdfScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
                return;

            e.Handled = true;
            if (e.Delta > 0)
                AdjustZoom(+10);
            else if (e.Delta < 0)
                AdjustZoom(-10);
        }

        private void AdjustZoom(double delta)
        {
            if (!_fitWidthReady || ZoomSlider == null)
                return;

            double target = ZoomSlider.Value + delta;
            if (target < ZoomSlider.Minimum) target = ZoomSlider.Minimum;
            if (target > ZoomSlider.Maximum) target = ZoomSlider.Maximum;
            ZoomSlider.Value = target;
        }

        private void CopySelectedShape()
        {
            var canvas = GetCurrentInkCanvas();
            if (canvas == null) return;

            var elements = _selectionTool.GetSelectedElementsSnapshot(canvas);
            var strokes = _selectionTool.GetSelectedStrokesSnapshot(canvas);
            if (elements.Count == 0 && strokes.Count == 0)
                return;

            var copiedShapes = new List<CopiedShapeInfo>();
            foreach (var element in elements)
            {
                if (element == null)
                    continue;

                try
                {
                    var info = new CopiedShapeInfo
                    {
                        Xaml = XamlWriter.Save(element)
                    };
                    var topLeft = GetElementTopLeftOnCanvas(canvas, element);
                    info.Left = topLeft.X;
                    info.Top = topLeft.Y;
                    copiedShapes.Add(info);
                }
                catch
                {
                    // skip element
                }
            }

            var copiedStrokes = new List<Stroke>();
            foreach (var stroke in strokes)
            {
                if (stroke == null)
                    continue;
                copiedStrokes.Add(stroke.Clone());
            }

            if (copiedShapes.Count == 0 && copiedStrokes.Count == 0)
                return;

            _copiedShapes = copiedShapes;
            _copiedStrokes = copiedStrokes;
        }

        private void PasteCopiedShape()
        {
            if ((_copiedShapes == null || _copiedShapes.Count == 0) &&
                (_copiedStrokes == null || _copiedStrokes.Count == 0))
                return;

            var canvas = GetCurrentInkCanvas();
            if (canvas == null)
                return;

            var newElements = new List<UIElement>();
            var newStrokes = new List<Stroke>();

            if (_copiedShapes != null)
            {
                foreach (var info in _copiedShapes)
                {
                    if (info == null || string.IsNullOrEmpty(info.Xaml))
                        continue;

                    try
                    {
                        var element = XamlReader.Parse(info.Xaml) as UIElement;
                        if (element == null)
                            continue;

                        ApplyPasteOffset(element, info);
                        canvas.Children.Add(element);
                        newElements.Add(element);
                    }
                    catch
                    {
                        // ignore element
                    }
                }
            }

            if (_copiedStrokes != null)
            {
                foreach (var template in _copiedStrokes)
                {
                    if (template == null)
                        continue;

                    var stroke = template.Clone();
                    TranslateStroke(stroke, 12, 12);
                    canvas.Strokes.Add(stroke);
                    newStrokes.Add(stroke);
                }
            }

            if (newElements.Count == 0 && newStrokes.Count == 0)
                return;

            RegisterGroupedAddition(canvas, newElements, newStrokes);
            _selectionTool.SelectItems(canvas, newElements, newStrokes);
        }

        private void DeleteCurrentSelection()
        {
            var canvas = GetCurrentInkCanvas();
            if (canvas == null)
                return;

            var selectedElements = _selectionTool.GetSelectedElementsSnapshot(canvas);
            var selectedStrokes = _selectionTool.GetSelectedStrokesSnapshot(canvas);
            if (selectedElements.Count == 0 && selectedStrokes.Count == 0)
                return;

            int pageIndex;
            if (!TryGetPageIndex(canvas, out pageIndex))
                return;

            var actions = new List<IUndoRedoAction>();

            foreach (var element in selectedElements)
            {
                if (element == null)
                    continue;

                if (!canvas.Children.Contains(element))
                    continue;

                canvas.Children.Remove(element);
                actions.Add(new ShapeRemovedAction(pageIndex, element));
            }

            if (selectedStrokes.Count > 0)
            {
                var strokesToRemove = new List<Stroke>();
                foreach (var stroke in selectedStrokes)
                {
                    if (stroke != null && canvas.Strokes.Contains(stroke))
                    {
                        strokesToRemove.Add(stroke);
                    }
                }

                if (strokesToRemove.Count > 0)
                {
                    foreach (var stroke in strokesToRemove)
                    {
                        canvas.Strokes.Remove(stroke);
                    }
                    actions.Add(new StrokeRemovedAction(pageIndex, strokesToRemove));
                }
            }

            if (actions.Count > 0)
            {
                PushUndoAction(actions.Count == 1 ? actions[0] : new CompositeAction(actions));
                _selectionTool.ClearSelection(canvas);
            }
        }

        private Point GetElementTopLeftOnCanvas(InkCanvas canvas, UIElement element)
        {
            double left = InkCanvas.GetLeft(element);
            double top = InkCanvas.GetTop(element);
            if (double.IsNaN(left) || double.IsNaN(top))
            {
                try
                {
                    Rect bounds = VisualTreeHelper.GetDescendantBounds(element);
                    GeneralTransform transform = element.TransformToVisual(canvas);
                    if (transform != null)
                    {
                        bounds = transform.TransformBounds(bounds);
                        if (double.IsNaN(left)) left = bounds.X;
                        if (double.IsNaN(top)) top = bounds.Y;
                    }
                }
                catch
                {
                    // ignore
                }
            }

            if (double.IsNaN(left)) left = 0;
            if (double.IsNaN(top)) top = 0;
            return new Point(left, top);
        }

        private void ApplyPasteOffset(UIElement element, CopiedShapeInfo info)
        {
            const double offset = 12;
            if (element is Line line)
            {
                line.X1 += offset;
                line.X2 += offset;
                line.Y1 += offset;
                line.Y2 += offset;
            }
            else if (element is Polygon polygon)
            {
                for (int i = 0; i < polygon.Points.Count; i++)
                {
                    polygon.Points[i] = new Point(polygon.Points[i].X + offset, polygon.Points[i].Y + offset);
                }
            }
            else
            {
                double left = info?.Left ?? 0;
                double top = info?.Top ?? 0;
                if (double.IsNaN(left)) left = 0;
                if (double.IsNaN(top)) top = 0;
                InkCanvas.SetLeft(element, left + offset);
                InkCanvas.SetTop(element, top + offset);
            }
        }

        private void TranslateStroke(Stroke stroke, double offsetX, double offsetY)
        {
            if (stroke == null)
                return;

            Matrix matrix = Matrix.Identity;
            matrix.Translate(offsetX, offsetY);
            stroke.Transform(matrix, false);
        }

        private void RegisterGroupedAddition(InkCanvas canvas, List<UIElement> elements, List<Stroke> strokes)
        {
            if (canvas == null)
                return;

            int pageIndex;
            if (!TryGetPageIndex(canvas, out pageIndex))
                return;

            var actions = new List<IUndoRedoAction>();
            if (elements != null)
            {
                foreach (var element in elements)
                {
                    if (element == null)
                        continue;
                    actions.Add(new ShapeAddedAction(pageIndex, element));
                }
            }

            if (strokes != null)
            {
                foreach (var stroke in strokes)
                {
                    if (stroke == null)
                        continue;
                    actions.Add(new StrokeAddedAction(pageIndex, stroke));
                }
            }

            if (actions.Count > 0)
            {
                PushUndoAction(actions.Count == 1 ? actions[0] : new CompositeAction(actions));
            }
        }

        private void PerformUndo()
        {
            if (_undoStack.Count == 0)
                return;

            var action = _undoStack.Pop();
            _recordHistory = false;
            action.Undo(this);
            _recordHistory = true;
            _redoStack.Push(action);
        }

        private void PerformRedo()
        {
            if (_redoStack.Count == 0)
                return;

            var action = _redoStack.Pop();
            _recordHistory = false;
            action.Redo(this);
            _recordHistory = true;
            _undoStack.Push(action);
        }

        private void PushUndoAction(IUndoRedoAction action)
        {
            if (!_recordHistory || action == null)
                return;

            _undoStack.Push(action);
            _redoStack.Clear();
        }

        private void ClearHistory()
        {
            _undoStack.Clear();
            _redoStack.Clear();
        }

        private void ShapeTool_ShapeCreated(InkCanvas canvas, UIElement element)
        {
            RegisterShapeAddition(canvas, element);
        }

        private void ShapeTool_ShapeRemoved(InkCanvas canvas, UIElement element)
        {
            if (canvas == null || element == null) return;
            if (_selectionTool.GetSelectedElement(canvas) == element)
            {
                _selectionTool.ClearSelection(canvas);
            }
            RegisterShapeRemoval(canvas, element);
        }

        private void RegisterShapeAddition(InkCanvas canvas, UIElement element)
        {
            if (canvas == null || element == null)
                return;

            int pageIndex;
            if (!TryGetPageIndex(canvas, out pageIndex))
                return;

            PushUndoAction(new ShapeAddedAction(pageIndex, element));
        }

        private void RegisterShapeRemoval(InkCanvas canvas, UIElement element)
        {
            if (canvas == null || element == null)
                return;

            int pageIndex;
            if (!TryGetPageIndex(canvas, out pageIndex))
                return;

            PushUndoAction(new ShapeRemovedAction(pageIndex, element));
        }

        private void InkCanvas_StrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
        {
            if (!_recordHistory) return;
            var canvas = sender as InkCanvas;
            if (canvas == null) return;

            int pageIndex;
            if (!TryGetPageIndex(canvas, out pageIndex))
                return;

            PushUndoAction(new StrokeAddedAction(pageIndex, e.Stroke));
        }

        private void InkCanvas_StrokesChanged(object sender, StrokeCollectionChangedEventArgs e)
        {
            if (!_recordHistory) return;
            if (e.Removed == null || e.Removed.Count == 0)
                return;

            var canvas = sender as InkCanvas;
            if (canvas == null) return;

            int pageIndex;
            if (!TryGetPageIndex(canvas, out pageIndex))
                return;

            var removed = new List<Stroke>();
            foreach (var stroke in e.Removed)
            {
                removed.Add(stroke);
            }

            if (removed.Count > 0)
            {
                PushUndoAction(new StrokeRemovedAction(pageIndex, removed));
            }
        }

        private void RemoveShapeFromCanvas(InkCanvas canvas, UIElement element)
        {
            if (canvas == null || element == null) return;
            if (_selectionTool.GetSelectedElement(canvas) == element)
            {
                _selectionTool.ClearSelection(canvas);
            }
            canvas.Children.Remove(element);
        }

        private void AddShapeToCanvas(InkCanvas canvas, UIElement element)
        {
            if (canvas == null || element == null) return;
            if (!canvas.Children.Contains(element))
            {
                canvas.Children.Add(element);
            }
        }

        private interface IUndoRedoAction
        {
            void Undo(MainWindow window);
            void Redo(MainWindow window);
        }

        private class StrokeAddedAction : IUndoRedoAction
        {
            private readonly int _pageIndex;
            private readonly Stroke _stroke;

            public StrokeAddedAction(int pageIndex, Stroke stroke)
            {
                _pageIndex = pageIndex;
                _stroke = stroke;
            }

            public void Undo(MainWindow window)
            {
                var canvas = window.GetInkCanvas(_pageIndex);
                canvas?.Strokes.Remove(_stroke);
            }

            public void Redo(MainWindow window)
            {
                var canvas = window.GetInkCanvas(_pageIndex);
                canvas?.Strokes.Add(_stroke);
            }
        }

        private class StrokeRemovedAction : IUndoRedoAction
        {
            private readonly int _pageIndex;
            private readonly List<Stroke> _strokes;

            public StrokeRemovedAction(int pageIndex, List<Stroke> strokes)
            {
                _pageIndex = pageIndex;
                _strokes = strokes;
            }

            public void Undo(MainWindow window)
            {
                var canvas = window.GetInkCanvas(_pageIndex);
                if (canvas == null) return;
                foreach (var stroke in _strokes)
                {
                    canvas.Strokes.Add(stroke);
                }
            }

            public void Redo(MainWindow window)
            {
                var canvas = window.GetInkCanvas(_pageIndex);
                if (canvas == null) return;
                foreach (var stroke in _strokes)
                {
                    canvas.Strokes.Remove(stroke);
                }
            }
        }

        private class ShapeAddedAction : IUndoRedoAction
        {
            private readonly int _pageIndex;
            private readonly UIElement _element;

            public ShapeAddedAction(int pageIndex, UIElement element)
            {
                _pageIndex = pageIndex;
                _element = element;
            }

            public void Undo(MainWindow window)
            {
                var canvas = window.GetInkCanvas(_pageIndex);
                window.RemoveShapeFromCanvas(canvas, _element);
            }

            public void Redo(MainWindow window)
            {
                var canvas = window.GetInkCanvas(_pageIndex);
                window.AddShapeToCanvas(canvas, _element);
            }
        }

        private class ShapeRemovedAction : IUndoRedoAction
        {
            private readonly int _pageIndex;
            private readonly UIElement _element;

            public ShapeRemovedAction(int pageIndex, UIElement element)
            {
                _pageIndex = pageIndex;
                _element = element;
            }

            public void Undo(MainWindow window)
            {
                var canvas = window.GetInkCanvas(_pageIndex);
                window.AddShapeToCanvas(canvas, _element);
            }

            public void Redo(MainWindow window)
            {
                var canvas = window.GetInkCanvas(_pageIndex);
                window.RemoveShapeFromCanvas(canvas, _element);
            }
        }

        private class CompositeAction : IUndoRedoAction
        {
            private readonly List<IUndoRedoAction> _actions;

            public CompositeAction(IEnumerable<IUndoRedoAction> actions)
            {
                _actions = actions != null ? new List<IUndoRedoAction>(actions) : new List<IUndoRedoAction>();
            }

            public void Undo(MainWindow window)
            {
                if (window == null) return;
                for (int i = _actions.Count - 1; i >= 0; i--)
                {
                    _actions[i].Undo(window);
                }
            }

            public void Redo(MainWindow window)
            {
                if (window == null) return;
                foreach (var action in _actions)
                {
                    action.Redo(window);
                }
            }
        }

    }
}
