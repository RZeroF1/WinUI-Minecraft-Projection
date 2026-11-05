namespace RZeroF1.Minecraft.Projection;

/// <summary>
/// Precomputed coordinate and material counts for fast slice queries.
/// The index is rebuilt only when a projection document changes.
/// </summary>
internal sealed class ProjectionSliceIndex
{
    private readonly ProjectionDocument _document;
    private readonly AxisIndex[] _axes;
    private readonly int[] _totalStateCounts;
    private readonly IReadOnlyDictionary<ProjectionPosition, ProjectionBlock> _blockMap;
    private readonly IReadOnlySet<ProjectionPosition> _opaqueFullCubes;

    public ProjectionSliceIndex(ProjectionDocument document)
    {
        _document = document;
        _blockMap = document.Blocks.ToDictionary(static block => block.Position);
        _opaqueFullCubes = document.Blocks
            .Where(block => ProjectionModelBaker.IsOpaqueFullCube(document.Palette[block.StateIndex].Name))
            .Select(static block => block.Position)
            .ToHashSet();
        _totalStateCounts = new int[document.Palette.Count];
        foreach (var block in document.Blocks)
            _totalStateCounts[block.StateIndex]++;

        _axes =
        [
            BuildAxis(0),
            BuildAxis(1),
            BuildAxis(2)
        ];
    }

    public int VisibleBlockCount(int axis, int minimum, int maximum)
    {
        if (axis < 0)
            return _document.Blocks.Count;

        var axisIndex = GetAxis(axis);
        var start = Math.Max(minimum, axisIndex.Minimum);
        var end = Math.Min(maximum, axisIndex.Maximum);
        if (start > end)
            return 0;

        var rowStart = start - axisIndex.Minimum;
        var rowEnd = end - axisIndex.Minimum + 1;
        var total = 0;
        for (var state = 0; state < axisIndex.StateCount; state++)
            total += axisIndex.PrefixCounts[rowEnd, state] - axisIndex.PrefixCounts[rowStart, state];
        return total;
    }

    public IReadOnlyList<ProjectionMaterialGroup> GroupMaterials(
        int axis = -1,
        int minimum = int.MinValue,
        int maximum = int.MaxValue)
    {
        var counts = GetStateCounts(axis, minimum, maximum);
        return counts
            .Select((count, stateIndex) => (count, stateIndex))
            .Where(static item => item.count > 0)
            .GroupBy(
                item => _document.Palette[item.stateIndex].Name,
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var variants = group
                    .Select(item =>
                    {
                        var state = _document.Palette[item.stateIndex];
                        return new ProjectionMaterialVariant(
                            state.Key,
                            state.Name,
                            state.Properties,
                            item.count);
                    })
                    .OrderByDescending(static item => item.Count)
                    .ThenBy(static item => item.StateKey, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return new ProjectionMaterialGroup(
                    group.Key,
                    variants.Sum(static item => item.Count),
                    variants);
            })
            .OrderByDescending(static item => item.Count)
            .ThenBy(static item => item.BlockName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<ProjectionBlock> BlocksAt(int axis, int coordinate)
    {
        var axisIndex = GetAxis(axis);
        if (coordinate < axisIndex.Minimum || coordinate > axisIndex.Maximum)
            return [];

        var blockIndexes = axisIndex.BlockIndexes[coordinate - axisIndex.Minimum];
        var blocks = new ProjectionBlock[blockIndexes.Length];
        for (var index = 0; index < blockIndexes.Length; index++)
            blocks[index] = _document.Blocks[blockIndexes[index]];
        return blocks;
    }

    public bool ContainsBlock(ProjectionPosition position) =>
        _blockMap.ContainsKey(position);

    public IReadOnlyDictionary<ProjectionPosition, ProjectionBlock> BlockMap => _blockMap;

    public bool IsOpaqueFullCube(ProjectionPosition position) =>
        _opaqueFullCubes.Contains(position);

    private int[] GetStateCounts(int axis, int minimum, int maximum)
    {
        if (axis < 0)
            return (int[])_totalStateCounts.Clone();

        var axisIndex = GetAxis(axis);
        var start = Math.Max(minimum, axisIndex.Minimum);
        var end = Math.Min(maximum, axisIndex.Maximum);
        if (start > end)
            return new int[axisIndex.StateCount];

        var rowStart = start - axisIndex.Minimum;
        var rowEnd = end - axisIndex.Minimum + 1;
        var counts = new int[axisIndex.StateCount];
        for (var state = 0; state < axisIndex.StateCount; state++)
            counts[state] = axisIndex.PrefixCounts[rowEnd, state] - axisIndex.PrefixCounts[rowStart, state];
        return counts;
    }

    private AxisIndex BuildAxis(int axis)
    {
        var minimum = _document.Min.GetAxis(axis);
        var maximum = _document.Max.GetAxis(axis);
        var coordinateCount = checked(maximum - minimum + 1);
        var blockIndexes = new int[coordinateCount][];
        var coordinateSizes = new int[coordinateCount];

        foreach (var block in _document.Blocks)
            coordinateSizes[block.Position.GetAxis(axis) - minimum]++;

        for (var coordinate = 0; coordinate < coordinateCount; coordinate++)
            blockIndexes[coordinate] = new int[coordinateSizes[coordinate]];

        var cursors = new int[coordinateCount];
        for (var blockIndex = 0; blockIndex < _document.Blocks.Count; blockIndex++)
        {
            var coordinate = _document.Blocks[blockIndex].Position.GetAxis(axis) - minimum;
            blockIndexes[coordinate][cursors[coordinate]++] = blockIndex;
        }

        var prefixCounts = new PrefixCountTable(coordinateCount, _document.Palette.Count);
        for (var coordinate = 0; coordinate < coordinateCount; coordinate++)
        {
            for (var state = 0; state < _document.Palette.Count; state++)
                prefixCounts[coordinate + 1, state] = prefixCounts[coordinate, state];

            foreach (var blockIndex in blockIndexes[coordinate])
            {
                var stateIndex = _document.Blocks[blockIndex].StateIndex;
                prefixCounts[coordinate + 1, stateIndex]++;
            }
        }

        return new AxisIndex(minimum, maximum, _document.Palette.Count, blockIndexes, prefixCounts);
    }

    private AxisIndex GetAxis(int axis) =>
        axis is >= 0 and <= 2
            ? _axes[axis]
            : throw new ArgumentOutOfRangeException(nameof(axis));

    private sealed record AxisIndex(
        int Minimum,
        int Maximum,
        int StateCount,
        int[][] BlockIndexes,
        PrefixCountTable PrefixCounts);

    private sealed class PrefixCountTable(int coordinateCount, int stateCount)
    {
        private readonly int[] _values = new int[checked((coordinateCount + 1) * stateCount)];

        public int this[int coordinate, int state]
        {
            get => _values[checked(coordinate * stateCount + state)];
            set => _values[checked(coordinate * stateCount + state)] = value;
        }
    }
}
