namespace KidGuard.Core;

/// <summary>Дни недели: ключи для хранения, русские названия, разбор из команд бота.</summary>
public static class Days
{
    public static IReadOnlyList<DayOfWeek> Week { get; } =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    ];

    public static IReadOnlyList<DayOfWeek> Weekdays { get; } =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday,
    ];

    public static IReadOnlyList<DayOfWeek> Weekend { get; } = [DayOfWeek.Saturday, DayOfWeek.Sunday];

    public static string Key(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "mon",
        DayOfWeek.Tuesday => "tue",
        DayOfWeek.Wednesday => "wed",
        DayOfWeek.Thursday => "thu",
        DayOfWeek.Friday => "fri",
        DayOfWeek.Saturday => "sat",
        DayOfWeek.Sunday => "sun",
        _ => throw new ArgumentOutOfRangeException(nameof(day)),
    };

    public static string Short(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "Пн",
        DayOfWeek.Tuesday => "Вт",
        DayOfWeek.Wednesday => "Ср",
        DayOfWeek.Thursday => "Чт",
        DayOfWeek.Friday => "Пт",
        DayOfWeek.Saturday => "Сб",
        DayOfWeek.Sunday => "Вс",
        _ => throw new ArgumentOutOfRangeException(nameof(day)),
    };

    /// <summary>Порядковый номер с понедельника (0) по воскресенье (6).</summary>
    public static int Index(DayOfWeek day) => ((int)day + 6) % 7;

    public static List<DayOfWeek> Sort(IEnumerable<DayOfWeek> days) => days.Distinct().OrderBy(Index).ToList();

    public static string Describe(IEnumerable<DayOfWeek> days)
    {
        var list = Sort(days);
        if (list.Count == 7) return "все дни";
        if (list.SequenceEqual(Weekdays)) return "будни";
        if (list.SequenceEqual(Weekend)) return "выходные";
        return string.Join(", ", list.Select(Short));
    }

    /// <summary>
    /// Разбор: «будни», «выходные», «все», «пн», «пн-пт», «сб,вс».
    /// </summary>
    public static IReadOnlyList<DayOfWeek>? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var result = new List<DayOfWeek>();
        foreach (var raw in text.ToLowerInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw)
            {
                case "будни":
                    result.AddRange(Weekdays);
                    continue;
                case "выходные":
                    result.AddRange(Weekend);
                    continue;
                case "все" or "всё" or "ежедневно" or "каждый":
                    result.AddRange(Week);
                    continue;
            }

            var range = raw.Split(new[] { '-', '–' }, StringSplitOptions.TrimEntries);
            if (range.Length == 1 && ParseOne(range[0]) is { } single)
            {
                result.Add(single);
            }
            else if (range.Length == 2 && ParseOne(range[0]) is { } from && ParseOne(range[1]) is { } to)
            {
                for (var i = Index(from); ; i = (i + 1) % 7)
                {
                    result.Add(Week[i]);
                    if (i == Index(to)) break;
                }
            }
            else
            {
                return null;
            }
        }
        return result.Count == 0 ? null : Sort(result);
    }

    static DayOfWeek? ParseOne(string text) => text switch
    {
        "пн" or "понедельник" => DayOfWeek.Monday,
        "вт" or "вторник" => DayOfWeek.Tuesday,
        "ср" or "среда" => DayOfWeek.Wednesday,
        "чт" or "четверг" => DayOfWeek.Thursday,
        "пт" or "пятница" => DayOfWeek.Friday,
        "сб" or "суббота" => DayOfWeek.Saturday,
        "вс" or "воскресенье" => DayOfWeek.Sunday,
        _ => null,
    };
}
