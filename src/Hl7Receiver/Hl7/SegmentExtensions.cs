using Efferent.HL7.V2;

namespace Hl7Receiver.Hl7;

/// <summary>A field/component position within a segment, using HL7 numbering (OBR-3.1 => Field 3, Component 1).</summary>
public readonly record struct FieldRef(int Field, int Component = 0)
{
    public override string ToString() => Component == 0 ? $"{Field}" : $"{Field}.{Component}";
}

/// <summary>
/// Null-safe, HL7-numbered access on top of the library's throw-on-missing accessors.
/// Returns null for absent or empty values. For repeating fields the first repetition is used
/// (e.g. the first patient identifier in PID-3).
/// </summary>
public static class SegmentExtensions
{
    public static string? Get(this Segment segment, FieldRef at) => segment.Get(at.Field, at.Component);

    /// <param name="field">1-based HL7 field number.</param>
    /// <param name="component">1-based component number, or 0 for the whole field.</param>
    public static string? Get(this Segment segment, int field, int component = 0)
    {
        if (field < 1 || segment.GetAllFields().Count < field)
        {
            return null;
        }

        var f = segment.Fields(field);
        if (f.HasRepetitions)
        {
            var repetitions = f.Repetitions();
            if (repetitions.Count == 0)
            {
                return null;
            }
            f = repetitions[0];
        }

        string? value;
        if (component == 0)
        {
            value = f.Value; // whole field, escape sequences decoded, components still joined by '^'
        }
        else if (f.IsComponentized)
        {
            value = f.Components().Count >= component ? f.Components(component).Value : null;
        }
        else
        {
            value = component == 1 ? f.Value : null;
        }

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static int CountOf(this Message message, string segmentName) => message.Segments(segmentName).Count;
}
