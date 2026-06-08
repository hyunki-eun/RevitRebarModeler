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

- [ ] **4. 전단철근 평면 법선 fallback이 `XYZ.BasisY` → 스터럽 오배치** — `Commands/CreateShearRebarCommand.cs:1124-1147` `TryComputePlane`
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

- [ ] **10. 전단 UI 검증이 잘못된 필드 기준** — `UI/ShearRebarWindow.xaml.cs:466-490` `Revalidate()`
  - `TransRebarCount`(원시 폴리라인 수)+modulo로 검증하나 실제 배치/프리뷰는 `TransUnitCount`(계단수)+겹침 스텝(g-1) 기준 → 메시지가 실제와 모순.
  - **개선:** 그룹 산술을 한 메서드로 묶어 검증/프리뷰/배치 공유.

- [ ] **11. 공유파라미터 파일 처리 위험** — `Models/RebarScheduleParameters.cs:137-147`, `Models/ShearRebarFactory.cs:136`
  - `app.SharedParametersFilename`(앱 전역) 덮어써 다른 프로젝트 영향; `File.WriteAllText(path,"")` 0바이트 파일은 유효 SP 헤더 없어 `OpenSharedParameterFile` 실패 가능.
  - **개선:** 진짜 미설정일 때만 변경+사후 복원, 최소 SP 헤더 기록.

- [ ] **12. 광범위한 `catch {}`가 예외 삼킴** — `App.cs:41`(탭 중복만 의도했으나 전부 삼킴), `BuildAutoMapsFromRevit`/`BuildDepthMap` 전체 메서드 catch, `StampLabels` 등.
  - **개선:** 기대 예외 타입만 좁게 catch, fallback 시 로그.

---

## 🟡 Minor

- [x] **13. 죽은 코드 정리** ✅ GeometryConverter(ToCurveLoop/GetLoopPoints/ToRevitPoint+미사용 상수), Longi cmd(GenerateSamplePositions/DistributeCountToPair/MatchInnerOuterPairs/CreateOffsetMidpointLineDirectShape), Shear cmd(BuildBaseCurve/GetEndpointFarFromBC/ComputeOutwardDir/TryCreateLineDirectShape/GetOrCreateRedLineStyle/TryCreateModelLines), Transverse(FindHookType), LongiCurveSampler(CentroidX), LongiWindow(GetRefArcLen) 삭제
  - `Models/GeometryConverter.cs`의 `ToRevitPoint/ToCurveLoop/GetLoopPoints` — 어디서도 호출 안 됨(grep 확인). 삭제 권장. (※ 지오메트리 리뷰가 Critical로 본 "원점 미차감"은 이 죽은 코드라 실제 버그 아님.)
  - ShearCommand `BuildBaseCurve`/`TryCreateLineDirectShape`(미사용 `sset` 포함)/`GetBoundaryCenter` 등, Longi `GenerateSamplePositions`/`DistributeCountToPair`/`MatchInnerOuterPairs`, Transverse `FindHookType`, PreviewLongitudinal 주석블록.
- [ ] **14. 하드코딩 매직넘버** — 기본 depth 1000, CTC 200, 후크 100mm, fallback 배수 `7.6923`(주석 없음), epsilon(0.001/0.01/1e-6/1e-9). `const` 승격.
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
