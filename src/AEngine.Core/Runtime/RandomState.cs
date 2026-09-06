using System.Reflection;
using System.Text.Json;

namespace AEngine.Core.Runtime;

/// <summary>
/// Capture and restore of <see cref="Random"/>'s internal PRNG state, so
/// a saved game continues the exact random sequence it was living —
/// reloading a save cannot re-roll the dice (classic Infocom behavior;
/// save-scumming by copying save files still works, as it should).
/// <para>
/// The runtime exposes no public state surface, so this walks the private
/// implementation object (<c>_impl</c> — <c>CompatSeedImpl</c> for seeded
/// construction, <c>XoshiroImpl</c> for the parameterless one, as of
/// .NET 10) and snapshots every instance field of the chain. Struct-typed
/// fields are written back as whole values: a struct fetched by
/// reflection is a boxed <i>copy</i>, so per-field writes to the box are
/// lost — the leaf values are collected into a fresh box that then
/// replaces the field. Arrays are deep-copied on capture. The layout is
/// pinned by tests; an unsupported layout fails loudly rather than
/// corrupting the sequence silently.
/// </para>
/// </summary>
public static class RandomState
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;

    private static readonly FieldInfo ImplField =
        typeof(Random).GetField("_impl", Flags)
        ?? throw new NotSupportedException(
            "This runtime's Random has no '_impl' field — save/load cannot capture RNG state.");

    /// <summary>
    /// The captured state: leaf field path → value. Paths walk the
    /// implementation chain, struct interiors joined with "." (e.g.
    /// "_prng._inext"). Values are long (signed leaves, sign preserved),
    /// ulong (raw unsigned state words), int[], or ulong[].
    /// </summary>
    public sealed class Snapshot
    {
        internal Dictionary<string, object> Fields { get; } = new(StringComparer.Ordinal);

        /// <summary>Serializable form (path → JSON value), for save files.</summary>
        public Dictionary<string, JsonElement> ToJson() => Fields.ToDictionary(
            kv => kv.Key, kv => World.World.ToJson(kv.Value), StringComparer.Ordinal);

        /// <summary>Rebuild from the serialized form (see <see cref="ToJson"/>).</summary>
        public static Snapshot FromJson(Dictionary<string, JsonElement> fields)
        {
            var snap = new Snapshot();
            foreach (var (path, element) in fields)
                snap.Fields[path] = element.ValueKind switch
                {
                    JsonValueKind.Number => element.TryGetInt64(out var l)
                        ? l
                        : element.GetUInt64(),
                    JsonValueKind.Array when element.EnumerateArray().All(e =>
                        e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out _)) =>
                        element.EnumerateArray().Select(e => e.GetInt64()).ToArray(),
                    JsonValueKind.Array =>
                        element.EnumerateArray().Select(e => e.GetUInt64()).ToArray(),
                    _ => throw new InvalidDataException(
                        $"Random state field '{path}' is not a number or number array."),
                };
            return snap;
        }
    }

    /// <summary>Capture the current state of <paramref name="random"/>.</summary>
    public static Snapshot Capture(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        var snap = new Snapshot();
        var impl = ImplField.GetValue(random)
            ?? throw new NotSupportedException(
                $"A {random.GetType().Name}'s implementation state is not captureable.");
        CaptureInto(impl, "", snap);
        return snap;
    }

    /// <summary>Restore <paramref name="random"/> to a captured state.</summary>
    public static void Restore(Random random, Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(random);
        ArgumentNullException.ThrowIfNull(snapshot);
        var impl = ImplField.GetValue(random)
            ?? throw new NotSupportedException(
                $"A {random.GetType().Name}'s implementation state is not restorable.");
        RestoreInto(impl, "", snapshot.Fields);
    }

    private static void CaptureInto(object container, string prefix, Snapshot snap)
    {
        foreach (var field in OrderedFields(container.GetType()))
        {
            var value = field.GetValue(container);
            var path = prefix + field.Name;
            switch (value)
            {
                case int[] a:
                    snap.Fields[path] = a.ToArray();
                    continue;
                case ulong[] a:
                    snap.Fields[path] = a.ToArray();
                    continue;
                case ulong:
                    snap.Fields[path] = value;
                    continue;
                case int or long or uint or short or ushort or byte or sbyte:
                    snap.Fields[path] = Convert.ToInt64(value);
                    continue;
            }
            // nested struct state (the compat PRNG): recurse into the box —
            // reading through it is fine, only writing needs whole-value care
            if (value is not null && field.FieldType.IsValueType && !field.FieldType.IsPrimitive)
            {
                CaptureInto(value, path + ".", snap);
                continue;
            }
            throw new NotSupportedException(
                $"Cannot capture Random state field '{field.Name}' " +
                $"of type {field.FieldType.Name}.");
        }
    }

    private static void RestoreInto(object container, string prefix, Dictionary<string, object> fields)
    {
        foreach (var field in OrderedFields(container.GetType()))
        {
            var path = prefix + field.Name;
            if (fields.TryGetValue(path, out var value))
            {
                field.SetValue(container, ConvertValue(value, field.FieldType));
                continue;
            }
            if (field.FieldType.IsValueType && !field.FieldType.IsPrimitive)
            {
                // nested struct: restore into a box (a copy), then write the
                // whole value back — per-field writes to a fetched box are lost
                var box = field.GetValue(container)
                    ?? throw new NotSupportedException(
                        $"Random state struct '{path}' has no current value to restore into.");
                RestoreInto(box, path + ".", fields);
                field.SetValue(container, box);
                continue;
            }
            throw new NotSupportedException(
                $"Random state is missing field '{path}' — the save predates " +
                "a runtime internals change.");
        }
    }

    private static IEnumerable<FieldInfo> OrderedFields(Type type) =>
        type.GetFields(Flags).OrderBy(f => f.Name, StringComparer.Ordinal);

    /// <summary>Coerce a captured value to the live field's type.</summary>
    private static object ConvertValue(object value, Type target) =>
        target == typeof(int) ? checked((int)(long)value)
        : target == typeof(long) ? (long)value
        : target == typeof(ulong)
            ? value is ulong u ? u : unchecked((ulong)Convert.ToInt64(value))
        : target == typeof(int[])
            ? value is int[] ints ? ints
                : ((long[])value).Select(v => checked((int)v)).ToArray()
        : target == typeof(ulong[])
            ? value is ulong[] ulongs ? ulongs
                : ((long[])value).Select(v => unchecked((ulong)v)).ToArray()
        : throw new NotSupportedException(
            $"Cannot restore Random state into a {target.Name} field.");
}
