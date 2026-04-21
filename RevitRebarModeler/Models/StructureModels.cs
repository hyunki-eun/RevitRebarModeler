using System.Collections.Generic;

namespace RevitRebarModeler.Models
{
    /// <summary>Civil3D JSON 루트 (RebarData.json)</summary>
    public class CivilExportData
    {
        public string ProjectName { get; set; }
        public string SourceDwgPath { get; set; }
        public string ExportedAt { get; set; }
        public string Units { get; set; } = "mm";
        public List<StructureCycleData> StructureRegions { get; set; } = new List<StructureCycleData>();
        public List<TransverseRebarData> TransverseRebars { get; set; } = new List<TransverseRebarData>();
    }

    /// <summary>사이클별 구조물 데이터</summary>
    public class StructureCycleData
    {
        public string CycleKey { get; set; }
        public int RegionCount { get; set; }
        public double BoundaryCenterX { get; set; }
        public double BoundaryCenterY { get; set; }

        /// <summary>Cycle1 중심선 (start.Y ≤ end.Y로 추출 단계에서 정규화됨)</summary>
        public double Cycle1CenterlineStartX { get; set; }
        public double Cycle1CenterlineStartY { get; set; }
        public double Cycle1CenterlineEndX { get; set; }
        public double Cycle1CenterlineEndY { get; set; }

        /// <summary>Cycle2 중심선 — 철근을 Cycle1 위치로 이동시키는 기준점</summary>
        public double Cycle2CenterlineStartX { get; set; }
        public double Cycle2CenterlineStartY { get; set; }
        public double Cycle2CenterlineEndX { get; set; }
        public double Cycle2CenterlineEndY { get; set; }

        /// <summary>Cycle1·Cycle2 중심선이 모두 있을 때 true → rigid transform 사용 가능</summary>
        public bool HasCenterlines { get; set; }

        /// <summary>Civil3D ④에서 선택된 CTC 값 (mm). 0이면 미지정</summary>
        public double Cycle1CtcMm { get; set; }
        public double Cycle2CtcMm { get; set; }

        public List<StructureRegionData> Regions { get; set; } = new List<StructureRegionData>();
    }

    /// <summary>개별 닫힌 영역</summary>
    public class StructureRegionData
    {
        public int Id { get; set; }
        public double Area { get; set; }
        public int VertexCount { get; set; }
        public bool IsClosed { get; set; }
        public string Layer { get; set; }
        public List<StructureVertex> Vertices { get; set; } = new List<StructureVertex>();
    }

    /// <summary>꼭짓점 (X, Y, Bulge)</summary>
    public class StructureVertex
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Bulge { get; set; }
    }

    // ================================================================
    // 횡방향 철근 데이터 모델
    // ================================================================

    /// <summary>횡방향 철근 1개 (여러 Segment로 구성된 하나의 철근 형상)</summary>
    public class TransverseRebarData
    {
        public string Id { get; set; }
        public string SheetId { get; set; }
        public int CycleNumber { get; set; }
        public List<RebarSegment> Segments { get; set; } = new List<RebarSegment>();
        public double DiameterMm { get; set; }
        public string MatchedText { get; set; }
        public string Layer { get; set; }
        public double CenterX { get; set; }
        public double CenterY { get; set; }
        public double MinX { get; set; }
        public double MaxX { get; set; }
        public double MinY { get; set; }
        public double MaxY { get; set; }
    }

    /// <summary>철근의 개별 세그먼트 (Arc 또는 Line)</summary>
    public class RebarSegment
    {
        public string SegmentType { get; set; }
        public RebarPoint StartPoint { get; set; }
        public RebarPoint EndPoint { get; set; }
        public RebarPoint MidPoint { get; set; }
    }

    /// <summary>2D 좌표 점 (mm)</summary>
    public class RebarPoint
    {
        public double X { get; set; }
        public double Y { get; set; }
    }
}
