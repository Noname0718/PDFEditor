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
- 텍스트 도구: InkCanvas 위를 클릭해 투명 배경/연한 테두리의 TextBox를 생성하고, 더블클릭·Enter로 편집 진입, ESC/포커스 이동 시 편집 확정 및 Undo/Redo 스택 연동. 글자 색/크기 콤보로 기본 스타일과 선택된 텍스트의 스타일을 즉시 바꿀 수 있다.
- 작업 저장/불러오기 + 전체 지우기: 현재 PDF 경로와 페이지별 필기/도형/텍스트를 `.pdfanno` 파일로 내보내고 다시 불러와 후속 편집이 가능하며, PDF와 같은 경로에 동일 이름의 `.pdfanno`가 있으면 PDF를 열 때 자동으로 적용. 툴바의 `전체 지우기` 버튼으로 모든 페이지의 주석을 한 번에 초기화할 수 있다.
- 주석 포함 PDF 내보내기: 현재 화면에 보이는 주석이 합쳐진 이미지를 각 페이지로 만들어 새 PDF로 내보낼 수 있다.

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
5. **TextToolManager**
   - 모든 InkCanvas에 Preview 이벤트를 붙이고, Text 도구가 선택되면 클릭 지점에 TextBox를 생성하거나 기존 TextBox를 선택합니다.
   - TextBox에는 `TextToolElement` 태그와 투명 배경/연한 테두리를 부여하고, 편집 세션 시작/종료 시 Undo 액션(`TextEditedAction`)을 만들어 히스토리에 넣습니다.
   - 선택된 TextBox는 `SelectionToolManager`와 동일한 그룹 바운딩/드래그 로직을 그대로 사용하므로 복사/붙여넣기/삭제/크기 조정이 기존 도형과 동일하게 동작합니다.

## 발생했던 문제 및 해결
| 이슈 | 원인 | 해결 방법 |
| --- | --- | --- |
| 지우개로 도형이 지워지지 않음 | InkCanvas 지우개는 `Stroke`에만 동작하고, 도형은 `Shape` 컨트롤로 추가되어 있어서 히트 테스트가 되지 않음 | `ShapeToolManager`가 Preview 이벤트에서 마우스 이동을 추적하고, InkCanvas.Children을 역순으로 검사하여 `RenderedGeometry` 기반으로 도형을 찾아 삭제하도록 로직을 추가 |
| 잉크를 그린 직후 Ctrl+Z가 반응하지 않음 | InkCanvas 기본 Undo가 먼저 스트로크를 지워 버려 커스텀 Undo 스택에서 대상을 찾지 못함 | InkCanvas 기본 Undo 호출을 제거하고, `StrokeCollected`/`StrokesChanged`에서 Stroke를 복제해 직접 히스토리를 관리. Undo 시에는 StylusPoints를 비교해 동일 Stroke를 찾아 제거하도록 보완 |

## 실행 및 사용 방법
1. Visual Studio 또는 `msbuild`로 `PDFEditor.sln`을 빌드합니다.
2. 앱 실행 후 `PDF 열기` 버튼으로 PDF 파일을 선택합니다.
3. 상단 도구 모음에서 필기/도형 도구, 색상, 두께를 선택한 뒤 PDF 페이지 위를 드래그하여 그림을 추가합니다.
4. 지우개를 선택하면 잉크와 도형을 모두 같은 방식으로 드래그하여 삭제할 수 있습니다.
5. `텍스트` 도구를 켜고 캔버스를 클릭하면 해당 위치에 텍스트 박스가 생성되며, 기본 글자 색/크기는 우측 콤보에서 설정합니다. 더블클릭이나 Enter로 편집 모드에 들어가고, ESC 또는 다른 곳을 클릭하면 편집이 확정됩니다.
6. 하단 페이지 이동/줌 컨트롤로 원하는 페이지와 배율을 맞추며 편집합니다.
7. `작업 저장` 버튼으로 `.pdfanno` 파일을 만들고, `작업 불러오기` 버튼으로 이전에 저장한 주석을 다시 열어 이어서 편집할 수 있습니다. 동일 경로·동일 이름의 `.pdfanno`가 있으면 PDF를 열 때 자동으로 불러옵니다.
8. `전체 지우기` 버튼으로 모든 페이지의 필기·도형·텍스트를 한 번에 삭제할 수 있습니다.
9. `주석 포함 PDF 내보내기` 버튼으로 현재 주석이 합쳐진 새 PDF를 만들 수 있습니다.
