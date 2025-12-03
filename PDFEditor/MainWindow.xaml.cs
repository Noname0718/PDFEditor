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
using System.Windows.Media;
using System.Windows.Media.Imaging;

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

        public MainWindow()
        {
            InitializeComponent();
            this.SizeChanged += MainWindow_SizeChanged;
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

                    _inkTool.SetTool(_inkTool.CurrentTool, ink); // 현재 선택된 필기 도구 적용
                    ApplyCurrentColorAndThicknessToInkCanvas(ink); // 색/두께 동기화

                    _shapeTool.AttachCanvas(ink); // 도형 드로잉/지우기 이벤트 연결

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
                }
            }
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
        }

        /// <summary>
        /// 다음 페이지 버튼 클릭 시 한 페이지 앞으로 이동한다.
        /// 실제 스크롤 이동 대신 페이지 인덱스 및 InkCanvas 상태만 갱신한다.
        /// </summary>
        private void NextPage_Click(object sender, RoutedEventArgs e)
        {
            if (_pdf == null) return;
            if (_currentPage >= _pdf.PageCount - 1) return;

            _currentPage++;
            UpdatePageInfo();

            // 현재 도구 상태를 새 페이지 InkCanvas에 적용
            _inkTool.SetTool(_inkTool.CurrentTool, GetCurrentInkCanvas());
        }

        /// <summary>
        /// 이전 페이지 버튼 클릭 시 한 페이지 뒤로 이동한다.
        /// </summary>
        private void PrevPage_Click(object sender, RoutedEventArgs e)
        {
            if (_pdf == null) return;
            if (_currentPage <= 0) return;

            _currentPage--;
            UpdatePageInfo();

            _inkTool.SetTool(_inkTool.CurrentTool, GetCurrentInkCanvas());
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
                if (targetIndex < 0 || targetIndex >= _pdf.PageCount)
                    return;
                _currentPage = targetIndex;
                UpdatePageInfo();

                _inkTool.SetTool(_inkTool.CurrentTool, GetCurrentInkCanvas());
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
            if (PenButton != null) PenButton.IsChecked = false;
            if (HighlighterButton != null) HighlighterButton.IsChecked = false;
            if (EraserButton != null) EraserButton.IsChecked = false;
            if (RectButton != null) RectButton.IsChecked = false;
            if (EllipseButton != null) EllipseButton.IsChecked = false;
            if (LineButton != null) LineButton.IsChecked = false;
            if (TriangleButton != null) TriangleButton.IsChecked = false;

            clicked.IsChecked = true;

            string tag = clicked.Tag as string ?? "";

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
        private void SuppressBringIntoView(object sender, RequestBringIntoViewEventArgs e)
        {
            e.Handled = true;
        }

    }
}
