# 코드 리뷰 — 개선 대기 항목

작성일: 2026-04-21

횡방향 철근 배치 파이프라인의 개선 가능 포인트. 실제 결과에 영향이 큰 순서대로 정렬.
실패 사유 파일 로깅(#6)은 먼저 구현 완료됨.

---

## 🔴 기능 문제 (실제 결과에 영향)

### 1. Cycle2가 없는 sheet → 홀수 단에 구멍

**위치**: `CreateTransverseRebarCommand.cs` Execute 루프 내 `currentRebars` 분기

```csharp
var currentRebars = isCycle1Turn ? sheetCycle1 : sheetCycle2;
if (currentRebars.Count == 0) continue;   // ← 해당 위치 통째로 스킵
```

**증상**: 구조도에 cycle2가 아예 없으면 1,3,5,...번째 단은 빈 공간. 결과적으로 CTC×2 간격으로 cycle1만 배치되는 효과.

**논의 포인트**:
- cycle2가 없는 구조도의 경우 cycle1로 대체할지?
- 또는 cycle2 없음을 명시적으로 오류 처리할지?
- Civil3D 추출 시점에 cycle2가 빠진 것이 의도된 것인지 (예: 중심선이 대칭이라 cycle2 생략) 확인 필요.

---

### 2. 끝단 보정 단의 cycle이 parity에 종속

**위치**: 같은 파일, `copy % 2 == 0` 기반 cycle 결정

**증상**: 방금 추가한 끝단 보정 단(nFull+1번째)은 parity에 따라 cycle이 결정됨.
- 예: CTC=300, depth=1000 → copies=8, copy=7은 cycle2
- 예: CTC=400, depth=1000 → copies=6, copy=5는 cycle2

**논의 포인트**:
- 실제 시공에서 depth 양 끝이 어느 cycle이어야 하는지 규칙이 있나?
- 대칭 구조 (첫 단 = 끝 단 같은 cycle) 요구사항이 있다면 parity 강제 조정 필요.

---

### 3. Zero-length Line segments로 인한 chain 깨짐

**위치**: `CreateTransverseRebarCommand.cs` `SegmentToCurve`

```csharp
if (p1.DistanceTo(p2) < 0.001) return null;
```

**증상**: 이 segment를 조용히 버리면 곡선 체인이 끊겨 `CreateFromCurves`가 disconnected 에러. 구조도(4)~(8)에서 실패 원인일 가능성 매우 높음.

**논의 포인트**:
- **Civil3D 추출 단계**에서 zero-length segment 자체를 걸러내는 것이 최선
- 또는 Revit에서 연속된 segment들 사이의 미세 갭을 자동 reconnect 로직 추가
- 필터링하면 chain이 깨질 경우 FreeForm만 사용하도록 우회

---

## 🟡 정밀도/품질 문제

### 4. ArcMerger 반복 병합 시 Center/Radius drift

**위치**: `ArcMerger.cs:86-88`

```csharp
double cx = (a.CenterX.Value + b.CenterX.Value) / 2.0;
double cy = (a.CenterY.Value + b.CenterY.Value) / 2.0;
double r = (a.Radius.Value + b.Radius.Value) / 2.0;
```

**증상**: 평균으로 계속 합치면 N번 병합 후 첫 원본에서 미세하게 멀어짐. 오차 누적.

**논의 포인트**:
- **개선안 A**: 가장 긴 arc의 Center/Radius를 유지 (각도 sweep 기준)
- **개선안 B**: 각도 가중 평균
- **개선안 C**: Civil3D 원본 Center/Radius는 동일 원이면 이미 거의 같으므로 그냥 첫 arc 값 사용

---

### 5. ArcMerger dead code + edge case

**위치**: `ArcMerger.cs:133-143`

- `IsArcCcw` 메서드는 선언만 되고 미사용 (dead code)
- sweep ≈ 0 또는 ≈ 2π 근처에서 `NormalizeAngle` 경계 동작 모호

**논의 포인트**: 삭제 or 검증 테스트 추가.

---

### 6. 실패 사유 per-diameter 단일 기록 ✅ 완료

**위치**: `CreateTransverseRebarCommand.cs`

`failureDetails` 리스트 추가 + `%TEMP%\RevitRebarModeler\Logs\TransverseRebar_{timestamp}.log`로 저장. 각 실패에 structureKey, rebar.Id, copy, cycle, diameter, standard/freeform 에러, stack trace 기록.

---

## 🟢 UX/경량 개선

### 7. Hook orientation이 고정값 (`Left`)

**위치**: `CreateTransverseRebarCommand.cs` `Rebar.CreateFromCurves` 호출부

```csharp
RebarHookOrientation.Left, RebarHookOrientation.Left,
```

**증상**: 곡선 방향/host 방향에 따라 훅이 반대 방향으로 나올 수 있음.

**논의 포인트**: 곡선 end tangent × host normal로 자동 결정? 또는 UI에서 옵션?

---

### 8. `depthMm` 기본값 1000 조용히 fallback

**위치**: `CreateTransverseRebarCommand.cs:99-100`

```csharp
double depthMm = ParseDepthFromHost(hostElement);
if (depthMm <= 0) depthMm = 1000;
```

**증상**: host comment에서 depth 파싱 실패 시 알림 없이 1000mm 사용 → 잘못된 개수 배치 가능.

**논의 포인트**: 파싱 실패 시 errors 리스트에 경고 추가, 또는 사용자 확인 Dialog.

---

## 기타 관찰

- `_debugLogged` 는 첫 rebar 1건에 대해서만 상세 로그 → 문제 재현 시 부족할 수 있음 (지금은 failureDetails가 대체)
- `GlobalOrigin`이 static이지만 `ResetGlobalOrigin()` 매번 호출하므로 OK
- `Standard → FreeForm` fallback은 모든 케이스에 시도되므로 실패 시 비용 2배. 특정 조건(segment 수, arc 비율)에서 바로 FreeForm으로 가도록 분기 가능.
