# Revit 한국형 DWG Export 애드인 개발 명세

## 0. Codex에게 전달할 핵심 지시

이 문서는 Revit용 DWG Export 애드인을 개발하기 위한 요구사항과 기술 방향을 정리한 것이다.

목표는 단순히 `Document.Export()`를 호출하는 기능이 아니라, **Revit의 객체/카테고리 중심 DWG 출력 방식을 한국 설계사무소의 기존 CAD 작성 방식에 맞게 변환하는 Export 파이프라인**을 만드는 것이다.

우선 구현 시 다음 원칙을 따른다.

1. Revit 모델링 방식 자체를 CAD 출력 때문에 억지로 변경하지 않는다.
2. 패밀리 분리, Subcategory 추가, Object Styles 조정, 모든 View에 필터 적용 같은 사전 작업을 최소화한다.
3. CAD 레이어 분류는 가능하면 출력 단계에서 결정한다.
4. 회사별 기존 CAD Layer Standard를 그대로 사용할 수 있게 한다.
5. 객체별 수동 Override와 Type/Parameter 기반 자동 분류를 모두 지원한다.
6. 여러 Revit Sheet를 그룹별로 하나의 DWG에 합치고, 최종 결과는 Model Space에 시트들이 배열된 형태를 지원한다.
7. Revit Native DWG Exporter가 직접 처리하지 못하는 부분은 DWG 후처리 계층에서 해결한다.
8. 첫 버전부터 완전한 독립 DWG Writer를 새로 만드는 것은 피하고, Native Export + Post Processing 방식부터 검증한다.

---

# 1. 현재 Revit DWG Export의 실무 문제

## 1.1 Category/Subcategory 중심의 레이어 출력

Revit은 기본적으로 Category/Subcategory 기반으로 DWG 레이어를 매핑한다.

예를 들어 구조 프레임에서:

- B1 = Beam
- G1 = Girder

라고 하더라도 둘 다 기본 Category가 `Structural Framing`이면 일반적인 Export 설정에서는 같은 계열 레이어로 출력되기 쉽다.

한국 구조도면에서는 Beam과 Girder를 서로 다른 CAD 레이어, 색상 등으로 관리하는 경우가 많다.

이 문제를 피하기 위해 실무에서는:

- 패밀리를 여러 개로 나눔
- Subcategory를 별도로 만듦
- Revit Object Style을 수정
- View Filter를 만들어 색상을 Override
- View Template에 필터를 적용

등의 방식이 사용되지만, 이것은 DWG 출력 때문에 BIM 모델링 규칙이 영향을 받는 구조라는 문제가 있다.

---

## 1.2 벽/마감 분류 문제

Revit 벽은 하나의 Walls Category 안에서도 실제 CAD에서는 다음처럼 세분화되는 경우가 많다.

- 콘크리트벽
- 조적벽
- 블록벽
- 석고벽
- 각종 마감벽
- 유리벽
- 기타 회사 표준 분류

하지만 Revit Native Export의 Layer Mapping만으로는 `Type Name`, 임의의 Shared Parameter 등 사용자 정의 규칙을 자유롭게 Layer Name으로 직접 사용하는 데 제약이 있다.

---

## 1.3 View Filter 방식의 불편

현재 실무에서는 Type Name 등을 필터링하여 Revit View에서 색상을 강제로 바꾸고, DWG 출력 결과의 색상 차이를 만드는 방법도 사용한다.

문제점:

- 모든 View에 필터가 필요함
- View Template을 관리해야 함
- 새 View가 생길 때 적용 여부를 확인해야 함
- CAD 출력을 위해 Revit 그래픽 설정을 관리해야 함
- 색상은 구분해도 최종 DWG Layer 자체가 원하는 회사 레이어로 분리되지 않을 수 있음

목표 애드인은 이 반복 작업을 출력 단계에서 자동으로 처리해야 한다.

---

# 2. 최종 목표

최종 개념은 다음과 같다.

