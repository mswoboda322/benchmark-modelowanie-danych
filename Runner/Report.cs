// Wspólne elementy raportu: tabele konsolowe (Spectre.Console) i pliki CSV dla polskiego Excela.
using System.Text;
using Spectre.Console;

namespace Runner;

/// <summary>Kolumna tabeli przestawnej: wartość dla wiersza i języka.</summary>
/// <param name="HigherIsBetter">null – bez kolorowania najlepszego/najgorszego.</param>
internal sealed record PivotMetric<T>(string Title, Func<T, double> Value, bool? HigherIsBetter, string Format);

internal static class Report
{
    private static readonly System.Globalization.CultureInfo Pl = Program.Pl;

    public static string Fmt(double v, string format = "N1") => double.IsFinite(v) ? v.ToString(format, Pl) : "—";

    /// <summary>
    /// Tabela przestawna: wiersze = konfiguracje, kolumny = języki. Najlepsza wartość w wierszu na zielono,
    /// najgorsza na czerwono; ⚠ przy niezgodnym wyniku kontrolnym.
    /// </summary>
    public static Table Pivot<T>(string title, IEnumerable<IGrouping<string, T>> rows, Language[] languages,
        Func<T, string> languageOf, PivotMetric<T> metric, Func<T, bool>? mismatch = null)
    {
        var table = new Table().RoundedBorder().Title($"[bold]{Markup.Escape(title)}[/] – {Markup.Escape(metric.Title)}")
            .AddColumn("Konfiguracja");
        foreach (var l in languages)
            table.AddColumn(new TableColumn($"[{l.Color.ToMarkup()}]{Markup.Escape(l.Name)}[/]").RightAligned());

        foreach (var row in rows)
        {
            var cells = languages.Select(l => row.FirstOrDefault(x => languageOf(x) == l.Name)).ToArray();
            double[] values = cells.Select(c => c is null ? double.NaN : metric.Value(c)).ToArray();
            var finite = values.Where(double.IsFinite).ToArray();
            double best = double.NaN, worst = double.NaN;
            if (metric.HigherIsBetter is { } higher && finite.Length > 1)
            {
                best = higher ? finite.Max() : finite.Min();
                worst = higher ? finite.Min() : finite.Max();
            }

            var markup = new List<string> { Markup.Escape(row.Key) };
            for (int i = 0; i < languages.Length; i++)
            {
                string text = Fmt(values[i], metric.Format);
                if (cells[i] is { } c && mismatch?.Invoke(c) == true)
                    text = "[yellow]⚠[/] " + text;
                string color = values[i] == best ? "green" : values[i] == worst ? "red" : "default";
                markup.Add($"[{color}]{text}[/]");
            }
            table.AddRow(markup.ToArray());
        }
        return table;
    }

    /// <summary>Zapis CSV: separator ';', przecinek dziesiętny, UTF-8 z BOM – otwiera się wprost w polskim Excelu.</summary>
    public static void WriteCsv(string path, string header, IEnumerable<IEnumerable<object?>> rows)
    {
        var sb = new StringBuilder(header).Append('\n');
        foreach (var row in rows)
            sb.AppendJoin(';', row.Select(Cell)).Append('\n');
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        AnsiConsole.MarkupLine($"[grey]Zapisano:[/] {Markup.Escape(path)}");
    }

    private static string Cell(object? value) => value switch
    {
        null => "",
        double d => double.IsFinite(d) ? d.ToString("0.######", Pl) : "",
        bool b => b ? "1" : "0",
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString()!.Replace(';', ','),
    };
}
