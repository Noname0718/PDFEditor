PDFEditor 과제 패키지 안내
===========================

1. 폴더 구조
   /bin  : 실행 바이너리 및 필요한 DLL/리소스
   /doc  : 기말과제최종보고서.hwp, 기말과제자체평가서.hwp, 발표자료(PPT/PDF),
           시연 동영상(MP4, 5분 이내)
   /src  : 전체 소스 코드 (PDFEditor 솔루션 및 프로젝트)
   readme.txt : 본 안내 파일

2. 실행 방법
   - /bin 폴더에서 PDFEditor.exe를 실행합니다.
   - PDF 열기 버튼으로 문서를 로드한 뒤 상단 도구를 이용해 펜/형광펜/도형/텍스트 주석을 추가합니다.
   - 주석 저장 버튼은 .pdfanno 파일을 생성하며, 주석 포함 PDF 내보내기
     버튼은 새로운 PDF를 생성합니다.

3. 환경 요약
   - 개발 언어/환경: C#, .NET Framework 4.8, WPF
   - 주요 라이브러리: PdfiumViewer 2.13.0, System.Windows.Ink
   - AI 코딩 유형(유형2)을 선택한 경우 부록 C에 프롬프트/응답 요약 포함

4. 주요 기능
   - PDF 뷰어: PdfiumViewer로 페이지 렌더링, 다음/이전/페이지 이동, 줌(슬라이더·텍스트·폭 맞추기).
   - 필기 도구: 펜/형광펜/지우개, PenCursorManager로 굵기 미리보기, 두께/색상 일괄 적용.
   - 도형 도구: 사각형/원/선/삼각형 생성, SelectionToolManager로 이동·리사이즈·복사/붙여넣기.
   - 텍스트 도구: 클릭으로 TextBox 생성, 더블클릭/Enter로 편집, 미니툴바로 글꼴/색상 변경.
   - 부분 지우개: AreaEraserManager로 도형/텍스트를 반경 기반으로 삭제.
   - Undo/Redo: MainWindow.UndoRedo.cs의 UndoRedoManager가 모든 작업 히스토리 관리.
   - 단축키: Ctrl+O(열기), Ctrl+S(.pdfanno 저장), Ctrl+Shift+S(주석 포함 PDF 내보내기), Ctrl+Z/Ctrl+Y(Undo/Redo), Ctrl+C/Ctrl+V(선택 복사/붙여넣기), Ctrl+=·Ctrl+- 또는 Ctrl+휠(줌), Delete(선택 삭제).
   - 주석 저장/불러오기: AnnotationPersistenceService가 .pdfanno 파일로 InkCanvas 상태 직렬화/복원.
   - PDF 내보내기: CaptureAnnotatedPages + WriteFlattenedPdf로 주석을 이미지에 합성해 새 PDF 생성.

5. 개발 스택
   - IDE: Visual Studio / msbuild
   - Framework: .NET Framework 4.8 (WPF)
   - NuGet 패키지: PdfiumViewer 2.13.0, PdfiumViewer.Native.* 패키지, Microsoft.Xaml.Behaviors.Wpf
   - 기타: System.Drawing.Common (PDF→이미지 변환), System.Windows.Ink (InkCanvas)
   - 빌드: `msbuild PDFEditor.sln /t:Build /p:Configuration=Release` 또는 Visual Studio에서 `PDFEditor` 프로젝트 빌드 후 `/bin` 폴더에 결과 복사

6. 제한 사항
   - 주석을 `.pdfanno` 외부 파일과 “주석 포함 PDF 내보내기”로만 저장하며, 원본 PDF 자체에 주석 객체를 삽입하지는 않습니다.
   - MVVM 미도입으로 `MainWindow.xaml.cs`가 많은 책임을 부담하고, 테스트 프로젝트/CI가 없어 자동 검증이 어렵습니다.
   - 이미지/스탬프 삽입, 고급 PDF 주석 호환 기능 등은 구현되어 있지 않습니다.