```text
Revit BIM Model
    ↓
DWG Export Rule Engine
    ↓
회사 CAD Standard 변환
    ↓
Revit Native DWG Export
    ↓
DWG Post Processor
    ↓
회사에서 바로 사용하는 최종 DWG
```

Revit 모델은 BIM 논리에 맞게 유지하고, CAD 논리는 Export 단계에서 번역한다.

---

# 3. 기능 A — Rule 기반 레이어 자동 분류

## 3.1 예시

다음 같은 Rule을 사용자가 설정할 수 있어야 한다.

| Category | Parameter | Operator | Value | Target Layer |
|---|---|---|---|---|
| Structural Framing | Structural Usage | Equals | Beam | S-BEAM |
| Structural Framing | Structural Usage | Equals | Girder | S-GIRDER |
| Walls | Type Name | Contains | 조적 | A-WALL-BRICK |
| Walls | Type Name | Contains | 블록 | A-WALL-BLOCK |
| Walls | Type Name | Contains | 석고 | A-WALL-GYP |
| Floors | Type Name | Contains | 마감 | A-FLOR-FIN |

향후 Operator 후보:

- Equals
- NotEquals
- Contains
- NotContains
- StartsWith
- EndsWith
- Regex
- IsEmpty
- IsNotEmpty
- GreaterThan
- LessThan
- Range

---

# 4. 기능 B — 객체별 CAD_LAYER 인스턴스 매개변수

이 기능은 매우 중요하다.

회사에는 이미 자체 CAD Layer 체계가 있으므로, Revit 객체마다 최종 DWG Layer를 직접 지정할 수 있게 한다.

## 4.1 Shared Parameter

예:

```text
CAD_LAYER
```

가능한 모델 카테고리에 **Instance Parameter**로 바인딩한다.

Type Parameter가 아니라 Instance Parameter여야 한다.

이유:

같은 Type을 사용하는 객체 100개 중 특정 객체만 별도 Layer로 보내는 것이 가능해야 한다.

예:

```text
Wall A
CAD_LAYER = A-WALL-01

Wall B
CAD_LAYER = A-WALL-02

Roof A
CAD_LAYER = A-WALL-01

Beam A
CAD_LAYER = S-BEAM
```

최종 DWG에서는 Category와 상관없이:

```text
A-WALL-01
 ├ Wall A
 └ Roof A

A-WALL-02
 └ Wall B

S-BEAM
 └ Beam A
```

와 같은 결과를 목표로 한다.

---

## 4.2 선택 객체에 레이어 직접 지정

Ribbon 명령 예:

```text
[CAD Layer 지정]
[CAD Layer 제거]
[Layer 관리]
[DWG 출력]
```

사용자가 Revit에서 여러 객체를 선택한 뒤:

```text
CAD Layer 지정
```

을 누르면 회사 Layer 목록이 나타난다.

예:

```text
A-WALL-CONC
A-WALL-BRICK
A-WALL-BLOCK
A-WALL-GLASS
S-COL
S-BEAM
S-GIRDER
...
```

사용자가 하나를 선택하면 선택된 모든 객체의 `CAD_LAYER` 값에 저장한다.

개념 코드:

```csharp
foreach (Element element in selectedElements)
{
    Parameter p = element.LookupParameter("CAD_LAYER");

    if (p != null && !p.IsReadOnly)
        p.Set(selectedLayerName);
}
```

실제 구현에서는 문자열 이름 검색보다 Shared Parameter GUID 또는 Definition 기반 접근을 우선 검토한다.

---

# 5. 레이어 결정 우선순위

다음 Priority를 기본으로 한다.

```text
1순위
CAD_LAYER 인스턴스 값 존재
    ↓
해당 Layer로 강제 출력

2순위
CAD_LAYER 값 없음
    ↓
Rule Engine 평가

3순위
일치 Rule 없음
    ↓
Revit 기본 Category/Subcategory Layer Mapping 사용
```

예:

