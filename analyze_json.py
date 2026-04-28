import json, re, sys
from collections import defaultdict

path = r"D:\은현기\01.Project\Programmer\## Civil3D API\지반터널부 요청\TEST\본선라이닝 구조도_RebarData.json"
with open(path, encoding="utf-8-sig") as f:
    data = json.load(f)

rebars = data.get("TransverseRebars", [])
cycle1 = [r for r in rebars if r.get("CycleNumber") == 1]

print(f"=== TransverseRebars 전체: {len(rebars)}개 ===")
print(f"=== CycleNumber=1: {len(cycle1)}개 ===\n")

groups = defaultdict(list)
for r in cycle1:
    sheet_id = r.get("SheetId", "")
    m = re.search(r"구조도\(\d+\)", sheet_id)
    key = m.group(0) if m else "(unknown)"
    groups[key].append(r)

print("=== 구조도별 Cycle1 철근 수 ===")
for key in sorted(groups.keys()):
    g = groups[key]
    ids = [str(r.get("Id","?")) for r in g]
    print(f"  {key}: {len(g)}개  Ids=[{', '.join(ids)}]")

# depth 관련 정보 없으면 안내
print("\n=== StructureRegions ===")
regions = data.get("StructureRegions", [])
if regions:
    for sr in regions:
        ck = sr.get("CycleKey","")
        bcx = sr.get("BoundaryCenterX", 0)
        bcy = sr.get("BoundaryCenterY", 0)
        print(f"  {ck}: BC=({bcx:.1f},{bcy:.1f})")
else:
    print("  (없음)")
