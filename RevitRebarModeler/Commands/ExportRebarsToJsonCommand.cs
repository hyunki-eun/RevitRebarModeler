using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

using Newtonsoft.Json;

namespace RevitRebarModeler.Commands
{
    /// <summary>
    /// 현재 Revit 문서의 철근(Rebar)을 Mark 패턴으로 분류해 JSON으로 추출.
    /// 분류:
    ///   - 종방향(longi):   구조도(N)_longi_(outer|inner)_M단
    ///   - 횡방향(trans):   구조도(N)_M단_(inner|outer)_K
    ///   - 전단(shear):     구조도(N)_shear_종N_횡A-B_(A|B)
    ///   - 기타(unknown):   매칭 안 되는 Mark
    /// 좌표는 Revit world (mm). 비교/검증용.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class ExportRebarsToJsonCommand : IExternalCommand
    {
        private const double FtToMm = 304.8;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uidoc = commandData.Application.ActiveUIDocument;
            var doc = uidoc.Document;

            // 현재 선택된 요소 중 Rebar만 필터링
            var selIds = uidoc.Selection.GetElementIds();
            var allRebars = selIds
                .Select(id => doc.GetElement(id))
                .OfType<Rebar>()
                .ToList();

            if (allRebars.Count == 0)
            {
                // 선택이 없으면 전체로 갈지 묻기
                var td = new TaskDialog("철근 추출")
                {
                    MainInstruction = "선택된 Rebar가 없습니다.",
                    MainContent = "문서 전체의 Rebar를 추출할까요?\n\n[예] 전체 추출\n[아니오] 취소 (Revit에서 Rebar를 먼저 선택하세요)",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                    DefaultButton = TaskDialogResult.No
                };
                if (td.Show() != TaskDialogResult.Yes) return Result.Cancelled;

                allRebars = new FilteredElementCollector(doc)
                    .OfClass(typeof(Rebar))
                    .Cast<Rebar>()
                    .ToList();

                if (allRebars.Count == 0)
                {
                    TaskDialog.Show("철근 추출", "문서에 Rebar 요소가 없습니다.");
                    return Result.Cancelled;
                }
            }

            var longiRegex = new Regex(@"^(구조도\(\d+\))_longi_(outer|inner)_(\d+)단$");
            var transRegex = new Regex(@"^(구조도\(\d+\))_(\d+)단_(inner|outer)_(\d+)$");
            var shearRegex = new Regex(@"^(구조도\(\d+\))_shear_종(\d+)_횡(\d+)-(\d+)_(A|B)$");

            var longis = new List<object>();
            var transes = new List<object>();
            var shears = new List<object>();
            var unknowns = new List<object>();

            foreach (var r in allRebars)
            {
                string mark = r.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";
                double diaMm = GetDiameterMm(doc, r);
                var curves = ExtractCurves(r);

                Match m;
                if ((m = longiRegex.Match(mark)).Success)
                {
                    longis.Add(new
                    {
                        mark,
                        structureKey = m.Groups[1].Value,
                        side = m.Groups[2].Value,
                        dan = int.Parse(m.Groups[3].Value),
                        diameterMm = diaMm,
                        curves
                    });
                }
                else if ((m = transRegex.Match(mark)).Success)
                {
                    transes.Add(new
                    {
                        mark,
                        structureKey = m.Groups[1].Value,
                        dan = int.Parse(m.Groups[2].Value),
                        side = m.Groups[3].Value,
                        index = int.Parse(m.Groups[4].Value),
                        diameterMm = diaMm,
                        curves
                    });
                }
                else if ((m = shearRegex.Match(mark)).Success)
                {
                    shears.Add(new
                    {
                        mark,
                        structureKey = m.Groups[1].Value,
                        longiDan = int.Parse(m.Groups[2].Value),
                        bundleStart = int.Parse(m.Groups[3].Value),
                        bundleEnd = int.Parse(m.Groups[4].Value),
                        bundleGroup = m.Groups[5].Value,
                        diameterMm = diaMm,
                        curves
                    });
                }
                else
                {
                    unknowns.Add(new
                    {
                        mark,
                        diameterMm = diaMm,
                        curves
                    });
                }
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"Rebars_{DateTime.Now:yyyyMMdd_HHmmss}.json",
                Filter = "JSON 파일 (*.json)|*.json|모든 파일 (*.*)|*.*",
                DefaultExt = "json",
                Title = "철근 데이터 JSON 저장 위치 선택"
            };
            if (dlg.ShowDialog() != true) return Result.Cancelled;

            var output = new
            {
                exportedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                rvtFile = doc.Title,
                coordinateUnit = "mm (Revit world)",
                summary = new
                {
                    longitudinal = longis.Count,
                    transverse = transes.Count,
                    shear = shears.Count,
                    unknown = unknowns.Count
                },
                longitudinal = longis.OrderBy(o => SortKey(o)).ToList(),
                transverse = transes.OrderBy(o => SortKey(o)).ToList(),
                shear = shears.OrderBy(o => SortKey(o)).ToList(),
                unknown = unknowns
            };

            try
            {
                File.WriteAllText(
                    dlg.FileName,
                    JsonConvert.SerializeObject(output, Formatting.Indented),
                    System.Text.Encoding.UTF8);

                TaskDialog.Show("철근 추출 완료",
                    $"종방향(longi): {longis.Count}개\n" +
                    $"횡방향(trans): {transes.Count}개\n" +
                    $"전단(shear): {shears.Count}개\n" +
                    $"기타(unknown): {unknowns.Count}개\n\n" +
                    $"저장: {dlg.FileName}");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("저장 오류", $"{ex.GetType().Name}: {ex.Message}");
                return Result.Failed;
            }
        }

