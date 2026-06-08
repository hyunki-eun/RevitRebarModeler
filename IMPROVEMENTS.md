# 코드 검토 — 문제점 및 개선사항

> 작성일: 2026-06-08 · 대상: RevitRebarModeler 전체
> 4개 영역(명령 / 지오메트리 모델 / 일람표·내보내기 / UI·앱) 코드 리뷰 종합.
> 체크박스로 진행 상황을 관리한다.

---

## 🔴 Critical — 동작/배포에 직접 영향

- [ ] **1. 라이선스 가드 시계 롤백 우회** — `Models/LicenseGuard.cs:16`
  - `DateTime.Now.Date > ExpirationDate` 단순 비교 → 윈도우 시계를 되돌리면 무제한 사용.
  - `DateTime.Now`(로컬) → `DateTime.UtcNow` 권장.
  - 각 명령이 `CheckOrBlock()`을 수동 호출 → 한 곳만 빠뜨려도 무방비.
  - **개선:** 최근 관측 날짜를 레지스트리/암호화 파일에 high-water mark 저장해 롤백 탐지 / `CommandBase` 추상클래스로 가드 중앙집중(또는 만료 시 리본 버튼 비활성화) / 진짜 보호 필요 시 RSA 서명 라이선스 토큰 + 머신ID.

- [ ] **2. 숫자 파싱 culture 의존 → 비-한국 로케일에서 트랜잭션 중 크래시**
  - `double.Parse`/`int.Parse` culture 기본 오버로드 광범위 사용. `depth=`/Mark는 코드가 직접 쓰고 읽는 값이라 쓰기/읽기 culture 불일치 시 소수점 깨짐 → `FormatException`.
  - 명령부: `Commands/CreateShearRebarCommand.cs:1184` 등 `ParseDepthFromHost` 4곳
  - 일람표: `Models/RebarScheduleRowDetailed.cs:54-57` `UnitLengthText` 현재 culture 문자열화 후 텍스트 셀 기록
  - UI: `ShearRebarWindow`/`LongitudinalRebarWindow` regex 그룹 파싱
  - **개선:** 기계 생성/regex 토큰은 전부 `CultureInfo.InvariantCulture` 통일, 사용자 입력은 `TryParse` 방어.

- [ ] **3. `SessionCache.TransverseCtcMap` 제자리 변형(세션 오염)** — `Commands/CreateShearRebarCommand.cs:166-172`
  - `?? new` 패턴이지만 세션 값 non-null이면 캐시 참조 그 자체를 변형 → 다음 실행 동작 달라짐.
  - **개선:** `new Dictionary<string,double>(SessionCache.TransverseCtcMap ?? new ...)`로 복사.

- [ ] **4. 전단철근 평면 법선 fallback이 `XYZ.BasisY` → 스터럽 오배치** — `Commands/CreateShearRebarCommand.cs:1124-1147` `TryComputePlane`
  - 시작 다리·상단바가 거의 일직선(ndx≈0)이면 법선 퇴화 → `BasisY` 대체 → 곡선 평면과 비수직 법선으로 `Rebar.CreateFromCurves` 호출돼 스터럽 틀어짐(`vfPlaneBad` 카운터가 이미 감지 중).
  - **개선:** `(pSO-pSI) × (pEO-pSO)` 외적으로 법선 계산, 퇴화 시 fallback 대신 로그 후 실패 처리.

---

## 🟠 Important

- [ ] **5. 일람표/수량의 "조용한 데이터 손실" (수량산출에서 최악)**
  - `Models/RebarScheduleCollector.cs:521-539` `ComputeRebarLengthMm`의 `catch { return 0; }` → 예외 철근이 길이 0으로 집계에서 사라짐(통계 카운터도 안 올림).
  - `Models/RebarScheduleCollector.cs:263,289,290` regex 그룹 `int.Parse`(`\d+` 무제한) → 비정상 Mark에서 `OverflowException`, per-rebar try 없어 전체 일람표 중단.
  - `Models/RebarSchedulePopulator.cs:83-86` `GroupBy(...).g.First()`가 중복 SourceKey 두번째를 경고없이 폐기.
  - **개선:** catch에서 에러 통계+elementId 로그, `int.TryParse`로 교체 후 `Unmatched++` continue.

