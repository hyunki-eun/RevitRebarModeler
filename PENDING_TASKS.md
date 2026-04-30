# 펜딩 작업 정리

WORKFLOW.md 기반 메인 흐름 외에 결정/진행 중인 작업의 상세 컨텍스트.
세션이 끊겨도 다른 세션에서 이어 작업할 수 있도록 의도/근거/영향 파일을 함께 기록.

작성일: 2026-04-30

---

## 1. 횡철근 직경별 분리 (큰 직경 우선) — 종방향 배치 정확도 개선

### 배경

현재 종방향 철근 배치 시, 같은 구조도(N) 안의 횡방향 철근 폴리라인을 inner/outer로만 분류하고 직경 구분 없이 합쳐서 사용함.

문제 상황:
- 횡철근 inner 3개가 H25 / H29 / H25 처럼 직경이 섞여 있을 때
- 현재 코드는 직경 평균값(예: (25+29+25)/3 = 26.33) 으로 처리 → 실제 존재하지 않는 직경
- 또한 inner 3개 폴리라인을 `ConcatenatePolylinesTrimmed`로 호장 위에 겹쳐서 합치는데, 겹침 구간에서 어느 직경이 우선되는지 결정 안 됨

### 결정된 규칙 (사용자 합의)

**겹침 구간에서 직경 큰 게 우선**

예시 (호장 mm 좌표, 같은 inner 측):
```
A (H25): 0      ─ 3000
B (H29):    2000 ─────── 7000
C (H25):                 6500 ─── 9000

→ 분할 결과
0    ─ 2000  : H25 (A 단독)
2000 ─ 7000  : H29 (B 우선; 겹친 A·C 구간 포함)
7000 ─ 9000  : H25 (C 단독)
```

outer도 동일 규칙 적용. inner / outer 각각 별도 chain.

### 알고리즘

1. 폴리라인을 호장 좌표(start_mm ~ end_mm)와 `DiameterMm`로 매핑
2. 직경 **큰 순**으로 정렬해서 호장 라인 위에 차례로 점유 → 큰 직경이 차지한 구간은 작은 직경이 못 침범
3. 결과: 구간별 dominant 직경 chain. 예: `[0-2000:H25][2000-7000:H29][7000-9000:H25]`
4. 종방향 sample point의 호장 위치 → 해당 구간 직경 → 그 직경의 폴리라인을 ray 교차 reference로 사용

### 영향 받는 코드

- `RevitRebarModeler/Commands/CreateLongitudinalRebarCommand.cs`
  - 138~142줄: `innerSegLists`, `outerSegLists` 만드는 부분 → 직경별 구간 chain으로 교체 필요
  - 318~336줄: `IntersectRayWithPolyline(concatInner/concatOuter, ...)` → 구간별 직경 라인과 교차하도록 변경
- `RevitRebarModeler/UI/LongitudinalRebarWindow.xaml.cs:106-133`
  - `avgInnerDiam`, `avgOuterDiam`, `avgTransDiam` 평균 계산 부분 → 구간별 직경 정보로 교체
- `RevitRebarModeler/Models/LongiCurveSampler.cs`
  - 직경 정보를 가진 chain 구조를 새로 정의 (예: `DiameterAwareTrimmedSeg` 또는 별도 메타데이터)
  - 호장 위치 → 직경 lookup 함수 추가

### 현재 데이터로는 검증 불가

`TEST/본선라이닝 구조도_RebarData.json` 은 모두 **H16 단일 직경** → 이 케이스를 재현하려면 다중 직경이 섞인 JSON 데이터가 필요.

### 상태

- [x] 규칙 합의 완료 (직경 큰 거 우선)
- [x] 알고리즘 합의 완료 (호장 위 큰 거부터 점유)
- [ ] 직경 정보를 보존하는 chain 자료구조 구현
- [ ] `IntersectRayWithPolyline` 직경별 분리 호출로 교체
- [ ] 다직경 JSON 테스트 데이터 확보 후 검증

---

## 2. 전단 철근 Shape-based 변환 — Revit 실제 동작 검증

### 배경

기존 FreeForm 방식 → Shape-based(Standard) 방식으로 변경.
- `RebarHookType.Create(doc, π/2, multiplier)` 로 90° 100mm hook 동적 생성
- 5선 'ㄷ'자형(top + 2 legs + 2 hooks) → 3선(pSI → pSO → pEO → pEI) 구조로 단순화
- hook은 RebarHookType 객체로 부착

### 영향 받는 코드

- `RevitRebarModeler/Commands/CreateShearRebarCommand.cs`
  - `EnsureHookType90(doc, barDiameterFt, 100.0)` 메서드 (기존 `FindHookType` 대체)
  - hook 객체는 barType마다 1번 생성 (이름 캐싱: `Hook_90_100mm_D{직경}`)
  - `TryCreateShearRebar`에 hookType 전달

### 검증 필요 항목

- [ ] hook 방향이 의도대로 나오는지 (Left/Right swap 필요할 수 있음)
- [ ] 곡면 호스트에서 Z drop 위치 fine-tune
- [ ] Standard 생성 실패 시 `CreateFromRebarShape` fallback 필요한지 판단

### 상태

- [x] 빌드 성공 (CS1061 에러 fix 완료)
- [ ] Revit에서 실제 배치 → hook 방향 / 위치 검증
- [ ] 필요 시 fallback 추가

---

## 3. 일람표 데이터 추출 버튼 — 보류 중

### 배경

리본탭에 새 버튼 추가. 현재 문서의 모든 Rebar를 Excel 일람표로 추출.

### 결정된 사양

- **출력 형식**: Excel (.xlsx)
- **추출 범위**: Revit 문서 전체 Rebar
- **컬럼** (사용자 지정):
  - 패턴 (longi / trans / shear / unknown — Mark 패턴 분류)
  - 직경 (mm)
  - 전체 철근 길이 (개당, mm)
  - 수량
  - 철근 set 개수
  - 철근 총길이
- **제외된 컬럼** (사용자가 "없으면 하지마"로 빼기로 함):
  - 단위중량 / 총중량
  - 1m당 철근
  - 해설
  - 일람표 마크
- **행 단위**: Mark별 묶기 1행 제안 (수량 = 같은 Mark 개수, set = NumberOfBarPositions, 총길이 = 개당길이 × set × 수량). 인스턴스 1개당 1행 옵션도 가능 — 사용자 최종 확정 필요.

### 영향 받는 코드 (예정)

- 새 Command 클래스: `RevitRebarModeler/Commands/ExportScheduleToExcelCommand.cs`
- 리본탭 등록: `RevitRebarModeler/Application.cs` (또는 ribbon 정의 위치)
- Excel 작성: ClosedXML 또는 EPPlus NuGet 추가 필요 — 또는 기존 라이브러리 사용 여부 확인

### 상태

- [x] 사양 합의 완료 (컬럼, 형식)
- [ ] 행 단위 (Mark별 vs 인스턴스별) 최종 확정
- [ ] Excel 라이브러리 결정
- [ ] Command 구현
- [ ] 리본 버튼 등록

---

## 참고 — 최근 완료된 작업 (요약)

- JSON 중심선 perpendicular 방식 → BuildCenterCurve(inner+outer 평균) 방식으로 변경 후 그대로 유지
- `DirectShape중앙선_{structureKey}` 빨간색 시각화는 **호출 제거** (cleanup 필터에는 mark 패턴 남겨둬서 기존에 만들어진 것은 다음 실행 시 자동 삭제)
- 전단 철근 5선 → 3선 단순화 + RebarHookType 동적 생성 코드 적용 완료 (실제 검증 대기)
