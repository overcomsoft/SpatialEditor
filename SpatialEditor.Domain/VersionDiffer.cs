// ============================================================================
// VersionDiffer.cs : 두 도면 상태의 차이 계산기 (순수 계산, DB 접근 없음)
//
//   Compare(이전 블록, 이후 블록, 이전 일반객체, 이후 일반객체)
//     |- CompareBlocks   : object_uid 로 매칭
//     |     - 이후에만 있음 -> Added / 이전에만 있음 -> Removed
//     |     - 양쪽에 있음  -> 위치(Moved) / 회전(Rotated) / 블록명·배율(Reshaped) / 레이어·속성(AttributesChanged)
//     |- CompareFeatures : object_uid 로 매칭
//     |     - Added / Removed / 지오메트리 변경(Reshaped) / 레이어·속성(AttributesChanged)
//     -> DiffEntry 들을 VersionDiff 로 묶어 반환
//
//   허용 오차: 위치 PositionTolerance(1e-4), 각도 AngleTolerance(1e-4도), 배율 1e-9, 지오메트리 EqualsExact 1e-6.
//   부동소수 저장/변환 오차로 "안 움직였는데 움직임" 으로 오탐되는 것을 막기 위한 값이다.
//   결과(Models)는 Versions.cs 의 VersionDiff/DiffEntry 이며 버전 비교 화면과 저장소가 사용한다.
// ============================================================================
using System.Globalization;
using NetTopologySuite.Geometries;

namespace SpatialEditor.Domain;

/// <summary>
/// 도면의 두 상태(버전 대 버전, 또는 버전 대 작업본)를 각 객체의 영구 <c>object_uid</c> 로 비교합니다.
/// 이렇게 하면 이동·회전한 객체를 "하나 삭제 + 하나 추가"가 아니라 같은 객체의 변경으로 인식합니다.
/// </summary>
public static class VersionDiffer
{
    /// <summary>이 값(도면 단위)보다 가까운 위치 차이는 이동하지 않은 것으로 봅니다.</summary>
    public const double PositionTolerance = 1e-4;

    /// <summary>이 값(도)보다 작은 각도 차이는 회전하지 않은 것으로 봅니다.</summary>
    public const double AngleTolerance = 1e-4;