        private static double GetDiameterMm(Document doc, Rebar rebar)
        {
            try
            {
                var bType = doc.GetElement(rebar.GetTypeId()) as RebarBarType;
                if (bType == null) return 0;
                var p = bType.get_Parameter(BuiltInParameter.REBAR_BAR_DIAMETER);
                return p != null ? p.AsDouble() * FtToMm : 0;
            }
            catch { return 0; }
        }

        private static List<object> ExtractCurves(Rebar rebar)
        {
            var list = new List<object>();
            try
            {
                var curves = rebar.GetCenterlineCurves(false, false, false,
                    MultiplanarOption.IncludeOnlyPlanarCurves, 0);
                foreach (var c in curves)
                {
                    if (c is Arc arc)
                    {
                        var s = arc.GetEndPoint(0);
                        var e = arc.GetEndPoint(1);
                        var mid = arc.Evaluate(0.5, true);
                        list.Add(new
                        {
                            type = "Arc",
                            startMm = ToMm(s),
                            midMm = ToMm(mid),
                            endMm = ToMm(e),
                            radiusMm = arc.Radius * FtToMm
                        });
                    }
                    else
                    {
                        var s = c.GetEndPoint(0);
                        var e = c.GetEndPoint(1);
                        list.Add(new
                        {
                            type = c is Line ? "Line" : c.GetType().Name,
                            startMm = ToMm(s),
                            endMm = ToMm(e)
                        });
                    }
                }
            }
            catch { }
            return list;
        }

        private static double[] ToMm(XYZ p) => new[]
        {
            Math.Round(p.X * FtToMm, 4),
            Math.Round(p.Y * FtToMm, 4),
            Math.Round(p.Z * FtToMm, 4)
        };

        /// <summary>구조도 → side → dan 순으로 정렬되도록 키 생성.</summary>
        private static string SortKey(object o)
        {
            try
            {
                var t = o.GetType();
                string sk = t.GetProperty("structureKey")?.GetValue(o)?.ToString() ?? "";
                int dan = (int?)t.GetProperty("dan")?.GetValue(o)
                       ?? (int?)t.GetProperty("longiDan")?.GetValue(o)
                       ?? 0;
                string side = t.GetProperty("side")?.GetValue(o)?.ToString() ?? "";
                int idx = (int?)t.GetProperty("index")?.GetValue(o) ?? 0;
                return $"{sk}|{dan:D6}|{side}|{idx:D6}";
            }
            catch { return ""; }
        }
    }
}