- [ ] **6. 엑셀 `SaveAs` 예외 처리 없음 → 파일 열려 있으면 크래시** — `Models/ScheduleExcelExporter.cs:28-41`
  - 대상 파일을 Excel로 열어둔 상태면 `IOException`(실사용 최빈 실패). (참고: ClosedXML 사용이라 COM 누수는 없음, `using` 정상.)
  - **개선:** try/catch로 "파일이 열려 있어 저장 불가" 안내, 임시파일 후 이동.

- [ ] **7. 대규모 헬퍼 코드 중복 — 이미 분기되어 잠재 버그**
  - `BuildHostMap`/`ParseDepthFromHost`/`ExtractStructureKey`/`FindRebarBarType`/`ParseDiameter`/`BuildDepthMap` 등이 명령 5개 + UI 4개 + 일람표 2개에 복붙.
  - 이미 어긋남: `FindRebarBarType` 스터럽 정렬이 Longi(`?1:0`, 뒤)와 Shear(`?0:1`, 앞) 반대. 일람표는 Collector↔Populator SourceKey 계약을 수작업 동기화(regex 3중복).
  - **개선:** `RebarCommandHelpers`/`RebarParse` 정적 헬퍼로 통합, regex는 `static readonly Regex`(루프 안 `new Regex`/`Regex.Match` 반복 할당도 해소).

- [ ] **8. `MmToFt`(304.8) 상수 4개 파일 독립 정의** — GeometryConverter, Civil3DCoordinate, ShearRebarFactory(인라인 `/304.8`), 일람표 파일들. 드리프트 위험.
  - **개선:** 단일 상수 또는 `UnitUtils.ConvertFromInternalUnits`.

- [ ] **9. `BuildSheetTransforms`를 루프 안에서 매 시트 재구축** — `Commands/CreateLongitudinalRebarCommand.cs:131-147`
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

- [ ] **13. 죽은 코드 정리**
  - `Models/GeometryConverter.cs`의 `ToRevitPoint/ToCurveLoop/GetLoopPoints` — 어디서도 호출 안 됨(grep 확인). 삭제 권장. (※ 지오메트리 리뷰가 Critical로 본 "원점 미차감"은 이 죽은 코드라 실제 버그 아님.)
  - ShearCommand `BuildBaseCurve`/`TryCreateLineDirectShape`(미사용 `sset` 포함)/`GetBoundaryCenter` 등, Longi `GenerateSamplePositions`/`DistributeCountToPair`/`MatchInnerOuterPairs`, Transverse `FindHookType`, PreviewLongitudinal 주석블록.
- [ ] **14. 하드코딩 매직넘버** — 기본 depth 1000, CTC 200, 후크 100mm, fallback 배수 `7.6923`(주석 없음), epsilon(0.001/0.01/1e-6/1e-9). `const` 승격.
- [ ] **15. `MergeCloseVertices` null/empty 가드 없음** — `Models/GeometryConverter.cs:249` (457행 실사용). 빈 입력 시 예외.
- [ ] **16. `IExternalCommand` 인스턴스 필드** — `CreateTransverseRebarCommand` `_debugLogged` 등, Revit 인스턴스 재사용 시 세션당 1회만 동작. 지역변수로.
- [ ] **17. 기타** — 모달 다이얼로그 `DialogResult=true; Close();` 중복 / 파일명 케이싱 `Previewrebarcurvescommand.cs` / 리본 버튼 아이콘(`LargeImage`) 부재 / `OnShutdown`에서 static `SessionCache` 미정리(문서 전환 시 잔존).

---

## 권장 처리 순서 (효과 대비 비용)

1. culture 파싱 일괄 InvariantCulture화 (#2)
2. SessionCache 복사 (#3)
3. 일람표 조용한 손실 3건 (#5)
4. 헬퍼/regex/MmToFt 통합 (#7, #8)
5. 라이선스 강화 (#1)
