using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace RevitRebarModeler.Models
{
    /// <summary>
    /// 구조도 사이클별로 구조 프레임 패밀리를 생성하고 프로젝트에 로드합니다.
    /// Generic Model 템플릿 기반 → 카테고리를 StructuralFraming으로 변경
    /// 프로파일: 패밀리 XY 평면 (X=횡단, Y=높이) → Z축 방향 돌출
    /// 배치 시: 패밀리 X→프로젝트 X, 패밀리 Y→프로젝트 Z, 패밀리 Z→프로젝트 Y
    /// </summary>
    public class StructureFamilyCreator
    {
        private readonly Application _app;
        private const double MinCurveLenFt = 0.001;
        private const int ArcSubdivisions = 8;

        /// <summary>패밀리 생성 중 발생한 에러 목록 (디버깅용)</summary>
        public List<string> Errors { get; } = new List<string>();

        /// <summary>Arc 생성이 완전히 실패했는지 여부 (true면 전체 생성 중단 필요)</summary>
        public bool ArcCreationFailed { get; private set; }

        public StructureFamilyCreator(Application app)
        {
            _app = app;
        }

        /// <summary>
        /// 사이클의 모든 Region을 하나의 패밀리로 생성하고 프로젝트에 로드합니다.
        /// 반환: (FamilySymbol, 배치좌표XYZ) — 실패 시 null
        /// </summary>
        public Tuple<FamilySymbol, XYZ> CreateFamily(
            Document projectDoc,
            StructureCycleData cycle,
            double depthMm)
        {
            Errors.Clear();
            ArcCreationFailed = false;

            // ── 1. 패밀리 원점 결정 ──
            // BoundaryCenterX/Y가 있으면 사이클 바운더리 중심 사용 (구조물+철근 공통 기준)
            // 없으면 기존 방식 (구조물 바운딩 박스 중심) 폴백
            double originXMm, originYMm;

            if (cycle.BoundaryCenterX != 0 || cycle.BoundaryCenterY != 0)
            {
                originXMm = cycle.BoundaryCenterX;
                originYMm = cycle.BoundaryCenterY;
                Errors.Add($"[INFO] 원점: BoundaryCenter ({originXMm:F0}, {originYMm:F0}) mm");
            }
            else
            {
                double minX = double.MaxValue, maxX = double.MinValue;
                double minY = double.MaxValue, maxY = double.MinValue;
                foreach (var region in cycle.Regions)
                    foreach (var v in region.Vertices)
                    {
                        minX = Math.Min(minX, v.X); maxX = Math.Max(maxX, v.X);
                        minY = Math.Min(minY, v.Y); maxY = Math.Max(maxY, v.Y);
                    }
                originXMm = (minX + maxX) / 2.0;
                originYMm = (minY + maxY) / 2.0;
                Errors.Add($"[INFO] 원점: BBox 폴백 ({originXMm:F0}, {originYMm:F0}) mm");
            }
            double depthFt = GeometryConverter.MmToFt(depthMm);

            // ── 2. 패밀리 문서 생성 ──
            string templatePath = FindFamilyTemplate();
            Errors.Add($"[INFO] 템플릿: {templatePath}");

            Document famDoc = _app.NewFamilyDocument(templatePath);
            string familyName = SanitizeName(cycle.CycleKey);

            int extrusionCount = 0;

            using (var t = new Transaction(famDoc, "구조 프레임 프로파일 생성"))
            {
                t.Start();

                // 카테고리를 구조 프레임으로 변경
                try
                {
                    Category cat = famDoc.Settings.Categories
                        .get_Item(BuiltInCategory.OST_StructuralFraming);
                    famDoc.OwnerFamily.FamilyCategory = cat;
                    Errors.Add("[INFO] 카테고리 → 구조 프레임 변경 성공");
                }
                catch (Exception ex)
                {
                    Errors.Add($"[WARN] 카테고리 변경 실패: {ex.Message}");
                }

                // 기존 기본 지오메트리 삭제
                DeleteDefaultGeometry(famDoc);

                // 패밀리 기본 참조 평면에서 스케치 평면 찾기
                // (Generic Model 템플릿의 기본 평면 = XY, Z 방향 돌출)
                SketchPlane sketchPlane = FindOrCreateSketchPlane(famDoc);
                Errors.Add($"[INFO] 스케치 평면: {(sketchPlane != null ? sketchPlane.Name : "NULL")}");

                if (sketchPlane == null)
                {
                    Errors.Add("[ERROR] 스케치 평면 생성 실패");
                    t.Commit();
                    famDoc.Close(false);
                    return null;
                }

                // 각 Region을 Extrusion으로 생성
                foreach (var region in cycle.Regions)
                {
                    if (!region.IsClosed || region.Vertices.Count < 3)
                        continue;

                    try
                    {
                        CurveArrArray profileLoops = BuildProfile(region, originXMm, originYMm);
                        
                        if (ArcCreationFailed)
                        {
                            Errors.Add($"[FATAL] Region {region.Id}에서 Arc 생성에 완전히 실패하여 사이클 생성을 중단합니다.");
                            t.RollBack();
                            famDoc.Close(false);
                            return null;
                        }

                        if (profileLoops == null || profileLoops.Size == 0)
                        {
                            Errors.Add($"[WARN] Region {region.Id}: 프로파일 빌드 실패");
                            continue;
                        }

                        Errors.Add($"[INFO] Region {region.Id}: CurveArray 크기={profileLoops.get_Item(0).Size}");

                        famDoc.FamilyCreate.NewExtrusion(true, profileLoops, sketchPlane, depthFt);
                        extrusionCount++;
                        Errors.Add($"[OK] Region {region.Id}: Extrusion 생성 성공");
                    }
                    catch (Exception ex)
                    {
                        Errors.Add($"[ERROR] Region {region.Id}: {ex.GetType().Name} - {ex.Message}");
                    }
                }

                t.Commit();
            }

            Errors.Add($"[INFO] 총 Extrusion 생성: {extrusionCount}개");

            if (extrusionCount == 0)
            {
                famDoc.Close(false);
                return null;
            }

            // ── 3. 임시 저장 후 프로젝트에 로드 ──
            string tempDir = Path.Combine(Path.GetTempPath(), "RevitRebarModeler");
            Directory.CreateDirectory(tempDir);
            string tempPath = Path.Combine(tempDir, familyName + ".rfa");

            var saveOpts = new SaveAsOptions { OverwriteExistingFile = true };
            famDoc.SaveAs(tempPath, saveOpts);
            famDoc.Close(false);

            Family family;
            projectDoc.LoadFamily(tempPath, new OverwriteFamilyLoadOptions(), out family);

            try { File.Delete(tempPath); } catch { }

            if (family == null) return null;

            // ── 4. FamilySymbol 활성화 ──
            FamilySymbol symbol = null;
            foreach (var id in family.GetFamilySymbolIds())
            {
                symbol = projectDoc.GetElement(id) as FamilySymbol;
                break;
            }

            if (symbol == null) return null;
            if (!symbol.IsActive) symbol.Activate();

            // ── 5. 배치 좌표 반환 ──
            // 세션 GlobalOrigin 기준 DWG 절대좌표로 배치 (구조물-철근 공통 원점)
            XYZ placementPoint = Civil3DCoordinate.FamilyPlacementPoint(originXMm, originYMm);

            return Tuple.Create(symbol, placementPoint);
        }

        /// <summary>
        /// Bulge 값으로 Arc 커브 리스트를 생성합니다.
        /// 5단계 폴백으로 Arc 생성을 보장합니다:
        ///   방법 1: 3점 Arc (시작, 중간, 끝)
        ///   방법 2: 중심/반지름/각도 Arc
        ///   방법 3: 2분할 서브Arc (3점 Arc × 2)
        ///   방법 4: N분할 서브Arc (SubdivideArc 점 기반 3점 Arc)
        ///   방법 5: 미세 분할 서브Arc (최후 수단)
        /// </summary>
        private List<Curve> CreateArcCurves(
            double x1Mm, double y1Mm, double x2Mm, double y2Mm, double bulge,
            double originXMm, double originYMm, int regionId, int segIndex)
        {
            XYZ p1 = ToLocalXZ(x1Mm, y1Mm, originXMm, originYMm);
            XYZ p2 = ToLocalXZ(x2Mm, y2Mm, originXMm, originYMm);

            // ── 방법 1: 단일 3점 Arc ──
            try
            {
                var arc = CreateThreePointArc(x1Mm, y1Mm, x2Mm, y2Mm, bulge, originXMm, originYMm);
                if (arc != null)
                    return new List<Curve> { arc };
            }
            catch { }

            // ── 방법 2: 중심/반지름/각도 Arc ──
            try
            {
                var arc = CreateCenterRadiusArc(x1Mm, y1Mm, x2Mm, y2Mm, bulge, originXMm, originYMm);
                if (arc != null)
                    return new List<Curve> { arc };
            }
            catch { }

            Errors.Add($"[INFO] Region {regionId} seg {segIndex}: 단일 Arc 실패 → 서브분할 시도");

            // ── 방법 3: 2분할 서브Arc (중간점에서 분할) ──
            try
            {
                var subArcs = CreateSubArcsFromBulge(
                    x1Mm, y1Mm, x2Mm, y2Mm, bulge, originXMm, originYMm, 2);
                if (subArcs != null && subArcs.Count > 0)
                {
                    Errors.Add($"[OK] Region {regionId} seg {segIndex}: 2분할 Arc 성공 ({subArcs.Count}개)");
                    return subArcs;
                }
            }
            catch { }

            // ── 방법 4: 4분할 서브Arc ──
            try
            {
                var subArcs = CreateSubArcsFromBulge(
                    x1Mm, y1Mm, x2Mm, y2Mm, bulge, originXMm, originYMm, 4);
                if (subArcs != null && subArcs.Count > 0)
                {
                    Errors.Add($"[OK] Region {regionId} seg {segIndex}: 4분할 Arc 성공 ({subArcs.Count}개)");
                    return subArcs;
                }
            }
            catch { }

            // ── 방법 5: 8분할 미세 서브Arc (최후 수단) ──
            try
            {
                var subArcs = CreateSubArcsFromBulge(
                    x1Mm, y1Mm, x2Mm, y2Mm, bulge, originXMm, originYMm, 8);
                if (subArcs != null && subArcs.Count > 0)
                {
                    Errors.Add($"[OK] Region {regionId} seg {segIndex}: 8분할 Arc 성공 ({subArcs.Count}개)");
                    return subArcs;
                }
            }
            catch { }

            // 모든 Arc 시도 실패 → 생성 중단 (직선 근사 사용 안 함)
            ArcCreationFailed = true;
            Errors.Add($"[FATAL] Region {regionId} seg {segIndex}: " +
                $"모든 Arc 생성 방법 실패 — 전체 생성을 중단합니다.\n" +
                $"  좌표: ({x1Mm:F1},{y1Mm:F1}) → ({x2Mm:F1},{y2Mm:F1}), Bulge={bulge:F6}");
            return null;
        }

        /// <summary>
        /// mm 좌표를 패밀리 로컬 XZ 좌표(ft)로 변환
        /// Civil3D X → 패밀리 X (횡단), Civil3D Y → 패밀리 Z (높이)
        /// 프로파일이 XZ 평면 (정면도), Y 방향으로 돌출
        /// </summary>
        private XYZ ToLocalXZ(double xMm, double yMm, double originXMm, double originYMm)
        {
            return new XYZ(
                GeometryConverter.MmToFt(xMm - originXMm),
                0,
                GeometryConverter.MmToFt(yMm - originYMm));
        }

        /// <summary>3점 Arc 생성 (Bulge 기반 중간점 계산)</summary>
        private Curve CreateThreePointArc(
            double x1Mm, double y1Mm, double x2Mm, double y2Mm, double bulge,
            double originXMm, double originYMm)
        {
            // 호 중간점 = 현 중점 + sagitta 방향 변위
            // sagitta 방향 = 중심의 반대편 (현에서 호쪽으로)
            // 올바른 법선: (dy, -dx) 방향에 bulge 부호 적용
            double midXMm = (x1Mm + x2Mm) / 2.0 + bulge * (y2Mm - y1Mm) / 2.0;
            double midYMm = (y1Mm + y2Mm) / 2.0 - bulge * (x2Mm - x1Mm) / 2.0;

            XYZ p1 = ToLocalXZ(x1Mm, y1Mm, originXMm, originYMm);
            XYZ p2 = ToLocalXZ(x2Mm, y2Mm, originXMm, originYMm);
            XYZ midPt = ToLocalXZ(midXMm, midYMm, originXMm, originYMm);

            if (p1.DistanceTo(midPt) < MinCurveLenFt || p2.DistanceTo(midPt) < MinCurveLenFt)
                return null;

            return Arc.Create(p1, p2, midPt);
        }

        /// <summary>
        /// 중심/반지름/각도 기반 Arc 생성
        /// Revit Arc.Create는 항상 CCW (xAxis→yAxis 방향)
        /// bulge > 0 (CCW): startAngle→endAngle 그대로
        /// bulge less than 0 (CW): P1→P2 CW = P2→P1 CCW이므로 angle 교환 후 Reverse
        /// </summary>
        private Curve CreateCenterRadiusArc(
            double x1Mm, double y1Mm, double x2Mm, double y2Mm, double bulge,
            double originXMm, double originYMm)
        {
            double dx = x2Mm - x1Mm;
            double dy = y2Mm - y1Mm;
            double chordLen = Math.Sqrt(dx * dx + dy * dy);
            double theta = 4.0 * Math.Atan(Math.Abs(bulge));
            double sinHalf = Math.Sin(theta / 2.0);

            if (Math.Abs(sinHalf) < 1e-10 || chordLen < 1e-6)
                return null;

            double radius = (chordLen / 2.0) / sinHalf;

            // 호 중심 계산 (mm)
            double midX = (x1Mm + x2Mm) / 2.0;
            double midY = (y1Mm + y2Mm) / 2.0;
            double nx = -dy / chordLen;   // 현에 수직인 단위벡터
            double ny = dx / chordLen;
            double dist = radius * Math.Cos(theta / 2.0);
            double sign = bulge > 0 ? 1.0 : -1.0;
            double cxMm = midX + sign * nx * dist;
            double cyMm = midY + sign * ny * dist;

            // Revit 좌표 변환
            XYZ center = ToLocalXZ(cxMm, cyMm, originXMm, originYMm);
            XYZ p1 = ToLocalXZ(x1Mm, y1Mm, originXMm, originYMm);
            XYZ p2 = ToLocalXZ(x2Mm, y2Mm, originXMm, originYMm);
            double radiusFt = GeometryConverter.MmToFt(radius);

            // 중심에서 P1, P2까지의 각도
            double angleP1 = Math.Atan2(p1.Y - center.Y, p1.X - center.X);
            double angleP2 = Math.Atan2(p2.Y - center.Y, p2.X - center.X);

            if (bulge > 0)
            {
                // CCW: P1 → P2 반시계 방향
                if (angleP2 <= angleP1) angleP2 += 2.0 * Math.PI;
                return Arc.Create(center, radiusFt, angleP1, angleP2,
                    XYZ.BasisX, XYZ.BasisY);
            }
            else
            {
                // CW: P1 → P2 시계 방향
                // = P2 → P1 반시계 방향으로 생성 후 Reverse
                if (angleP1 <= angleP2) angleP1 += 2.0 * Math.PI;
                Arc ccwArc = Arc.Create(center, radiusFt, angleP2, angleP1,
                    XYZ.BasisX, XYZ.BasisY);
                return ccwArc.CreateReversed();
            }
        }

        /// <summary>
        /// SubdivideArc로 호 위의 중간점들을 구한 뒤,
        /// 연속 3점씩 묶어 작은 3점 Arc를 생성합니다.
        /// </summary>
        private List<Curve> CreateSubArcsFromBulge(
            double x1Mm, double y1Mm, double x2Mm, double y2Mm, double bulge,
            double originXMm, double originYMm, int numSegments)
        {
            // SubdivideArc: 호 위의 점 목록 반환 (mm 좌표)
            var arcPoints = GeometryConverter.SubdivideArc(
                x1Mm, y1Mm, x2Mm, y2Mm, bulge, numSegments);

            if (arcPoints == null || arcPoints.Count < 3)
                return null;

            var curves = new List<Curve>();

            // 연속된 점 쌍 사이마다 3점 Arc 생성
            // arcPoints: [p0, p1, p2, ..., pN] — 짝수 인덱스 단위로 3점 Arc
            // 전략: i, i+1, i+2 로 Arc 생성 → 2칸씩 이동
            // 홀수 개수 처리를 위해 겹치기 방식 사용
            for (int i = 0; i < arcPoints.Count - 2; i += 2)
            {
                var ptA = arcPoints[i];
                var ptMid = arcPoints[i + 1];
                var ptB = arcPoints[Math.Min(i + 2, arcPoints.Count - 1)];

                XYZ a = ToLocalXZ(ptA.Item1, ptA.Item2, originXMm, originYMm);
                XYZ m = ToLocalXZ(ptMid.Item1, ptMid.Item2, originXMm, originYMm);
                XYZ b = ToLocalXZ(ptB.Item1, ptB.Item2, originXMm, originYMm);

                if (a.DistanceTo(m) < MinCurveLenFt || m.DistanceTo(b) < MinCurveLenFt)
                {
                    // 너무 짧으면 Line으로
                    if (a.DistanceTo(b) >= MinCurveLenFt)
                        curves.Add(Line.CreateBound(a, b));
                    continue;
                }

                try
                {
                    curves.Add(Arc.Create(a, b, m));
                }
                catch
                {
                    // 서브Arc도 실패하면 해당 구간만 Line으로
                    if (a.DistanceTo(b) >= MinCurveLenFt)
                        curves.Add(Line.CreateBound(a, b));
                }
            }

            // 마지막 점이 누락된 경우 보충 (홀수 분할일 때)
            if (arcPoints.Count % 2 == 0 && arcPoints.Count >= 2)
            {
                var ptPrev = arcPoints[arcPoints.Count - 2];
                var ptLast = arcPoints[arcPoints.Count - 1];
                XYZ prev = ToLocalXZ(ptPrev.Item1, ptPrev.Item2, originXMm, originYMm);
                XYZ last = ToLocalXZ(ptLast.Item1, ptLast.Item2, originXMm, originYMm);

                if (curves.Count > 0)
                {
                    XYZ curveEnd = curves.Last().GetEndPoint(1);
                    if (curveEnd.DistanceTo(last) >= MinCurveLenFt)
                        curves.Add(Line.CreateBound(curveEnd, last));
                }
            }

            return curves.Count > 0 ? curves : null;
        }



        /// <summary>
        /// 정면도용 XZ 스케치 평면 생성 (노멀 = +Y)
        /// 프로파일이 XZ 평면(X=횡단, Z=높이)에 있으므로 반드시 XZ 평면 사용
        /// </summary>
        private SketchPlane FindOrCreateSketchPlane(Document famDoc)
        {
            try
            {
                Plane xzPlane = Plane.CreateByNormalAndOrigin(XYZ.BasisY, XYZ.Zero);
                return SketchPlane.Create(famDoc, xzPlane);
            }
            catch (Exception ex)
            {
                Errors.Add($"[ERROR] XZ 스케치 평면 생성 실패: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Region의 Vertex를 패밀리 로컬 좌표 CurveArrArray로 변환
        /// 패밀리 XY 평면: X=횡단방향(Civil3D X), Y=높이방향(Civil3D Y)
        /// Bulge가 있는 세그먼트는 실제 Revit Arc로 생성 (다중 커브 지원)
        /// </summary>
        private CurveArrArray BuildProfile(
            StructureRegionData region, double originXMm, double originYMm)
        {
            var merged = GeometryConverter.MergeCloseVertices(region.Vertices);
            if (merged.Count < 3) return null;

            int n = merged.Count;
            var curveArray = new CurveArray();

            for (int i = 0; i < n; i++)
            {
                var v1 = merged[i];
                var v2 = merged[(i + 1) % n];

                XYZ p1 = ToLocalXZ(v1.X, v1.Y, originXMm, originYMm);
                XYZ p2 = ToLocalXZ(v2.X, v2.Y, originXMm, originYMm);

                if (p1.DistanceTo(p2) < MinCurveLenFt)
                    continue;

                if (Math.Abs(v1.Bulge) > 1e-8)
                {
                    // Arc 세그먼트: 여러 커브가 반환될 수 있음
                    var arcCurves = CreateArcCurves(
                        v1.X, v1.Y, v2.X, v2.Y, v1.Bulge,
                        originXMm, originYMm, region.Id, i);

                    if (arcCurves == null)
                        return null;  // Arc 완전 실패 → 전체 프로파일 중단

                    foreach (var curve in arcCurves)
                        curveArray.Append(curve);
                }
                else
                {
                    curveArray.Append(Line.CreateBound(p1, p2));
                }
            }

            if (curveArray.Size < 3) return null;

            var result = new CurveArrArray();
            result.Append(curveArray);
            return result;
        }

        /// <summary>패밀리 템플릿 경로 검색 (구조 프레이밍 우선, 일반 모델 폴백)</summary>
        private string FindFamilyTemplate()
        {
            string basePath = _app.FamilyTemplatePath;

            if (!Directory.Exists(basePath))
                throw new FileNotFoundException($"패밀리 템플릿 경로가 없습니다: {basePath}");

            var rftFiles = Directory.GetFiles(basePath, "*.rft", SearchOption.AllDirectories);

            // "가변" / "adaptive" / "conceptual" 제외 필터
            bool IsAdaptive(string fileName)
            {
                string lower = fileName.ToLower();
                return lower.Contains("가변") || lower.Contains("adaptive") || lower.Contains("conceptual");
            }

            // 1순위: 구조 프레이밍 템플릿
            foreach (var f in rftFiles)
            {
                string name = Path.GetFileNameWithoutExtension(f).ToLower();
                if (IsAdaptive(name)) continue;

                if ((name.Contains("structural") && name.Contains("framing")) ||
                    (name.Contains("구조") && (name.Contains("프레이밍") || name.Contains("프레임"))))
                    return f;
            }

            // 2순위: 일반 모델 (가변 제외)
            foreach (var f in rftFiles)
            {
                string name = Path.GetFileNameWithoutExtension(f).ToLower();
                if (IsAdaptive(name)) continue;

                if ((name.Contains("generic") && name.Contains("model")) ||
                    (name.Contains("일반") && name.Contains("모델")))
                    return f;
            }

            // 3순위: 가변이 아닌 아무 rft
            foreach (var f in rftFiles)
            {
                if (!IsAdaptive(Path.GetFileNameWithoutExtension(f)))
                    return f;
            }

            throw new FileNotFoundException(
                $"적합한 패밀리 템플릿을 찾을 수 없습니다.\n검색 경로: {basePath}\n" +
                $"발견된 rft: {string.Join(", ", rftFiles.Select(Path.GetFileName))}");
        }

        /// <summary>기본 템플릿의 기존 Extrusion 등 삭제</summary>
        private void DeleteDefaultGeometry(Document famDoc)
        {
            var forms = new FilteredElementCollector(famDoc)
                .OfClass(typeof(GenericForm))
                .ToElements();

            foreach (var elem in forms)
            {
                try { famDoc.Delete(elem.Id); } catch { }
            }
        }

        /// <summary>파일명으로 사용 불가능한 문자 제거</summary>
        private string SanitizeName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }
    }

    /// <summary>패밀리 로드 시 덮어쓰기 허용</summary>
    internal class OverwriteFamilyLoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = true;
            return true;
        }

        public bool OnSharedFamilyFound(
            Family sharedFamily, bool familyInUse,
            out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = true;
            return true;
        }
    }
}
