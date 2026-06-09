# 코드 검토 — 문제점 및 개선사항

> 작성일: 2026-06-08 · 대상: RevitRebarModeler 전체
> 4개 영역(명령 / 지오메트리 모델 / 일람표·내보내기 / UI·앱) 코드 리뷰 종합.
> 체크박스로 진행 상황을 관리한다.

---

## 🔴 Critical — 동작/배포에 직접 영향

- [x] **1. 라이선스 가드 시계 롤백 우회** — `Models/LicenseGuard.cs` ✅ UTC 기준 + HKCU 레지스트리 high-water-mark(난독화)로 시계 롤백 탐지 + 신설 `Commands/CommandBase.cs`로 가드 중앙집중(9개 명령 Execute→Run 전환). ※ Revit 실모델에서 롤백/만료 동작 직접 검증 권장.
  - `DateTime.Now.Date > ExpirationDate` 단순 비교 → 윈도우 시계를 되돌리면 무제한 사용.
  - `DateTime.Now`(로컬) → `DateTime.UtcNow` 권장.
  - 각 명령이 `CheckOrBlock()`을 수동 호출 → 한 곳만 빠뜨려도 무방비.
  - **개선:** 최근 관측 날짜를 레지스트리/암호화 파일에 high-water mark 저장해 롤백 탐지 / `CommandBase` 추상클래스로 가드 중앙집중(또는 만료 시 리본 버튼 비활성화) / 진짜 보호 필요 시 RSA 서명 라이선스 토큰 + 머신ID.

