using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KidGuard.Core;

/// <summary>
/// Интервал времени суток в минутах от полуночи, конец не включается: <c>[Start, End)</c>.
/// <c>End</c> может быть 1440 («24:00»). В JSON хранится строкой <c>"16:00-20:00"</c>.
/// </summary>
[JsonConverter(typeof(TimeRangeJsonConverter))]
public readonly record struct TimeRange(int StartMinute, int EndMinute)
{
    public const int MinutesPerDay = 24 * 60;

    public bool Contains(int minute) => minute >= StartMinute && minute < EndMinute;

    public override string ToString() => $"{Format(StartMinute)}-{Format(EndMinute)}";

    /// <summary>Вид для сообщений бота: <c>16:00–20:00</c>.</summary>
    public string ToDisplay() => $"{Format(StartMinute)}–{Format(EndMinute)}";

    public static string Format(int minute) =>
        string.Create(CultureInfo.InvariantCulture, $"{minute / 60:00}:{minute % 60:00}");

    public static bool TryParse(string? text, out TimeRange range)
    {
        range = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split(new[] { '-', '–', '—' });
        if (parts.Length != 2) return false;
        if (!TryParseTime(parts[0], out var start) || !TryParseTime(parts[1], out var end)) return false;
        if (start >= end || start >= MinutesPerDay) return false;
        range = new TimeRange(start, end);
        return true;
    }

    static bool TryParseTime(string text, out int minutes)
    {
        minutes = 0;
        var parts = text.Trim().Split(':');
        if (parts.Length != 2 || parts[1].Length != 2 || parts[0].Length is < 1 or > 2) return false;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var h)) return false;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m)) return false;
        if (h > 24 || m > 59 || (h == 24 && m != 0)) return false;
        minutes = h * 60 + m;
        return true;
    }

    /// <summary>
    /// Разбор списка интервалов «10:00-13:00,15:00-21:00». Возвращает null и текст ошибки,
    /// если формат неверен, интервалов больше трёх или они пересекаются.
    /// </summary>
    public static IReadOnlyList<TimeRange>? ParseList(string? text, out string? error)
    {
        error = null;
        var result = new List<TimeRange>();
        foreach (var item in (text ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryParse(item, out var range))
            {
                error = $"Не понял интервал «{item}». Пример: 16:00-20:00";
                return null;
            }
            result.Add(range);
        }
        error = Validate(result);
        return error is null ? Normalize(result) : null;
    }

    /// <summary>Проверка набора интервалов одного дня: не больше трёх, без пересечений.</summary>
    public static string? Validate(IReadOnlyCollection<TimeRange> ranges)
    {
        if (ranges.Count > 3) return "Не больше трёх интервалов в день.";
        var sorted = Normalize(ranges);
        for (var i = 1; i < sorted.Count; i++)
        {
            if (sorted[i].StartMinute < sorted[i - 1].EndMinute)
                return $"Интервалы {sorted[i - 1].ToDisplay()} и {sorted[i].ToDisplay()} пересекаются.";
        }
        return null;
    }

    public static List<TimeRange> Normalize(IEnumerable<TimeRange> ranges) =>
        ranges.OrderBy(r => r.StartMinute).ThenBy(r => r.EndMinute).ToList();
}

public sealed class TimeRangeJsonConverter : JsonConverter<TimeRange>
{
    public override TimeRange Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        return TimeRange.TryParse(text, out var range)
            ? range
            : throw new JsonException($"Invalid time range '{text}'");
    }

    public override void Write(Utf8JsonWriter writer, TimeRange value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
