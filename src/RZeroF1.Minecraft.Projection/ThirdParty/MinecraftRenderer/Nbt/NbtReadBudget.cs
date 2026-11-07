namespace MinecraftRenderer.Nbt;

internal sealed record NbtReadLimits(int MaxDepth = 64, int MaxNodes = 4_000_000,
    long MaxAllocatedBytes = 512L * 1024 * 1024);

// Bounds retained payloads, including records subsequently discarded by overlap or
// coordinate validation. The binary reader separately bounds allocations before use.
internal sealed class NbtPayloadBudget(int maxRecords, long maxBytes, long maxRecordBytes)
{
    private int _records;
    private long _bytes;

    internal long Include(NbtTag tag, CancellationToken cancellationToken)
    {
        if (++_records > maxRecords)
            throw new InvalidDataException("BlockEntityCountBudgetExceeded");
        var bytes = Measure(tag, cancellationToken, maxRecordBytes);
        _bytes = checked(_bytes + bytes);
        if (_bytes > maxBytes)
            throw new InvalidDataException("BlockEntityBytesBudgetExceeded");
        return bytes;
    }

    internal static long Measure(NbtTag tag, CancellationToken cancellationToken, long maximum = long.MaxValue)
    {
        long bytes = 0;
        Visit(tag, 0);
        return bytes;

        void Charge(long count)
        {
            bytes = checked(bytes + count);
            if (bytes > maximum) throw new InvalidDataException("BlockEntityRecordBudgetExceeded");
        }
        void Visit(NbtTag value, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth > 64) throw new InvalidDataException("BlockEntityDepthBudgetExceeded");
            Charge(32);
            switch (value)
            {
                case NbtCompound compound:
                    Charge(compound.Count * 32L);
                    foreach (var (key, child) in compound)
                    {
                        Charge(24 + key.Length * 2L);
                        Visit(child, depth + 1);
                    }
                    break;
                case NbtList list:
                    Charge(list.Count * 8L);
                    foreach (var child in list) Visit(child, depth + 1);
                    break;
                case NbtString text: Charge(24 + text.Value.Length * 2L); break;
                case NbtByteArray array: Charge(24 + array.Values.LongLength); break;
                case NbtIntArray array: Charge(24 + array.Values.LongLength * 4); break;
                case NbtLongArray array: Charge(24 + array.Values.LongLength * 8); break;
            }
        }
    }
}