```text
벽 1
Type = 조적벽
CAD_LAYER = ""
→ Rule에 따라 A-WALL-BRICK

벽 2
Type = 조적벽
CAD_LAYER = A-WALL-SPECIAL
→ 사용자 지정값 우선
→ A-WALL-SPECIAL
```

이렇게 자동 분류와 수동 Override가 동시에 존재하도록 한다.

---

# 6. 회사 CAD Layer Standard

객체마다 `CAD_COLOR`, `CAD_LINEWEIGHT`, `CAD_LINETYPE`를 모두 저장하는 방식은 피한다.

객체에는 가능하면:

```text
CAD_LAYER
```

하나만 저장한다.

나머지는 회사 Layer Standard 파일에서 관리한다.

예:

```json
{
  "A-WALL-BRICK": {
    "color": 1,
    "linetype": "Continuous",
    "lineweight": 0.18
  },
  "S-BEAM": {
    "color": 3,
    "linetype": "Continuous",
    "lineweight": 0.30
  }
}
```

장점:

- 회사 표준 변경 시 Revit 모델을 수정할 필요 없음
- JSON/CSV만 수정하면 됨
- 프로젝트 간 재사용 가능
- 회사별 Profile을 따로 만들 수 있음

예:

```text
Company_A.json
Company_B.json
Structure_Standard.json
Architecture_Standard.json
```

---

# 7. Revit Native DWG Export API 활용 방향

Revit API에는 Export Layer Table을 가져오고 수정한 뒤 Export Options에 다시 설정하는 구조가 있다.

공식 문서 기준:

- `BaseExportOptions`
- `DWGExportOptions`
- `ExportLayerTable`
- Get/Set Export Layer Table

등을 사용할 수 있다.

Autodesk 문서:
https://help.autodesk.com/cloudhelp/2018/ENU/Revit-API/Revit_API_Developers_Guide/Advanced_Topics/Export/Export_Tables.html

중요:

Native Export Layer Table은 Category/Subcategory 중심이다.

따라서 임의의 인스턴스 Shared Parameter 문자열인 `CAD_LAYER`를 Native Exporter가 그대로 최종 Layer Name으로 사용하는 기능이 존재한다고 가정하면 안 된다.

이 부분은 별도의 매핑 또는 후처리가 필요하다.

---

# 8. Revit 기본 Layer Modifier 활용 가능성

Native Layer Modifier가 제공하는 값 중 활용 가능한 것은 적극 사용한다.

예:

- Structural Usage
- Structural Material Type
- Function
- Fire Rating
- Level
- Phase
- System Name
- System Type
- Workset

등.

따라서 구조 프레임의 Beam / Girder처럼 Revit 내부 속성 자체로 구분 가능한 것은 Native 기능을 먼저 사용할 수 있다.

반면 다음과 같은 범용 분류는 Native Modifier만으로 부족할 수 있다.

```text
Type Name Contains "조적"
Shared Parameter "CAD_LAYER" = "A-WALL-BRICK"
사용자 정의 Parameter 값
Regex 기반 분류
```

이런 경우 Rule Engine + 후처리 구조를 사용한다.

---

# 9. Temporary Export View / Graphic Override 방식

Native Exporter가 그래픽 Override를 별도 Layer로 분리할 수 있는 옵션을 이용하는 방식을 1차 Prototype으로 검토한다.

개념:

```text
원본 View
    ↓
Temporary Export View 생성
    ↓
Rule Engine으로 객체 그룹 분류
    ↓
임시 Filter / Graphic Override 적용
    ↓
DWG Native Export
    ↓
임시 View / Filter 삭제
```

이 방식은 최종 Layer 이름을 완전히 자유롭게 지정하는 목적보다,

**Revit Native Export 단계에서 객체 그룹을 식별 가능한 별도 Layer로 분리하기 위한 중간 단계**

로 사용하는 것이 좋다.

예:

```text
TEMP_WALL_0001
TEMP_WALL_0002
TEMP_ROOF_0001
TEMP_FRAMING_0001
```

후처리에서:

```text
TEMP_WALL_0001 → A-WALL-BRICK
TEMP_ROOF_0001 → A-WALL-BRICK
```

처럼 서로 다른 Revit Category의 객체를 최종적으로 하나의 CAD Layer로 합칠 수 있다.

---

# 10. 기능 C — Sheet 그룹별 단일 DWG 출력

한국 설계사무소에서는 Revit 기본 출력처럼 시트마다 DWG 하나를 사용하는 것보다,

```text
한 DWG 파일
└ Model Space
   ├ A-101
   ├ A-102
   ├ A-103
   ├ A-104
   └ ...
```

처럼 여러 도면을 한 파일 Model Space 안에 일렬 또는 격자로 배치하는 방식이 흔하다.

이 기능을 애드인에 포함한다.

---

# 11. Sheet 그룹 매개변수

Sheet에 프로젝트/공유 매개변수를 추가한다.

예:

```text
CAD_EXPORT_GROUP
CAD_EXPORT_ORDER
```

사용 예:

| Sheet | CAD_EXPORT_GROUP | CAD_EXPORT_ORDER |
|---|---|---:|
| A-101 | 평면도 | 1 |
| A-102 | 평면도 | 2 |
| A-103 | 평면도 | 3 |
| A-201 | 입면단면도 | 1 |
| A-202 | 입면단면도 | 2 |

최종 결과:

```text
평면도.dwg
    A-101 A-102 A-103

입면단면도.dwg
    A-201 A-202
```

---

# 12. Model Space 배치

최종 DWG는 Layout/Paper Space가 아니라 Model Space에 여러 시트를 펼쳐 놓는 방식을 지원한다.

배치 옵션 예:

```text
배치 방식
- 가로 일렬
- 세로 일렬
- N장마다 다음 줄
- 도곽 크기 기준 자동 격자

정렬
- Sheet Number
- CAD_EXPORT_ORDER

간격
- 사용자 지정
- 도곽 폭 + Margin
```

예:

```text
A001 A002 A003 A004 A005
A006 A007 A008 A009 A010
A011 A012 A013 A014 A015
```

---

# 13. Sheet → Model Space 변환에서 중요한 두 방식

구현 전에 반드시 구분해야 한다.

## 방식 1 — 시트의 시각적 결과를 그대로 Model Space에 평면화

Revit Sheet가:

```text
┌──────────────────────────┐
│ 평면도 1:100             │
│                          │
│ 단면도 1:50              │
│                  상세 1:20│
└──────────────────────────┘
```

라면 보이는 결과를 그대로 Model Space 도형으로 변환한다.

첫 버전에서는 이 방식이 우선이다.

장점:

- 시트와 최종 DWG의 외형 일치가 쉬움
- 한 시트에 서로 다른 축척 View가 있어도 결과를 그대로 보존 가능
- 한국식 시트 나열 DWG를 만들기 쉬움

AutoCAD의 `EXPORTLAYOUT` 개념과 유사한 결과를 목표로 한다.

---

## 방식 2 — 각 View Geometry를 실제 1:1 크기로 Model Space에 유지

예:

실제 평면도가:

```text
30,000 mm × 50,000 mm
```

이면 DWG에서도 실제 1:1 크기를 유지하고,

1:100 A1 도곽은:

```text
841 × 594
```

를 100배 확대하여:

```text
84,100 × 59,400
```

형태로 배치하는 방식이다.

이 방식은 한국 CAD에서 자주 사용하는 형태와 유사할 수 있지만,

한 Sheet에:

- 1:100 평면
- 1:50 단면
- 1:20 상세

가 동시에 들어있을 경우 변환 규칙이 복잡해진다.

따라서 MVP에서는 방식 1을 먼저 구현하고, 방식 2는 별도 모드로 확장한다.

---

# 14. DWG 후처리 엔진

