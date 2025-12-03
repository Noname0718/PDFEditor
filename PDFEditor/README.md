# PDFEditor

## 개요
- PDF 문서를 이미지로 렌더링한 뒤 InkCanvas 위에서 필기, 형광펜, 도형 삽입 등을 수행하는 WPF 응용프로그램입니다.
- 드래그로 사각형/원/선/삼각형을 그리고, 동일한 지우개 도구로 잉크와 도형을 함께 삭제할 수 있도록 구성했습니다.

## 개발 스택
- **Framework**: .NET Framework 4.8, WPF
- **PDF 렌더링**: [PdfiumViewer 2.13.0](https://github.com/pvginkel/PdfiumViewer)
- **UI 보조**: Microsoft.Xaml.Behaviors.Wpf
- **그래픽**: System.Windows.Ink (InkCanvas), System.Drawing.Common (PDF 렌더링 변환)

## 주요 기능
- PDF 열기, 페이지 이동(버튼/직접 입력), 줌 슬라이더 및 폭 맞춤 기능.
- 펜/형광펜/지우개 도구: 잉크 굵기·색상 동기화 및 RectangleStylusShape 지우개.
- 도형 도구: 드래그 시작점~끝점 기준으로 사각형/원/선/삼각형을 생성하고, 태그를 통해 InkCanvas.Children 에 저장.
- 도형 지우개: InkCanvas 지우개 모드 활성화 시 ShapeToolManager가 Preview 이벤트에서 히트 테스트를 수행해 동일 도구로 삭제.

## 꼭 알아야 하는 부분
1. **페이지 렌더링 파이프라인** (`MainWindow.RenderAllPages`)
   - PdfiumViewer가 반환한 `System.Drawing.Image`를 `BitmapImage`로 변환 후 `Image` + `InkCanvas` + `Border` 계층으로 StackPanel에 추가합니다.
   - 페이지마다 InkCanvas를 `Dictionary<int, InkCanvas>`에 저장하여 페이지 간 이동 시에도 도구 상태를 복원합니다.
2. **도구 전환 로직** (`ToolButton_Click`)
   - 펜/형광펜/지우개 선택 시 InkToolManager가 InkCanvas의 `EditingMode`를 제어하고, 도형 도구를 사용할 때는 InkCanvas의 필기 입력을 막습니다.
   - 지우개를 선택하면 ShapeToolManager의 `_eraseShapeMode`가 활성화되어 도형과 잉크를 동일한 도구로 처리합니다.
3. **ShapeToolManager**
   - InkCanvas별 드래그 상태를 저장하고 `PreviewMouse*` 이벤트에서 좌표를 계산하여 도형을 즉시 배치합니다.
   - 히트 테스트 결과에서 Shape 요소를 찾아 태그(`ShapeToolElement`) 기준으로 제거하므로 다른 UI 요소에는 영향을 주지 않습니다.
4. **줌 처리**
   - StackPanel 전체를 `ScaleTransform`으로 확대/축소하며, `FitWidthToViewer`가 ScrollViewer 폭을 기준으로 `_baseScale`을 계산합니다.

## 실행 및 사용 방법
1. Visual Studio 또는 `msbuild`로 `PDFEditor.sln`을 빌드합니다.
2. 앱 실행 후 `PDF 열기` 버튼으로 PDF 파일을 선택합니다.
3. 상단 도구 모음에서 필기/도형 도구, 색상, 두께를 선택한 뒤 PDF 페이지 위를 드래그하여 그림을 추가합니다.
4. 지우개를 선택하면 잉크와 도형을 모두 같은 방식으로 드래그하여 삭제할 수 있습니다.
5. 하단 페이지 이동/줌 컨트롤로 원하는 페이지와 배율을 맞추며 편집합니다.
