using System.Xml.Linq;

namespace MavNet.CodeGen;

internal sealed record FieldSpec(
    string Name,
    MavType Type,
    string? EnumName,
    string Description,
    bool IsExtension);

internal sealed record MessageSpec(
    int Id,
    string Name,
    string Description,
    IReadOnlyList<FieldSpec> Fields)            // wire order (size-desc for non-extension, then extensions in decl order)
{
    public IEnumerable<FieldSpec> NonExtensionFields => Fields.Where(f => !f.IsExtension);

    /// <summary>Untruncated wire length — all fields including extensions. Decoders zero-fill up to this.</summary>
    public int MaxPayloadLength => Fields.Sum(f => f.Type.WireSize);

    /// <summary>Truncation floor — extension-field bytes may be stripped on the wire down to this length.
    /// For pre-v2 messages (no extensions), MinPayloadLength == MaxPayloadLength.</summary>
    public int MinPayloadLength => Fields.Where(f => !f.IsExtension).Sum(f => f.Type.WireSize);

    public bool HasExtensions => Fields.Any(f => f.IsExtension);
}

/// <summary>Param descriptor for a MAV_CMD entry. Index is 1..7 matching the COMMAND_LONG param slots.</summary>
internal sealed record CommandParamSpec(int Index, string Label, string? Units, string? EnumName, string Description);

internal sealed record EnumEntrySpec(
    long Value,
    string Name,
    string Description,
    IReadOnlyList<CommandParamSpec> Params);   // populated only for MAV_CMD entries; empty otherwise

internal sealed record EnumSpec(string Name, string Description, bool IsBitmask, IReadOnlyList<EnumEntrySpec> Entries);

internal sealed record Spec(IReadOnlyList<MessageSpec> Messages, IReadOnlyList<EnumSpec> Enums);

internal static class XmlSpecParser
{
    /// <summary>Recursively loads a MAVLink XML dialect, following &lt;include&gt; directives.
    /// Returns merged messages + enums. Later definitions don't override earlier ones (first wins).</summary>
    public static Spec Load(string rootPath)
    {
        var messages = new Dictionary<int, MessageSpec>();
        var enums = new Dictionary<string, EnumSpec>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        LoadRecursive(Path.GetFullPath(rootPath), messages, enums, visited);

        return new Spec(
            Messages: messages.Values.OrderBy(m => m.Id).ToArray(),
            Enums: enums.Values.OrderBy(e => e.Name, StringComparer.Ordinal).ToArray());
    }

    private static void LoadRecursive(string path, Dictionary<int, MessageSpec> messages,
        Dictionary<string, EnumSpec> enums, HashSet<string> visited)
    {
        if (!visited.Add(path)) return;
        var doc = XDocument.Load(path);
        var root = doc.Root ?? throw new InvalidDataException($"{path}: no root element");
        var dir = Path.GetDirectoryName(path)!;

        foreach (var inc in root.Elements("include"))
        {
            var incPath = Path.GetFullPath(Path.Combine(dir, inc.Value.Trim()));
            LoadRecursive(incPath, messages, enums, visited);
        }

        foreach (var enumEl in root.Element("enums")?.Elements("enum") ?? Enumerable.Empty<XElement>())
        {
            var name = (string)enumEl.Attribute("name")!;
            if (enums.ContainsKey(name)) continue;
            var bitmask = string.Equals((string?)enumEl.Attribute("bitmask"), "true", StringComparison.OrdinalIgnoreCase);
            var entries = new List<EnumEntrySpec>();
            foreach (var entry in enumEl.Elements("entry"))
            {
                var entryName = (string)entry.Attribute("name")!;
                var rawValue = (string?)entry.Attribute("value");
                // Some enum entries omit value (sequential); we skip them for simplicity — we only
                // generate constants we can resolve. Real MAVLink entries always carry value.
                if (!long.TryParse(rawValue, out var value)) continue;
                var desc = entry.Element("description")?.Value?.Trim() ?? "";

                var paramList = new List<CommandParamSpec>();
                foreach (var p in entry.Elements("param"))
                {
                    if (!int.TryParse((string?)p.Attribute("index"), out var idx)) continue;
                    var label = (string?)p.Attribute("label") ?? $"Param{idx}";
                    var units = (string?)p.Attribute("units");
                    var enumRef = (string?)p.Attribute("enum");
                    var pDesc = p.Value?.Trim() ?? "";
                    // MAVLink convention: param description "Empty" / "Reserved" means unused.
                    paramList.Add(new CommandParamSpec(idx, label, units, enumRef, pDesc));
                }
                entries.Add(new EnumEntrySpec(value, entryName, desc, paramList));
            }
            enums[name] = new EnumSpec(
                Name: name,
                Description: enumEl.Element("description")?.Value?.Trim() ?? "",
                IsBitmask: bitmask,
                Entries: entries);
        }

        foreach (var msgEl in root.Element("messages")?.Elements("message") ?? Enumerable.Empty<XElement>())
        {
            var id = int.Parse((string)msgEl.Attribute("id")!);
            if (messages.ContainsKey(id)) continue;
            var name = (string)msgEl.Attribute("name")!;
            var desc = msgEl.Element("description")?.Value?.Trim() ?? "";

            // Walk children in document order so we can mark fields after <extensions/> as IsExtension.
            var nonExt = new List<FieldSpec>();
            var ext = new List<FieldSpec>();
            var seenExt = false;
            foreach (var child in msgEl.Elements())
            {
                if (child.Name.LocalName == "extensions") { seenExt = true; continue; }
                if (child.Name.LocalName != "field") continue;
                var f = new FieldSpec(
                    Name: (string)child.Attribute("name")!,
                    Type: MavType.Parse((string)child.Attribute("type")!),
                    EnumName: (string?)child.Attribute("enum"),
                    Description: child.Value?.Trim() ?? "",
                    IsExtension: seenExt);
                (seenExt ? ext : nonExt).Add(f);
            }

            // Wire order: non-extension fields sorted by descending element size (stable),
            // followed by extension fields in declaration order.
            var ordered = nonExt
                .Select((f, idx) => (f, idx))
                .OrderByDescending(t => t.f.Type.ElementSize)
                .ThenBy(t => t.idx)
                .Select(t => t.f)
                .Concat(ext)
                .ToArray();

            messages[id] = new MessageSpec(id, name, desc, ordered);
        }
    }
}