전체 프로젝트의 핵심 계층이다.

후처리 기능:

1. 여러 Revit Export DWG 읽기
2. Layout 또는 Sheet 표현을 Model Space용 Geometry로 변환
3. 지정 Group의 여러 Sheet를 하나의 DWG에 병합
4. Sheet별 위치 이동
5. Layer Rename
6. Layer Merge
7. Color 정리
8. Linetype 정리
9. Lineweight 정리
10. 필요 없는 Revit 생성 Layer 제거
11. Xref 처리
12. Purge 가능한 불필요 항목 정리
13. 최종 DWG 저장

---

# 15. DWG 처리 기술 후보

## 15.1 RealDWG

Autodesk 공식 RealDWG는 C++/.NET 개발자가 DWG/DXF 파일을 읽고 쓸 수 있는 SDK이다.

공식 설명:
https://aps.autodesk.com/developer/overview/realdwg-api

공식 설명상:

- DWG Read
- DWG Write
- DXF Read
- DXF Write
- .NET / C++

을 지원한다.

다만 라이선스/배포 조건이 있으므로 실제 제품 적용 전에 반드시 별도 확인해야 한다.

RealDWG를 사용할 경우 목표 구조:

```text
Revit Add-in
    ↓
Native DWG Export
    ↓
RealDWG Database Open
    ↓
Layer 재매핑
    ↓
Model Space 병합
    ↓
최종 DWG 저장
```

---

## 15.2 AutoCAD ObjectARX / .NET

AutoCAD 설치를 전제로 하는 내부 사내용 도구라면 AutoCAD .NET/ObjectARX를 이용한 후처리도 후보가 될 수 있다.

그러나 Revit Add-in 하나만 배포하고 싶다면 AutoCAD 의존성 문제를 고려해야 한다.

---

## 15.3 기타 DWG SDK

RealDWG 라이선스가 문제가 되면 상용 또는 오픈소스 대안을 검토할 수 있다.

단, 실제 DWG 쓰기 품질, 폰트, Hatch, Dimension, Block, Xref, Proxy Object 호환성을 반드시 테스트한다.

---

# 16. CustomExporter / IExportContext2D는 장기 옵션

Revit의 Custom Export API를 이용해 2D View의 Curve, Polyline, Text 등 Export Context를 직접 받아 완전한 사용자 정의 Exporter를 만드는 방향도 장기적으로 가능하다.

하지만 이것은 최종 DWG 파일 작성까지 직접 책임져야 하므로 난이도가 크게 올라간다.

따라서 우선순위:

```text
1차
Native Revit DWG Export + Post Processor

2차
Rule Engine 고도화

3차
CustomExporter / IExportContext2D 기반 독립 Export 검토
```

---

# 17. 권장 소프트웨어 구조

```text
RevitDwgExporter
│
├─ UI
│  ├─ ExportWindow
│  ├─ LayerAssignWindow
│  ├─ LayerManagerWindow
│  └─ RuleEditorWindow
│
├─ Parameters
│  ├─ SharedParameterService
│  ├─ CadLayerParameterService
│  └─ SheetExportParameterService
│
├─ Rules
│  ├─ DwgRule
│  ├─ RuleCondition
│  ├─ RuleEngine
│  └─ ElementClassificationService
│
├─ Standards
│  ├─ CadStandard
│  ├─ CadLayerDefinition
│  ├─ JsonCadStandardRepository
│  └─ CsvCadStandardRepository
│
├─ RevitExport
│  ├─ RevitDwgExportService
│  ├─ ExportLayerTableService
│  ├─ TemporaryExportViewService
│  └─ SheetExportService
│
├─ DwgProcessing
│  ├─ IDwgProcessor
│  ├─ DwgLayerRemapper
│  ├─ DwgLayerMerger
│  ├─ DwgSheetFlattener
│  ├─ DwgSheetArranger
│  └─ DwgCleaner
│
├─ Models
│  ├─ ExportJob
│  ├─ ExportGroup
│  ├─ ExportSheet
│  └─ ElementLayerAssignment
│
└─ Commands
   ├─ AssignCadLayerCommand
   ├─ ClearCadLayerCommand
   ├─ ManageCadLayersCommand
   └─ ExportDwgCommand
```

