# 창Export Beta 0.1.0

창Export는 Revit의 BIM 객체 정보를 회사 CAD Layer Standard로 번역하고, Sheet를 한국 설계 실무의 DWG 전달 구조로 출력하기 위한 Revit 2026 애드인입니다.

## 현재 Beta 기능

- 독립 `창Export` 리본 탭과 7개 명령
- 고정 GUID 공유 매개변수 자동 준비
  - `CAD_LAYER`: 모델 객체 인스턴스 Text
  - `CAD_EXPORT_GROUP`: Sheet 인스턴스 Text
  - `CAD_EXPORT_ORDER`: Sheet 인스턴스 Integer
- 선택 객체의 CAD Layer 지정/제거와 처리 결과 요약
- 회사 CAD Layer JSON Profile 편집/검증
- Category, Family Name, Type Name, 일반 Parameter 기반 Rule Engine
- `Equals`, `Contains`, `StartsWith`, `IsEmpty` Operator
- `CAD_LAYER → Rule → Revit 기본 Mapping` 판정
- Sheet 그룹/순서 일괄 편집
- Revit 기존 DWG Export Setup 선택
- 그룹별 폴더에 Sheet별 Native DWG 안전 출력
- 기존 파일 자동 덮어쓰기 방지와 실행 Manifest JSON
- 프로젝트 준비 상태와 기술 게이트 진단

## 설치

1. Revit 2026을 완전히 종료합니다.
2. `배포폴더\최신\설치.cmd`를 실행합니다.
3. Revit 2026을 다시 실행합니다.
4. 상단의 `창Export` 탭을 확인합니다.

설치 위치는 `%APPDATA%\Autodesk\Revit\Addins\2026\ChangExport`입니다. 사용자 Profile은 `%APPDATA%\ChangExport\Profiles\Company_Default.json`에 저장되며 재설치와 제거 시 보존됩니다.

## 권장 사용 순서

1. Revit 객체를 선택하고 `CAD Layer 지정`을 실행합니다.
2. `Layer/Rule 관리`에서 회사 Profile과 자동 분류 Rule을 확인합니다.
3. `Sheet 그룹 관리`에서 그룹과 출력 순서를 저장합니다.
4. `회사 DWG 출력`에서 Setup, Sheet, 출력 폴더를 선택합니다.
5. 결과 폴더의 DWG와 `ChangExport_Manifest_*.json`을 확인합니다.

## Beta 범위와 기술 게이트

현재 버전은 실사용 가능한 매개변수/설정/Native Export 흐름을 먼저 검증하는 프로토타입입니다. 다음 기능은 RealDWG, AutoCAD .NET 또는 재배포가 승인된 DWG SDK가 확정된 뒤 `IDwgProcessor` 구현으로 연결합니다.

- 같은 Category의 인스턴스를 객체별 Temporary Layer로 안정적으로 분리
- Temporary Layer의 회사 Layer Rename/Merge 및 속성 적용
- 여러 Sheet의 단일 DWG Model Space 평면화와 자동 배치
- DWG 내부 Entity/Layer 재열기 검증

배치 방식과 Margin은 UI 및 Manifest에 기록되지만 Beta 0.1.0의 Native Sheet별 출력 파일에는 아직 적용되지 않습니다.

## 개발 빌드

Revit 2026이 기본 경로에 설치된 환경에서 다음 명령을 사용합니다.

```powershell
dotnet build .\ChangExport.csproj --configuration Release
```

`Build-Latest.cmd`는 Release 빌드 후 `배포폴더\최신`을 갱신합니다.
