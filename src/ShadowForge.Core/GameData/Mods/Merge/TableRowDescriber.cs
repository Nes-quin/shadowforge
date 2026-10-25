namespace ShadowForge.GameData.Mods.Merge;

/// <summary>
/// Describes rows of a game table for conflict reports. A row is named by what it is where the
/// table is known (an item, an enemy, a character model), and a change is listed as the columns
/// it touches, by header name, as "HP 2250 -> 3000". Columns that hold item ids show the item.
/// </summary>
public sealed class TableRowDescriber
{
    /// <summary>
    /// English names for the Japanese headers of the enemy stat tables.
    /// </summary>
    private static readonly Dictionary<string, string> EnglishHeaders = new()
    {
        ["カテゴリNo."] = "Category", ["サイズ"] = "Size", ["グリッド"] = "Grid", ["名前"] = "Name",
        ["種族"] = "Race", ["レベル"] = "Level", ["攻撃力"] = "Attack", ["魔法攻撃力"] = "Magic attack",
        ["防御力"] = "Defense", ["魔法防御"] = "Magic defense", ["回避率"] = "Evasion",
        ["回復量"] = "Recovery", ["命中率"] = "Accuracy", ["すばやさ"] = "Speed",
        ["無属性耐性"] = "Neutral resist", ["火属性耐性"] = "Fire resist", ["水属性耐性"] = "Water resist",
        ["風属性耐性"] = "Wind resist", ["土属性耐性"] = "Earth resist", ["聖属性耐性"] = "Light resist",
        ["闇属性耐性"] = "Shadow resist", ["眠り耐性"] = "Sleep resist", ["ケロロン耐性"] = "Kelolon resist",
        ["毒耐性"] = "Poison resist", ["しびれ耐性"] = "Paralysis resist", ["パニック耐性"] = "Panic resist",
        ["石化耐性"] = "Petrify resist", ["気絶耐性"] = "Faint resist", ["死亡耐性"] = "Death resist",
        ["スロウ耐性"] = "Slow resist", ["ストップ耐性"] = "Stop resist", ["MONEY"] = "Gold",
        ["MEDAL"] = "Medals", ["アイテム1"] = "Drop item 1", ["アイテム2"] = "Drop item 2",
        ["アイテム3"] = "Drop item 3", ["アイテム4"] = "Drop item 4", ["ドロップ率"] = "Drop rate",
        ["移動タイプ"] = "Movement type",
    };

    private readonly RowIdentity _identity;
    private readonly GameNames _names;
    private readonly List<string> _headers;
    private readonly int _headerLines;

    private enum RowIdentity { Position, Item, ZakoEnemy, BossEnemy, ModelColumn }

    public TableRowDescriber(string fileName, IReadOnlyList<string> stockLines, GameNames names)
    {
        _names = names;
        string name = Path.GetFileName(fileName).ToLowerInvariant();
        _identity = name switch
        {
            _ when name.StartsWith("ittb") || name.StartsWith("itwd") => RowIdentity.Item,
            _ when name.StartsWith("enemy_zako") || name.StartsWith("pre_enemy_zako") => RowIdentity.ZakoEnemy,
            _ when name.StartsWith("enemy_boss") => RowIdentity.BossEnemy,
            _ when name.StartsWith("id_chr_model") || name.StartsWith("namelist_chr") => RowIdentity.ModelColumn,
            _ => RowIdentity.Position,
        };

        bool hasHeader = _identity != RowIdentity.Item && LooksLikeHeader(stockLines);
        _headerLines = hasHeader ? 1 : 0;
        _headers = hasHeader ? CsvFields.Split(stockLines[0]) : [];
    }

    /// <summary>
    /// True for tables: .u16 and .csv files.
    /// </summary>
    public static bool IsTable(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() is ".u16" or ".csv";

    /// <summary>
    /// What the row at this 0-based line is: "item 241 Elixir", "enemy em045 Bloodsucker",
    /// "model bs17 Sai", or "row 12 (pointID 3)" for a table this does not know.
    /// </summary>
    public string RowLabel(int lineIndex, string line)
    {
        var fields = CsvFields.Split(line);
        string first = fields[0].Trim();
        switch (_identity)
        {
            case RowIdentity.Item:
                return _names.ItemLabel(lineIndex + 1);
            case RowIdentity.ZakoEnemy or RowIdentity.BossEnemy when int.TryParse(first, out int no):
                string model = GameNames.EnemyModelId(_identity == RowIdentity.BossEnemy ? 1 : 0, no);
                string fallback = fields.Count > 4 ? fields[4].Trim() : "";
                return _names.Model(model) is { } english ? $"enemy {model} {english}" : $"enemy {model} {fallback}".TrimEnd();
            case RowIdentity.ModelColumn when fields.Count > 1 && fields[1].Trim().Length > 0:
                return "model " + _names.ModelLabel(fields[1].Trim());
        }

        int row = lineIndex - _headerLines + 1;
        if (first.Length == 0) return $"row {row}";
        return _headers.Count > 0 ? $"row {row} ({_headers[0].Trim()} {first})" : $"row {row} ({first})";
    }

    /// <summary>
    /// The columns a mod's row changes: "HP 2250 -> 3000, Drop item 1 50 Medicine -> 241 Elixir".
    /// </summary>
    public string FieldChanges(string stockLine, string modLine)
    {
        var before = CsvFields.Split(stockLine);
        var after = CsvFields.Split(modLine);
        var changes = new List<string>();
        for (int i = 0; i < Math.Max(before.Count, after.Count); i++)
        {
            string from = i < before.Count ? before[i].Trim() : "";
            string to = i < after.Count ? after[i].Trim() : "";
            if (from != to)
                changes.Add($"{ColumnName(i)} {Value(i, from)} -> {Value(i, to)}");
        }
        return changes.Count == 0 ? "only whitespace differs" : string.Join(", ", changes);
    }

    /// <summary>
    /// A new or removed row, by its label and leading fields.
    /// </summary>
    public string Summary(int lineIndex, string line) =>
        $"{RowLabel(lineIndex, line)}: {ConflictText.Join([line])}";

    private string ColumnName(int column)
    {
        if (column >= _headers.Count || _headers[column].Trim().Length == 0) return $"column {column}";
        string header = _headers[column].Trim();
        string name = EnglishHeaders.GetValueOrDefault(header, header);
        return _headers.Count(h => h.Trim() == header) > 1 ? $"{name} (column {column})" : name;
    }

    /// <summary>
    /// A cell with the name of what it points at: an item id in an item column, a tagged item
    /// id (ITM185) or a model id (em045) anywhere.
    /// </summary>
    private string Value(int column, string value)
    {
        if (value.Length == 0) return "(blank)";
        bool itemColumn = column < _headers.Count
            && (_headers[column].Contains("アイテム") || _headers[column].Contains("item", StringComparison.OrdinalIgnoreCase));
        string? name = itemColumn && int.TryParse(value, out int id) ? _names.Item(id)
            : value.StartsWith("ITM", StringComparison.OrdinalIgnoreCase) && int.TryParse(value[3..], out int tagged) ? _names.Item(tagged)
            : _names.Model(value);
        return name is null ? value : $"{value} {name}";
    }

    /// <summary>
    /// A first line with no number in any field followed by a line with one is a header row.
    /// </summary>
    private static bool LooksLikeHeader(IReadOnlyList<string> lines)
    {
        if (lines.Count < 2) return false;
        static bool AnyNumber(string line) => CsvFields.Split(line).Any(f => double.TryParse(f.Trim(), out _));
        return !AnyNumber(lines[0]) && AnyNumber(lines[1]);
    }
}