---

# 18. 핵심 데이터 모델 예시

## DwgRule

```csharp
public class DwgRule
{
    public string Name { get; set; }

    public BuiltInCategory? Category { get; set; }

    public string ParameterName { get; set; }

    public RuleOperator Operator { get; set; }

    public string CompareValue { get; set; }

    public string TargetLayer { get; set; }

    public int Priority { get; set; }

    public bool Enabled { get; set; }
}
```

---

## CadLayerDefinition

```csharp
public class CadLayerDefinition
{
    public string Name { get; set; }

    public int ColorIndex { get; set; }

    public string Linetype { get; set; }

    public double Lineweight { get; set; }
}
```

---

## ExportGroup

```csharp
public class ExportGroup
{
    public string Name { get; set; }

    public List<ElementId> SheetIds { get; set; }

    public SheetArrangeMode ArrangeMode { get; set; }

    public double Gap { get; set; }

    public int Columns { get; set; }
}
```

---

# 19. Export 실행 순서

권장 전체 Pipeline:

```text
사용자 Export 실행
        ↓
회사 CAD Standard 로드
        ↓
Sheet 검색
        ↓
CAD_EXPORT_GROUP 기준 Grouping
        ↓
각 Sheet의 Element/출력 정보 수집
        ↓
CAD_LAYER 인스턴스 Override 확인
        ↓
Rule Engine 평가
        ↓
Temporary Export Mapping 생성
        ↓
Revit Native DWG Export
        ↓
DWG 파일 Open
        ↓
Temporary Layer → 최종 회사 Layer Remap
        ↓
동일 Target Layer 병합
        ↓
Sheet/Layout → Model Space Flatten
        ↓
Group별 Sheet 배치
        ↓
Layer Color/Linetype/Lineweight 정리
        ↓
불필요 Layer/Block 정리
        ↓
Group당 DWG 1개 저장
```

---

# 20. MVP 개발 순서

## Phase 1 — CAD_LAYER Parameter

가장 먼저 구현한다.

- Shared Parameter 생성
- 가능한 대상 Model Category에 Instance Binding
- 선택 객체에 Layer 지정
- Layer 제거
- 값 조회
- Undo/Transaction 정상 처리

성공 기준:

같은 Type의 객체를 각각 다른 `CAD_LAYER` 값으로 지정할 수 있음.

---

## Phase 2 — Rule Engine

- Category
- Type Name
- Family Name
- Built-in Parameter
- Shared Parameter

기준으로 Layer 결정.

우선 Operator:

- Equals
- Contains
- StartsWith
- IsEmpty

부터 구현.

---

## Phase 3 — Native DWG Export Prototype

- `DWGExportOptions`
- `ExportLayerTable`
- Native Layer Modifier
- Temporary View/Filter/Override

를 테스트한다.

목적:

**객체 단위의 Rule 결과를 DWG에서 식별 가능한 Layer 그룹으로 얼마나 안정적으로 분리할 수 있는지 확인.**

---

## Phase 4 — DWG Layer 후처리

Prototype에서 반드시 확인:

```text
Wall A CAD_LAYER = LAYER1
Roof A CAD_LAYER = LAYER1
Beam A CAD_LAYER = LAYER2
```

최종 DWG:

```text
LAYER1
 ├ Wall A Geometry
 └ Roof A Geometry

LAYER2
 └ Beam A Geometry
```

가 실제로 만들어지는지 검증.

---

## Phase 5 — Sheet Group Export

- `CAD_EXPORT_GROUP`
- `CAD_EXPORT_ORDER`

매개변수 구현.

Group별로 시트를 수집하여 임시 DWG 생성.

---

## Phase 6 — Model Space 병합

