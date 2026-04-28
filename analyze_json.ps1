$jsonPath = "D:\은현기\01.Project\Programmer\## Civil3D API\지반터널부 요청\TEST\본선라이닝 구조도_RebarData.json"
$json = Get-Content $jsonPath -Raw -Encoding UTF8 | ConvertFrom-Json

$rebars = $json.TransverseRebars
$cycle1 = $rebars | Where-Object { $_.CycleNumber -eq 1 }

Write-Host "=== 전체 TransverseRebars 수: $($rebars.Count) ==="
Write-Host "=== CycleNumber=1 수: $($cycle1.Count) ==="
Write-Host ""

$groups = @{}
foreach ($r in $cycle1) {
    $key = ""
    if ($r.SheetId -match "구조도\(\d+\)") {
        $key = $Matches[0]
    }
    if ($key -ne "") {
        if (-not $groups.ContainsKey($key)) { $groups[$key] = @() }
        $groups[$key] += $r
    }
}

Write-Host "=== 구조도별 Cycle1 철근 수 ==="
foreach ($key in ($groups.Keys | Sort-Object)) {
    $g = $groups[$key]
    $ids = ($g | ForEach-Object { $_.Id }) -join ","
    Write-Host "$key : $($g.Count)개  [Ids: $ids]"
}

Write-Host ""
Write-Host "=== StructureRegions (depth 정보) ==="
if ($json.StructureRegions) {
    foreach ($sr in $json.StructureRegions) {
        Write-Host "$($sr.CycleKey) : BC=($($sr.BoundaryCenterX),$($sr.BoundaryCenterY))"
    }
} else {
    Write-Host "(없음)"
}