- [x] **2. 숫자 파싱 culture 의존 → 비-한국 로케일에서 트랜잭션 중 크래시** ✅ depth round-trip(쓰기/읽기 4곳)·UnitLengthText 출력·자동맵/SourceKey 파싱 InvariantCulture 통일 (※ command 내부 일부 int.Parse는 #7 통합 시 정리)
  - `double.Parse`/`int.Parse` culture 기본 오버로드 광범위 사용. `depth=`/Mark는 코드가 직접 쓰고 읽는 값이라 쓰기/읽기 culture 불일치 시 소수점 깨짐 → `FormatException`.
  - 명령부: `Commands/CreateShearRebarCommand.cs:1184` 등 `ParseDepthFromHost` 4곳
  - 일람표: `Models/RebarScheduleRowDetailed.cs:54-57` `UnitLengthText` 현재 culture 문자열화 후 텍스트 셀 기록
  - UI: `ShearRebarWindow`/`LongitudinalRebarWindow` regex 그룹 파싱
  - **개선:** 기계 생성/regex 토큰은 전부 `CultureInfo.InvariantCulture` 통일, 사용자 입력은 `TryParse` 방어.

- [x] **3. `SessionCache.TransverseCtcMap` 제자리 변형(세션 오염)** — `Commands/CreateShearRebarCommand.cs:166-172` ✅ 복사본 사용으로 수정
  - `?? new` 패턴이지만 세션 값 non-null이면 캐시 참조 그 자체를 변형 → 다음 실행 동작 달라짐.
  - **개선:** `new Dictionary<string,double>(SessionCache.TransverseCtcMap ?? new ...)`로 복사.

- [x] **4. 전단철근 평면 법선 fallback이 `XYZ.BasisY` → 스터럽 오배치** — `Commands/CreateShearRebarCommand.cs` `TryComputePlane` ✅ 모든 커브 방향 쌍 중 가장 비평행한 쌍으로 법선 산출(퇴화 강건) + BasisY 폴백 시 진단 로그. ※ Revit 실모델 검증 권장.
  - 시작 다리·상단바가 거의 일직선(ndx≈0)이면 법선 퇴화 → `BasisY` 대체 → 곡선 평면과 비수직 법선으로 `Rebar.CreateFromCurves` 호출돼 스터럽 틀어짐(`vfPlaneBad` 카운터가 이미 감지 중).
  - **개선:** `(pSO-pSI) × (pEO-pSO)` 외적으로 법선 계산, 퇴화 시 fallback 대신 로그 후 실패 처리.

---

## 🟠 Important

- [x] **5. 일람표/수량의 "조용한 데이터 손실" (수량산출에서 최악)** ✅ LengthError/MarkParseError 통계 추가, NaN 센티넬+TryParse, 중복 SourceKey 로그
  - `Models/RebarScheduleCollector.cs:521-539` `ComputeRebarLengthMm`의 `catch { return 0; }` → 예외 철근이 길이 0으로 집계에서 사라짐(통계 카운터도 안 올림).
  - `Models/RebarScheduleCollector.cs:263,289,290` regex 그룹 `int.Parse`(`\d+` 무제한) → 비정상 Mark에서 `OverflowException`, per-rebar try 없어 전체 일람표 중단.
  - `Models/RebarSchedulePopulator.cs:83-86` `GroupBy(...).g.First()`가 중복 SourceKey 두번째를 경고없이 폐기.
  - **개선:** catch에서 에러 통계+elementId 로그, `int.TryParse`로 교체 후 `Unmatched++` continue.

- [x] **6. 엑셀 `SaveAs` — 잠금 메시지 + atomic 저장** — `Models/ScheduleExcelExporter.cs:28-41` ✅ (호출부 try/catch는 이미 존재) 잠금 사전감지 + 임시파일 후 이동
  - 대상 파일을 Excel로 열어둔 상태면 `IOException`(실사용 최빈 실패). (참고: ClosedXML 사용이라 COM 누수는 없음, `using` 정상.)
  - **개선:** try/catch로 "파일이 열려 있어 저장 불가" 안내, 임시파일 후 이동.

- [x] **7. 대규모 헬퍼 코드 중복 — 이미 분기되어 잠재 버그** ✅ `Models/RebarHelpers.cs` 신설, 9개 파일에서 ExtractStructureKey/ParseDepthFromHost/BuildHostMap/FindRebarBarType/GetBoundaryCenter/ClassifyInnerOuter/GetBarDiameterFt 통합 (`using static`). FindRebarBarType 분기는 `preferStirrup` 파라미터로 보존(Longi/Trans=false, Shear=true).
  - `BuildHostMap`/`ParseDepthFromHost`/`ExtractStructureKey`/`FindRebarBarType`/`ParseDiameter`/`BuildDepthMap` 등이 명령 5개 + UI 4개 + 일람표 2개에 복붙.
  - 이미 어긋남: `FindRebarBarType` 스터럽 정렬이 Longi(`?1:0`, 뒤)와 Shear(`?0:1`, 앞) 반대. 일람표는 Collector↔Populator SourceKey 계약을 수작업 동기화(regex 3중복).
  - **개선:** `RebarCommandHelpers`/`RebarParse` 정적 헬퍼로 통합, regex는 `static readonly Regex`(루프 안 `new Regex`/`Regex.Match` 반복 할당도 해소).

- [x] **8. `MmToFt`(304.8) 상수 중복** ✅ `RebarHelpers.MmToFt`/`FtToMm` 단일 정의. `using static` 적용 파일(명령 5개+Collector)의 사본 const 제거. (※ Civil3DCoordinate/GeometryConverter/Populator/Export는 using static 미적용이라 자체 const 유지 — 차후 필요 시 통합)

- [x] **9. `BuildSheetTransforms`를 루프 안에서 매 시트 재구축** — `Commands/CreateLongitudinalRebarCommand.cs:131-147` ✅ 루프 밖으로 hoist
  - Transverse 명령은 루프 밖(49행)에서 올바르게 호출. Longi만 루프 안 재생성.
  - **개선:** 루프 밖으로 hoist.

- [x] **10. 전단 UI 검증이 잘못된 필드 기준** — `UI/ShearRebarWindow.xaml.cs` `Revalidate()` ✅ 공유 `TryComputeGrouping` 헬퍼 도입, 검증/미리보기 모두 `TransUnitCount`+겹침 묶음 기준으로 일치. (MarkRangeDisplay의 TransRebarCount/2는 가로 위치 개수로 의도된 것이라 유지)
  - `TransRebarCount`(원시 폴리라인 수)+modulo로 검증하나 실제 배치/프리뷰는 `TransUnitCount`(계단수)+겹침 스텝(g-1) 기준 → 메시지가 실제와 모순.
  - **개선:** 그룹 산술을 한 메서드로 묶어 검증/프리뷰/배치 공유.

- [x] **11. 공유파라미터 파일 처리 위험** — `Models/RebarScheduleParameters.cs`, `Models/ShearRebarFactory.cs` ✅ 앱 전역 `SharedParametersFilename`을 try/finally로 원복(다른 프로젝트 영향 방지) + 0바이트 대신 유효 SP 헤더 기록 + `SharedParameterElement.Create` null/캐스트 가드
  - `app.SharedParametersFilename`(앱 전역) 덮어써 다른 프로젝트 영향; `File.WriteAllText(path,"")` 0바이트 파일은 유효 SP 헤더 없어 `OpenSharedParameterFile` 실패 가능.
  - **개선:** 진짜 미설정일 때만 변경+사후 복원, 최소 SP 헤더 기록.

- [x] **12. 광범위한 `catch {}`가 예외 삼킴** ✅ App.cs 탭 생성은 `Autodesk.Revit.Exceptions.ArgumentException`으로 협소화. 메서드 레벨 침묵 catch(StampLabels·Shear/Transverse 자동맵·depth맵)에 진단 Debug 로그 추가. (※ ds.Name 설정·후크 제거·임시파일 삭제 등 idiomatic best-effort 1줄 catch는 의도된 것이라 유지)

---

## 🟡 Minor

- [x] **13. 죽은 코드 정리** ✅ GeometryConverter(ToCurveLoop/GetLoopPoints/ToRevitPoint+미사용 상수), Longi cmd(GenerateSamplePositions/DistributeCountToPair/MatchInnerOuterPairs/CreateOffsetMidpointLineDirectShape), Shear cmd(BuildBaseCurve/GetEndpointFarFromBC/ComputeOutwardDir/TryCreateLineDirectShape/GetOrCreateRedLineStyle/TryCreateModelLines), Transverse(FindHookType), LongiCurveSampler(CentroidX), LongiWindow(GetRefArcLen) 삭제
  - `Models/GeometryConverter.cs`의 `ToRevitPoint/ToCurveLoop/GetLoopPoints` — 어디서도 호출 안 됨(grep 확인). 삭제 권장. (※ 지오메트리 리뷰가 Critical로 본 "원점 미차감"은 이 죽은 코드라 실제 버그 아님.)
  - ShearCommand `BuildBaseCurve`/`TryCreateLineDirectShape`(미사용 `sset` 포함)/`GetBoundaryCenter` 등, Longi `GenerateSamplePositions`/`DistributeCountToPair`/`MatchInnerOuterPairs`, Transverse `FindHookType`, PreviewLongitudinal 주석블록.
- [x] **14. 하드코딩 매직넘버** ✅ 불투명한 `7.6923`을 `100.0/13.0`(D13 후크 폴백) 명명 상수+주석으로, 명령부 fallback depth를 `RebarHelpers.DefaultDepthMm`로 승격. (※ UI 필드 기본값·기하 epsilon은 맥락상 자명/위험 대비 저가치라 유지)
- [x] **15. `MergeCloseVertices` null/empty 가드 없음** — `Models/GeometryConverter.cs` ✅ null/empty 방어 추가
- [x] **16. `IExternalCommand` 인스턴스 필드** — `CreateTransverseRebarCommand` `_verboseDebug`/`_debugLogged` ✅ Execute 지역변수로 이동
- [x] **17. `OnShutdown` SessionCache 정리** — `App.cs` ✅ `SessionCache.Clear()` 추가 + ScheduleExcelExporter stale 주석(9→10) 정정. (※ DialogResult Close 중복/파일명 케이싱/리본 아이콘은 보류 — 무해/저가치)

---

## 권장 처리 순서 (효과 대비 비용)

1. culture 파싱 일괄 InvariantCulture화 (#2)
2. SessionCache 복사 (#3)
3. 일람표 조용한 손실 3건 (#5)
4. 헬퍼/regex/MmToFt 통합 (#7, #8)
5. 라이선스 강화 (#1)

---

# UI/UX 개선 (2026-06-09)

- [x] **U1. 창 크기 정책 통일** ✅ 고정 크기(min=max)였던 구조물생성·횡방향·종방향 + 전단·일람표를 모두 **리사이즈 가능**하게(MaxWidth/MaxHeight 제거), 기본 높이 720으로 통일해 1366×768 화면에서 하단 버튼 잘림 해결. MinWidth/MinHeight만 유지. (콘텐츠가 `*` 행이라 축소 시 리스트만 줄고 버튼은 유지)
- [~] **U2. Enter=주동작(IsDefault)** — 평가 후 **의도적 미적용**. 이 창들은 셀 편집(단일행 TextBox)이 주 입력이라 Enter가 "배치 실행"을 조기 발동시킬 위험 → 버튼 클릭만 유지(Esc=취소는 이미 있음).
- [ ] **U3. 버튼 스타일 공통화** — PrimaryBtn/SecBtn이 5개 창에 복붙(일부 드리프트). 보류: ResourceDictionary 병합은 pack URI 런타임 의존이라 Revit 실행 검증 후 적용 권장.
- [ ] **U4. 입력 검증 시각 피드백(빨간 테두리)** — 보류: 구현 자체는 가능하나 Revit 실행 검증 필요. (전단 창은 상태 칩으로 일부 대체)
- [ ] **U5. 편집 그리드 컨트롤 통일(ListView→DataGrid)** — 보류: 대규모 리팩터링 + 회귀 위험, 시연 후 권장.
- [ ] **U6. 진행 표시(ProgressBar/커서)** — 보류: 실제 배치는 창이 닫힌 뒤 Command 트랜잭션에서 실행돼 창 내 표시 효과 제한적.

> 적용(U1)은 인스톨러 v1.0.8에 반영. 보류 항목(U3~U6)은 Revit 실행 검증을 동반해 함께 진행 권장.