예:

```text
Group = 평면도

A101
A102
A103
A104
```

결과:

```text
평면도.dwg

Model Space
A101 A102 A103 A104
```

---

## Phase 7 — 자동 배치

- Horizontal
- Vertical
- Grid
- Auto Grid

구현.

---

# 21. 처음부터 하지 말아야 할 것

MVP에서 다음은 피한다.

1. Revit 2D Geometry를 처음부터 전부 직접 DWG Entity로 만드는 완전 독립 Exporter
2. 모든 Revit Category 예외를 첫 버전부터 지원
3. Dimension/Hatch/Text/Link/Import/Annotation의 모든 예외를 동시에 해결
4. 회사 CAD 표준 UI를 지나치게 복잡하게 설계
5. CAD 때문에 Revit Family/Subcategory를 강제로 변경
6. CAD Layer별 Color 등을 Revit 객체 Parameter에 모두 중복 저장

---

# 22. 반드시 테스트해야 할 Revit 객체

MVP 이후 아래 Category를 순서대로 검증한다.

## 건축

- Walls
- Floors
- Roofs
- Doors
- Windows
- Generic Models
- Curtain Walls
- Curtain Panels
- Detail Items

## 구조

- Structural Framing
- Structural Columns
- Structural Foundations
- Floors
- Walls
- Rebar

## Annotation

- Text Notes
- Dimensions
- Detail Lines
- Filled Regions
- Tags
- Symbols

## 기타

- CAD Import
- CAD Link
- Revit Link
- Images
- Raster
- Revision Clouds

각 항목에 대해:

- Layer
- Color
- Linetype
- Lineweight
- Block 변환 여부
- Model/Paper Space 위치
- Clip
- Draw Order

를 확인한다.

---

# 23. 가장 중요한 기술 검증 항목

Codex는 코딩 전에 작은 Test Command를 만들어 다음을 실제 Revit에서 확인해야 한다.

### Test 1

`ExportLayerTable`을 수정해서 Category Layer 이름 변경 가능 여부.

### Test 2

Structural Framing의 `Structural Usage` Modifier로 Beam/Girder Layer가 실제 분리되는지.

### Test 3

서로 다른 Graphic Override를 가진 동일 Category Element가 `NewLayer` 방식에서 실제 별도 Layer로 출력되는지.

### Test 4

동일 View에서 Instance별로 Override했을 때 출력 Layer가 원하는 수준으로 분리되는지.

### Test 5

Wall과 Roof처럼 서로 다른 Category를 임시 Layer로 분리한 후 DWG 후처리에서 동일 Layer로 병합할 수 있는지.

### Test 6

Revit Sheet Export 결과의 Paper Space / Viewport / Xref 구조를 실제 DWG에서 조사할 것.

### Test 7

Sheet 하나를 Model Space의 시각적 Geometry로 평면화하는 최소 Prototype 제작.

### Test 8

평면화한 Sheet 3개를 하나의 DWG Model Space에 X축 방향으로 배열.

---

# 24. 구현 철학

이 프로젝트의 핵심은 Revit을 CAD처럼 만들려는 것이 아니다.

다음 분리를 유지한다.

```text
BIM 정보 체계
≠
CAD Layer 체계
```

Revit에서는:

- Category
- Family
- Type
- Material
- Structural Usage
- Parameters

를 BIM 정보로 사용한다.

Export 단계에서만:

```text
BIM Element
        ↓
Classification
        ↓
CAD Layer
```

로 변환한다.

이 구조를 지키면 회사별 CAD 표준이 달라져도 Revit 모델링 표준을 다시 만들 필요가 없다.

---

# 25. 최종 사용자 경험 목표

사용자가 해야 할 일:

### 자동 분류 대상

그냥 정상적으로 Revit 모델링.

### 예외 객체

객체 선택 → `CAD Layer 지정`.

### Sheet

필요하면 `CAD_EXPORT_GROUP` 지정.

### 출력

