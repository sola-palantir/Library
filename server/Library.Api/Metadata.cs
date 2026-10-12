using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Library.Api;

public static partial class Naming
{
    [GeneratedRegex(@"[ ._-]+S(\d{1,2})E(\d{1,3})", RegexOptions.IgnoreCase)] private static partial Regex EpisodePattern();
    [GeneratedRegex(@"\b(19\d{2}|20\d{2})\b")] private static partial Regex YearPattern();
    public static (Title Title, Edition Edition) Infer(string file)
    {
        var episode = EpisodePattern().Match(file);
        var year = YearPattern().Match(file);
        var cut = Math.Min(episode.Success ? episode.Index : file.Length, year.Success ? year.Index : file.Length);
        var name = file[..cut].Replace('.', ' ').Replace('_', ' ').Trim(' ', '(', '-', '[');
        return (new Title { Name = name.Length == 0 ? file : name, Kind = episode.Success ? "tv" : "movie", Year = year.Success ? int.Parse(year.Value) : null },
            new Edition { Season = episode.Success ? int.Parse(episode.Groups[1].Value) : null, Episode = episode.Success ? int.Parse(episode.Groups[2].Value) : null });
    }
    public static string SafeName(string value)
    {
        var safe = string.Concat(value.Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        if (safe.Length == 0) throw new ArgumentException("A title name is required.");
        Storage.Components(safe); return safe;
    }
    public static string Plex(Title title, Edition edition, Asset asset)
    {
        var name = SafeName(title.Name) + (title.Year is { } year ? $" ({year})" : "");
        var ext = Path.GetExtension(asset.FileName).ToLowerInvariant();
        if (title.Kind == "tv")
        {
            if (edition.Season == null || edition.Episode == null) throw new ArgumentException("TV naming needs season and episode.");
            return $"TV/{name}/Season {edition.Season:00}/{name} - s{edition.Season:00}e{edition.Episode:00}{ext}";
        }
        var editionSuffix = edition.Name == "Original" ? "" : $" {{edition-{SafeName(edition.Name)}}}";
        return $"Movies/{name}{editionSuffix}/{name}{editionSuffix}{ext}";
    }
}
public static class Metadata
{
    public static readonly string[] Fields = ["name", "year", "plot", "genre"];
    public static string? Get(Title t, string field) => field switch { "name" => t.Name, "year" => t.Year?.ToString(), "plot" => t.Plot, "genre" => t.Genre, _ => throw new ArgumentException("Unknown metadata field.") };
    public static Title Apply(Title original, MetadataChange change)
    {
        if (original.Revision != change.Revision) throw new ArgumentException("Metadata changed since preview. Refresh and preview again.");
        var title = JsonSerializer.Deserialize<Title>(JsonSerializer.Serialize(original))!;
        var locks = JsonSerializer.Deserialize<string[]>(original.LockedFields)!;
        foreach (var (field, edit) in change.Fields)
        {
            if (!Fields.Contains(field) || edit.Mode is not ("leave" or "set" or "clear")) throw new ArgumentException("Use a supported field and leave/set/clear mode.");
            if (edit.Mode == "leave") continue;
            if (locks.Contains(field)) throw new ArgumentException($"{field} is manually locked. Unlock in a separate reviewed change first.");
            var value = edit.Mode == "clear" ? null : edit.Value?.Trim();
            switch (field)
            {
                case "name": title.Name = !string.IsNullOrWhiteSpace(value) ? Naming.SafeName(value) : throw new ArgumentException("Title cannot be cleared."); break;
                case "year": title.Year = value == null ? null : int.TryParse(value, out var year) && year >= 1888 && year <= 2200 ? year : throw new ArgumentException("Invalid year."); break;
                case "plot": title.Plot = value; break;
                case "genre": title.Genre = value; break;
            }
        }
        if (change.LockedFields.Any(x => !Fields.Contains(x))) throw new ArgumentException("Unknown lock field.");
        title.LockedFields = JsonSerializer.Serialize(change.LockedFields.Distinct()); title.Revision++;
        return title;
    }
    public static XDocument ReadNfo(string xml)
    {
        if (xml.Length > 2_000_000) throw new ArgumentException("NFO exceeds 2 MB limit.");
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000 };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        if (document.Root?.Name.LocalName is not ("movie" or "tvshow" or "episodedetails")) throw new ArgumentException("Expected a movie, tvshow or episodedetails NFO.");
        return document;
    }
    public static string ExportNfo(Title title, Edition edition, string? original = null)
    {
        var rootName = title.Kind == "movie" ? "movie" : "episodedetails";
        var doc = original == null ? new XDocument(new XElement(rootName)) : ReadNfo(original);
        if (doc.Root!.Name.LocalName != rootName) throw new ArgumentException("NFO type does not match the selected asset.");
        void Set(string name, string? value)
        {
            // Only change the mapped element; all unknown tags and attributes survive.
            var element = doc.Root.Element(name);
            doc.Root.Elements(name).Skip(1).Remove();
            if (value == null) { element?.Remove(); return; }
            if (element == null) doc.Root.Add(new XElement(name, value)); else element.Value = value;
        }
        Set("title", title.Name); Set("year", title.Year?.ToString()); Set("plot", title.Plot); Set("genre", title.Genre);
        if (title.Kind == "tv") { Set("season", edition.Season?.ToString()); Set("episode", edition.Episode?.ToString()); }
        return doc.ToString(SaveOptions.DisableFormatting);
    }
}
public static class Packing
{
    public static List<List<ManifestEntry>> Pack(IEnumerable<ManifestEntry> entries, long capacity, long reserve)
    {
        if (capacity <= 0 || reserve < 1_000_000 || reserve >= capacity) throw new ArgumentException("Reserve must be at least 1 MB and less than disc capacity.");
        var usable = capacity - reserve;
        var bins = new List<List<ManifestEntry>>();
        foreach (var item in entries.OrderByDescending(x => x.Bytes).ThenBy(x => x.Path, StringComparer.Ordinal))
        {
            if (item.Bytes < 0 || item.Bytes > usable) throw new ArgumentException($"Oversized file: {item.Path}. Nothing was split or transcoded.");
            var bin = bins.FirstOrDefault(x => x.Sum(e => e.Bytes) + item.Bytes <= usable);
            if (bin == null) { bin = []; bins.Add(bin); }
            bin.Add(item);
        }
        return bins;
    }
}
