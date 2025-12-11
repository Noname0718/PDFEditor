# PDFEditor 아키텍처 개요

이 문서는 WPF 기반 PDFEditor의 주요 구성 요소와 역할을 요약해 협업자가 빠르게 구조를 파악할 수 있도록 돕는다.

## 계층 구조
- **UI (XAML + MainWindow.xaml.cs)**  
  - 사용자 입력을 받아 각 도구 매니저와 서비스에 위임하는 오케스트레이션 계층.  
  - 페이지 렌더링, 줌/스크롤, 툴바 상태 갱신 등 뷰 관련 로직만 유지한다.
- **도구 매니저 (Ink/Text/Shape/Selection 등)**  
  - InkCanvas에 붙는 입력 이벤트를 소유하며 특정 도구의 상태와 동작을 완전히 캡슐화한다.  
  - MainWindow는 매니저 API(예: `SetActive`, `AttachCanvas`)만 호출해 충돌 없이 상태를 공유한다.
- **서비스 (Services/*)**  
  - 파일 입출력이나 PDF 렌더링처럼 UI와 직접 관련 없는 작업을 담당한다.  
  - 현재는 주석 저장/불러오기를 `AnnotationPersistenceService`에서 관리하며, 향후 PDF 로더/Export 서비스도 동일한 패턴으로 분리할 수 있다.

## 주요 컴포넌트
- **InkToolManager**: 펜/형광펜/지우개 모드를 InkCanvas마다 설정하고 커서, 굵기, 색상을 통합 관리.
- **ShapeToolManager**: 사각형/원/선/삼각형을 생성하고, 동일 지우개 도구로 Shape를 제거할 수 있도록 히트 테스트를 수행.
- **SelectionToolManager + GroupSelectionAdorner**: 요소 다중 선택·이동·리사이즈 및 복사/붙여넣기를 담당.
- **TextToolManager**: TextBox 생성/편집/스타일 변경을 담당하며, InkCanvas와 TextBox 사이의 소유권을 추적한다.
- **PenCursorManager / AreaEraserManager**: 커서 미리보기와 영역 지우개(도형/텍스트 일괄 삭제)를 별도 클래스로 분할해 각 책임을 명확히 했다.
- **UndoRedoManager (MainWindow.UndoRedo.cs)**: 모든 히스토리 스택과 액션 클래스를 코드 비하인드 본문과 분리해, UI 이벤트와 히스토리 로직을 독립적으로 유지한다.
- **AnnotationPersistenceService**: InkCanvas 상태를 `.pdfanno` 파일로 직렬화하거나 다시 로드하며, 직렬화 대상 요소 필터링 로직을 재사용 가능한 API로 제공한다.

## 데이터 흐름 요약
1. **PDF 로드**  
   - `LoadPdfFromPath`가 PdfiumViewer로 이미지를 얻어 페이지별 Grid(Image + InkCanvas) 구조를 생성.  
   - 각 InkCanvas는 도구 매니저에 등록되고 `_pageInkCanvases` 딕셔너리에 저장된다.
2. **편집 도중 상태 공유**  
   - 도구 버튼 → `ToolButton_Click` → 각 매니저에 전달되어 동일 InkCanvas 참조를 사용.  
   - Undo/Redo 스택(`IUndoRedoAction`)이 Ink/Shape/Text 이벤트로부터 직접 액션을 생성한다.
3. **주석 저장/불러오기**  
   - MainWindow는 현재 PDF 경로와 페이지별 InkCanvas만 서비스에 넘기고, `AnnotationPersistenceService`가 XML 구조 생성/파싱과 XAML 직렬화를 담당한다.  
   - 불러오기 시 서비스가 생성한 UIElement를 다시 캔버스에 붙이고 TextToolManager에 등록해 상태를 복구한다.
4. **PDF 내보내기**  
   - `CaptureAnnotatedPages`가 InkCanvas를 복제(`CloneCanvasForExport`) 후 RenderTargetBitmap으로 Flatten, `WriteFlattenedPdf`가 새 PDF를 생성한다.

## 향후 확장 포인트
- PDF 로딩/내보내기 역시 Service 패턴으로 감싸면 테스트가 쉬워진다.
- ViewModel을 도입하면 도구 상태를 바인딩으로 옮겨 UI 테스트 용이성 향상이 기대된다.
