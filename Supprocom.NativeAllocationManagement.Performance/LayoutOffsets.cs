using System.Globalization;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement.Performance;

// Where each layout copy's measured loop sits, from a JIT listing
// (DOTNET_JitDisasm=Run, DOTNET_JitStdOutFile, DOTNET_JitDisasmWithCodeBytes=1).
// The loop is the widest backward jump; its head is that jump's target.
internal readonly record struct LoopPlacement(
    int Copy,
    string Tier,
    int HeadOffset,
    int JumpOffset,
    int JumpLength)
{
    internal int HeadMod32 => HeadOffset % 32;

    // A jump that crosses or ends on a 32-byte boundary is slow on affected CPUs.
    internal bool JumpHitsBoundary =>
        (JumpOffset % 32) + JumpLength >= 32;
}

internal static class LayoutOffsets
{
    private const string ListingStart = "; Assembly listing for method ";
    private const string PadMarker = "LayoutPad";
    private const string OffsetMarker = ";; offset=0x";

    // The last Tier1 listing of each copy.
    internal static LoopPlacement[] Parse(string disassembly)
    {
        Dictionary<int, LoopPlacement> last = [];
        string[] lines = disassembly.Split('\n');
        int start = -1;
        for (int i = 0; i <= lines.Length; i++)
        {
            bool header = i < lines.Length
                && lines[i].StartsWith(ListingStart, StringComparison.Ordinal);
            if (!header && i != lines.Length)
            {
                continue;
            }

            if (start >= 0 && TryParseListing(lines, start, i, out LoopPlacement placement)
                && string.Equals(placement.Tier, "Tier1", StringComparison.Ordinal))
            {
                last[placement.Copy] = placement;
            }

            start = header ? i : -1;
        }

        return [.. last.Values.OrderBy(p => p.Copy)];
    }

    private static bool TryParseListing(string[] lines, int start, int end, out LoopPlacement placement)
    {
        placement = default;
        string head = lines[start];
        int pad = head.IndexOf(PadMarker, StringComparison.Ordinal);
        int open = head.LastIndexOf('(');
        if (pad < 0 || open < 0 || !char.IsAsciiDigit(head[pad + PadMarker.Length]))
        {
            return false;
        }

        int copy = head[pad + PadMarker.Length] - '0';
        string tier = head[(open + 1)..].TrimEnd().TrimEnd(')');
        Dictionary<string, int> labels = new(StringComparer.Ordinal);
        List<(int From, int Length, string Target)> jumps = [];
        int offset = 0;
        foreach (ref readonly string raw in lines.AsSpan(start + 1, end - start - 1))
        {
            string line = raw.Trim();
            if (line.StartsWith("G_M", StringComparison.Ordinal))
            {
                int colon = line.IndexOf(':', StringComparison.Ordinal);
                int marker = line.IndexOf(OffsetMarker, StringComparison.Ordinal);
                if (colon > 0 && marker > 0)
                {
                    offset = int.Parse(line[(marker + OffsetMarker.Length)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    labels[line[..colon]] = offset;
                }

                continue;
            }

            int space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space < 2 || space % 2 != 0)
            {
                continue;
            }

            if (!IsHex(line.AsSpan(0, space)))
            {
                continue;
            }

            int length = space / 2;
            string text = line[space..].Trim();
            if (text.StartsWith('j'))
            {
                int target = text.IndexOf("G_M", StringComparison.Ordinal);
                if (target > 0)
                {
                    jumps.Add((offset, length, text[target..].Split(' ', '\t', ';')[0]));
                }
            }

            offset += length;
        }

        int bestSpan = -1;
        foreach (ref readonly (int From, int Length, string Target) jump in CollectionsMarshal.AsSpan(jumps))
        {
            if (labels.TryGetValue(jump.Target, out int targetOffset) && targetOffset <= jump.From && jump.From - targetOffset > bestSpan)
            {
                bestSpan = jump.From - targetOffset;
                placement = new LoopPlacement(copy, tier, targetOffset, jump.From, jump.Length);
            }
        }

        return bestSpan >= 0;
    }

    private static bool IsHex(ReadOnlySpan<char> text)
    {
        foreach (ref readonly char c in text)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