    // Placement keys change with every move/rotation and are compared as real numbers instead;
    // the DXF handle only exists on objects that came from a DXF.
    // 배치 관련 속성 키입니다. 이동/회전할 때마다 값이 바뀌므로 문자열 속성 비교에서는 제외하고
    // (위치·각도·배율은 실수로 따로 비교), DXF 핸들은 DXF 에서 온 객체에만 있어 비교 대상이 아닙니다.
    // 대소문자를 구분하지 않습니다.
    private static readonly HashSet<string> PlacementKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "block_insert_x", "block_insert_y", "block_rotation_deg", "block_scale_x", "block_scale_y", "block_handle"
    };

    /// <summary>두 상태의 블록과 일반 객체를 비교해 차이 목록을 만듭니다.</summary>
    /// <param name="beforeBlocks">이전 상태의 블록 목록(null 불가, 비어 있을 수 있음).</param>
    /// <param name="afterBlocks">이후 상태의 블록 목록.</param>
    /// <param name="beforeFeatures">이전 상태의 일반 객체 목록.</param>
    /// <param name="afterFeatures">이후 상태의 일반 객체 목록.</param>
    /// <returns>블록 차이 다음에 일반 객체 차이가 이어지는 <see cref="VersionDiff"/>.</returns>
    public static VersionDiff Compare(
        IReadOnlyCollection<VersionBlockItem> beforeBlocks, IReadOnlyCollection<VersionBlockItem> afterBlocks,
        IReadOnlyCollection<VersionFeatureItem> beforeFeatures, IReadOnlyCollection<VersionFeatureItem> afterFeatures)
    {
        var entries = new List<DiffEntry>();
        CompareBlocks(beforeBlocks, afterBlocks, entries);
        CompareFeatures(beforeFeatures, afterFeatures, entries);
        return new VersionDiff(entries);
    }

    /// <summary>블록 인스턴스를 object_uid 기준으로 비교해 차이를 entries 에 추가합니다.</summary>
    /// <param name="before">이전 블록 목록. object_uid 는 목록 안에서 유일해야 합니다(중복이면 ToDictionary 가 예외).</param>
    /// <param name="after">이후 블록 목록.</param>
    /// <param name="entries">차이를 누적할 목록(출력).</param>
    private static void CompareBlocks(
        IReadOnlyCollection<VersionBlockItem> before, IReadOnlyCollection<VersionBlockItem> after, List<DiffEntry> entries)
    {
        // 1) 양쪽을 object_uid 로 조회할 수 있게 사전으로 만듭니다.
        var beforeByUid = before.ToDictionary(item => item.ObjectUid);
        var afterByUid = after.ToDictionary(item => item.ObjectUid);

        // 2) 이후에만 있는 객체 = 추가됨.
        foreach (var item in after.Where(item => !beforeByUid.ContainsKey(item.ObjectUid)))
        {
            entries.Add(new DiffEntry(DiffKind.Added, true, item.ObjectUid, item.BlockName, item.LayerName,
                null, Describe(item), null, item.OuterGeometry));
        }

        // 3) 이전에만 있는 객체 = 삭제됨.
        foreach (var item in before.Where(item => !afterByUid.ContainsKey(item.ObjectUid)))
        {
            entries.Add(new DiffEntry(DiffKind.Removed, true, item.ObjectUid, item.BlockName, item.LayerName,
                Describe(item), null, item.OuterGeometry, null));
        }

        // 4) 양쪽에 모두 있는 객체는 항목별로 변경 여부를 판단합니다. 한 객체가 여러 종류(이동+회전 등)로 동시에 보고될 수 있습니다.
        foreach (var (uid, a) in beforeByUid)
        {
            if (!afterByUid.TryGetValue(uid, out var b))
            {
                continue;
            }

            // 4-1) 이동: 삽입점 좌표가 모두 있을 때만 두 점 사이 거리를 계산하고, 허용 오차를 넘으면 이동으로 판정합니다.
            if (a.InsertX is not null && a.InsertY is not null && b.InsertX is not null && b.InsertY is not null
                && Math.Sqrt(Math.Pow(a.InsertX.Value - b.InsertX.Value, 2) + Math.Pow(a.InsertY.Value - b.InsertY.Value, 2)) > PositionTolerance)
            {
                entries.Add(new DiffEntry(DiffKind.Moved, true, uid, b.BlockName, b.LayerName,
                    $"({Num(a.InsertX.Value)}, {Num(a.InsertY.Value)})", $"({Num(b.InsertX.Value)}, {Num(b.InsertY.Value)})",
                    a.OuterGeometry, b.OuterGeometry));
            }

            // 4-2) 회전: 0/360 경계를 고려한 최소 각도 차이로 비교합니다.
            if (AngleDifference(a.RotationDeg, b.RotationDeg) > AngleTolerance)
            {
                entries.Add(new DiffEntry(DiffKind.Rotated, true, uid, b.BlockName, b.LayerName,
                    $"{Num(a.RotationDeg)}°", $"{Num(b.RotationDeg)}°", a.OuterGeometry, b.OuterGeometry));
            }

            // 4-3) 형상 변경: 블록 종류가 바뀌었거나 X/Y 배율이 미세 오차(1e-9)보다 크게 달라진 경우.
            if (!string.Equals(a.BlockName, b.BlockName, StringComparison.Ordinal)
                || Math.Abs(a.ScaleX - b.ScaleX) > 1e-9 || Math.Abs(a.ScaleY - b.ScaleY) > 1e-9)
            {
                entries.Add(new DiffEntry(DiffKind.Reshaped, true, uid, b.BlockName, b.LayerName,
                    $"{a.BlockName} ×({Num(a.ScaleX)}, {Num(a.ScaleY)})", $"{b.BlockName} ×({Num(b.ScaleX)}, {Num(b.ScaleY)})",
                    a.OuterGeometry, b.OuterGeometry));
            }

            // 4-4) 레이어/속성 변경: 배치 관련 키는 제외하고 값이 다른 항목만 모아 한 건으로 보고합니다.
            var change = DescribeAttributeChange(a.LayerName, b.LayerName, a.Attributes, b.Attributes);
            if (change is not null)
            {
                entries.Add(new DiffEntry(DiffKind.AttributesChanged, true, uid, b.BlockName, b.LayerName,
                    change.Value.Before, change.Value.After, a.OuterGeometry, b.OuterGeometry));
            }
        }
    }

    /// <summary>일반(블록이 아닌) 객체를 object_uid 기준으로 비교해 차이를 entries 에 추가합니다.</summary>
    /// <param name="before">이전 일반 객체 목록(object_uid 유일해야 함).</param>
    /// <param name="after">이후 일반 객체 목록.</param>
    /// <param name="entries">차이를 누적할 목록(출력).</param>
    private static void CompareFeatures(
        IReadOnlyCollection<VersionFeatureItem> before, IReadOnlyCollection<VersionFeatureItem> after, List<DiffEntry> entries)
    {
        // 1) 양쪽을 object_uid 로 조회할 수 있게 사전으로 만듭니다.
        var beforeByUid = before.ToDictionary(item => item.ObjectUid);
        var afterByUid = after.ToDictionary(item => item.ObjectUid);

        // 2) 이후에만 있는 객체 = 추가됨.
        foreach (var item in after.Where(item => !beforeByUid.ContainsKey(item.ObjectUid)))
        {
            entries.Add(new DiffEntry(DiffKind.Added, false, item.ObjectUid, FeatureName(item), item.LayerName,
                null, item.FeatureType, null, item.Geometry));
        }

        // 3) 이전에만 있는 객체 = 삭제됨.
        foreach (var item in before.Where(item => !afterByUid.ContainsKey(item.ObjectUid)))
        {
            entries.Add(new DiffEntry(DiffKind.Removed, false, item.ObjectUid, FeatureName(item), item.LayerName,
                item.FeatureType, null, item.Geometry, null));
        }

        // 4) 양쪽에 있는 객체는 도형과 레이어/속성 변경 여부를 판단합니다.
        foreach (var (uid, a) in beforeByUid)
        {
            if (!afterByUid.TryGetValue(uid, out var b))
            {
                continue;
            }

            // 4-1) 도형 변경: 실제 도형 또는 외곽 도형이 허용 오차(1e-6) 밖으로 달라졌으면 형상 변경입니다.
            if (!SameGeometry(a.Geometry, b.Geometry) || !SameGeometry(a.OuterGeometry, b.OuterGeometry))
            {
                entries.Add(new DiffEntry(DiffKind.Reshaped, false, uid, FeatureName(b), b.LayerName,
                    "shape", "shape changed", a.Geometry, b.Geometry));
            }

            // 4-2) 레이어/속성 변경 여부.
            var change = DescribeAttributeChange(a.LayerName, b.LayerName, a.Attributes, b.Attributes);
            if (change is not null)
            {
                entries.Add(new DiffEntry(DiffKind.AttributesChanged, false, uid, FeatureName(b), b.LayerName,
                    change.Value.Before, change.Value.After, a.Geometry, b.Geometry));
            }
        }
    }

    /// <summary>일반 객체의 표시 이름을 정합니다. 블록 이름이 있으면 그것을, 없으면 객체 종류를 씁니다.</summary>
    /// <param name="item">대상 객체.</param>
    /// <returns>표시용 이름.</returns>
    private static string FeatureName(VersionFeatureItem item) =>
        string.IsNullOrWhiteSpace(item.BlockName) ? item.FeatureType : item.BlockName!;

    /// <summary>블록 상태를 "(x, y) 각도°" 형태 문자열로 설명합니다. 삽입점이 없으면 각도만 표시합니다.</summary>
    /// <param name="item">대상 블록.</param>
    /// <returns>추가/삭제 항목의 Before/After 설명 문자열.</returns>
    private static string Describe(VersionBlockItem item) =>
        item.InsertX is { } x && item.InsertY is { } y
            ? $"({Num(x)}, {Num(y)}) {Num(item.RotationDeg)}°"
            : $"{Num(item.RotationDeg)}°";

    /// <summary>숫자를 최대 소수 4자리까지 문화권과 무관한(InvariantCulture) 문자열로 변환합니다.</summary>
    /// <param name="value">변환할 값.</param>
    /// <returns>불필요한 0 이 없는 문자열.</returns>
    private static string Num(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>두 각도의 최소 차이(0~180도)를 구합니다. 0도와 360도의 경계를 넘나드는 경우도 올바르게 처리합니다.</summary>
    /// <param name="a">첫 번째 각도(도).</param>
    /// <param name="b">두 번째 각도(도).</param>
    /// <returns>0 이상 180 이하의 각도 차이(도).</returns>
    public static double AngleDifference(double a, double b)
    {
        var difference = Math.Abs(a - b) % 360.0;
        return difference > 180.0 ? 360.0 - difference : difference;
    }

    /// <summary>두 지오메트리가 같은지 판단합니다. 둘 다 null 이면 같고, 하나만 null 이면 다릅니다.</summary>
    /// <param name="a">첫 번째 도형(null 가능).</param>
    /// <param name="b">두 번째 도형(null 가능).</param>
    /// <returns>좌표가 1e-6 오차 안에서 정확히 일치하면 true.</returns>
    private static bool SameGeometry(Geometry? a, Geometry? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return a.EqualsExact(b, 1e-6);
    }

    /// <summary>
    /// 레이어와 속성의 차이를 "키=값; 키=값" 형태의 변경 전/후 문자열로 만듭니다.
    /// 배치 관련 키(<c>PlacementKeys</c>)는 제외하고 키 이름순(대소문자 무시)으로 정렬해 결과가 항상 일정하게 합니다.
    /// </summary>
    /// <param name="layerBefore">이전 레이어 이름(null 은 빈 문자열과 같게 취급).</param>
    /// <param name="layerAfter">이후 레이어 이름.</param>
    /// <param name="before">이전 속성 사전.</param>
    /// <param name="after">이후 속성 사전.</param>
    /// <returns>차이가 있으면 (이전 설명, 이후 설명), 전혀 없으면 null.</returns>
    private static (string Before, string After)? DescribeAttributeChange(
        string? layerBefore, string? layerAfter,
        IReadOnlyDictionary<string, string?> before, IReadOnlyDictionary<string, string?> after)
    {
        // 1) 레이어 이름이 바뀌었으면 가장 먼저 기록합니다(null 과 빈 문자열은 같은 것으로 봅니다).
        var changes = new List<string>();
        var beforeParts = new List<string>();
        var afterParts = new List<string>();
        if (!string.Equals(layerBefore ?? string.Empty, layerAfter ?? string.Empty, StringComparison.Ordinal))
        {
            beforeParts.Add($"layer={layerBefore}");
            afterParts.Add($"layer={layerAfter}");
            changes.Add("layer");
        }

        // 2) 양쪽 속성 키의 합집합(배치 키 제외)을 이름순으로 훑어 값이 다른 키만 기록합니다. 한쪽에만 있는 키는 값이 null 로 비교됩니다.
        var keys = before.Keys.Concat(after.Keys)
            .Where(key => !PlacementKeys.Contains(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            before.TryGetValue(key, out var valueBefore);
            after.TryGetValue(key, out var valueAfter);
            if (!string.Equals(valueBefore, valueAfter, StringComparison.Ordinal))
            {
                beforeParts.Add($"{key}={valueBefore}");
                afterParts.Add($"{key}={valueAfter}");
            }
        }

        // 3) 달라진 항목이 하나도 없으면 null, 있으면 "; " 로 이어붙인 전/후 문자열을 반환합니다.
        return beforeParts.Count == 0 ? null : (string.Join("; ", beforeParts), string.Join("; ", afterParts));
    }
}