`회사 DWG 출력` 버튼 클릭.

그 이후는 애드인이 처리한다.

```text
Revit
  ↓
회사 DWG 출력
  ↓
완료
```

최종 폴더 예:

```text
DWG/
├─ 01_평면도.dwg
├─ 02_입면단면도.dwg
├─ 03_상세도.dwg
└─ 04_구조도.dwg
```

각 파일은:

- 지정된 Sheet들이 하나의 DWG에 포함
- Model Space에 배열
- 회사 CAD Layer 적용
- 회사 Color/Linetype/Lineweight 적용
- 불필요한 Revit 기본 Layer 최소화

상태를 목표로 한다.

---

# 26. 현재 기술 판단

## 확정적으로 가능한 부분

- Revit Add-in에서 Shared Parameter 생성/Binding
- Instance별 `CAD_LAYER` 값 저장
- 선택 Element에 Layer 값 지정
- Parameter/Type/Category 기반 Rule Engine 작성
- Revit API를 통한 DWG Export 실행
- Export Layer Table 조회/수정
- 외부 DWG SDK를 이용한 DWG Read/Write
- DWG에서 Layer Rename/Merge
- 여러 DWG Geometry를 하나의 Model Space에 병합
- Sheet Group에 따라 파일을 묶는 로직

## Prototype으로 실제 검증이 필요한 부분

- Graphic Override + `NewLayer`가 객체별 Temporary Layer 생성에 어느 수준까지 안정적으로 사용 가능한지
- Revit Sheet DWG의 Paper Space 구조를 어떤 방식으로 가장 안전하게 Model Space로 평면화할지
- Viewport Clip/Hatch/Annotation/Text/Dimension 변환 정확도
- Xref를 사용할지 완전 Bind 형태로 처리할지
- RealDWG 사용 시 정확한 라이선스/배포 조건
- Revit 버전별 API 차이

---

# 27. 공식 참고자료

Autodesk Revit API — Export Tables  
https://help.autodesk.com/cloudhelp/2018/ENU/Revit-API/Revit_API_Developers_Guide/Advanced_Topics/Export/Export_Tables.html

Autodesk Revit API / SDK  
https://aps.autodesk.com/developer/overview/revit-api

Autodesk RealDWG API  
https://aps.autodesk.com/developer/overview/realdwg-api

Autodesk ObjectARX  
https://aps.autodesk.com/developer/overview/objectarx-autocad-sdk

---

# 28. Codex가 처음 수행할 작업

바로 전체 애드인을 만들지 말고 먼저 **기술 검증용 최소 Add-in**을 만든다.

첫 Command의 목표:

```text
1. 현재 View에서 Wall 2개 선택
2. Wall A → CAD_LAYER = TEST_A
3. Wall B → CAD_LAYER = TEST_B
4. 두 객체를 서로 다른 임시 Export 그룹으로 분류
5. 현재 View를 DWG로 Export
6. 결과 DWG의 Layer 구조를 확인할 수 있도록 로그 출력
```

두 번째 Prototype:

```text
Wall 1 → LAYER1
Roof 1 → LAYER1
Beam 1 → LAYER2
```

를 만들어 최종 DWG에서:

```text
LAYER1
 ├ Wall
 └ Roof

LAYER2
 └ Beam
```

가 되는 가장 단순한 Pipeline을 구현한다.

세 번째 Prototype:

```text
A101
A102
A103
```

세 Sheet를 각각 Export한 뒤:

```text
TEST_GROUP.dwg
Model Space

[A101] [A102] [A103]
```

형태로 병합한다.

이 세 Prototype이 성공하면 전체 UI와 Rule Manager를 확장한다.

---

# 29. 한 줄 정의

> **Revit의 BIM 객체 정보를 회사의 기존 CAD Layer Standard로 번역하고, 여러 Revit Sheet를 그룹별 단일 DWG의 Model Space에 자동 배열하는 한국형 DWG Export Add-in을 만든다.**
