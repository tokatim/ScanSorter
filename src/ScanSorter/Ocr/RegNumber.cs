using System.Text.RegularExpressions;

namespace ScanSorter.Ocr;

/// <summary>Реестровый номер: год-том-номер. Год может быть неизвестен (старые документы вида «№ 1-154»).</summary>
public sealed record RegNumber(int? Year, string Vol, int Num)
{
    public string Key => $"{Year}-{Vol.ToLowerInvariant()}-{Num}";
    public override string ToString() =>
        Vol.Length == 0 ? (Year.HasValue ? $"{Year}-{Num}" : Num.ToString())
        : Year.HasValue ? $"{Year}-{Vol}-{Num}" : $"{Vol}-{Num}";
}

/// <summary>Разбор текста вокруг слов «Зарегистрировано в реестре: № …».</summary>
public static class RegNumberParser
{
    // «№» OCR часто распознаёт как N, No, Ng, М, W.
    private static readonly Regex Marker = new(@"(?=(?:№|N[o°ое]?|No|Ng|М|W)\s*[:.]?\s*(.{3,60}))", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex NewFormat = new(@"((?:1|2|I|l|\|)[0-9OОoо]{3})\s*-\s*([0-9A-Za-zА-Яа-я]{1,3})\s*-\s*([0-9OОoоlI|]{1,5})(?![0-9A-Za-zА-Яа-я])", RegexOptions.Compiled);
    private static readonly Regex OldFormat = new(@"^[\s:.№]*([0-9A-Za-zА-Яа-я]{1,3})\s*-\s*([0-9OОoоlI|]{1,5})(?![0-9A-Za-zА-Яа-я])", RegexOptions.Compiled);
    // мягкий разбор для нейросети: после номера допускаются любые буквы (мусор от соседнего текста), но не цифры
    private static readonly Regex NewFormatLenient = new(@"((?:1|2|I|l|\|)[0-9OОoо]{3})\s*-\s*([0-9A-Za-zА-Яа-я]{1,3})\s*-\s*([0-9OОoоlI|]{1,5})(?![0-9])", RegexOptions.Compiled);
    private static readonly Regex OldFormatLenient = new(@"^[\s:.№]*([0-9A-Za-zА-Яа-я]{1,3})\s*-\s*([0-9OОoоlI|]{1,5})(?![0-9])", RegexOptions.Compiled);
    // старые документы: «Зарегистрировано в реестре за N 23.» (только номер, без тома)
    private static readonly Regex NoVolume = new(@"^[\s:.]*([0-9OОoоlI|]{1,5})(?![0-9A-Za-zА-Яа-я])", RegexOptions.Compiled);
    private static readonly Regex NoVolumeLenient = new(@"^[\s:.]*([0-9OОoоlI|]{1,5})(?![0-9])", RegexOptions.Compiled);
    private static readonly Regex VolumeForm = new(@"^[\s:.]*\d+\s*-\s*\d", RegexOptions.Compiled);
    // старые документы с буквой: «за N В-37» (буква вместо тома)
    private static readonly Regex NoVolumeLetter = new(@"^[\s:.]*([A-Za-zА-Яа-я]{1,3})\s*-\s*([0-9OОoоlI|]{1,5})(?![0-9A-Za-zА-Яа-я])", RegexOptions.Compiled);
    private static readonly Regex NoVolumeLetterLenient = new(@"^[\s:.]*([A-Za-zА-Яа-я]{1,3})\s*-\s*([0-9OОoоlI|]{1,5})(?![0-9])", RegexOptions.Compiled);
    // поле «Номер» в окне проверки: «37», «В-37», «В37», «1-437»
    private static readonly Regex NumberField = new(@"^(?:(?<v>[0-9A-Za-zА-Яа-я]{1,3})\s*-\s*|(?<v2>[A-Za-zА-Яа-я]{1,3}))?(?<n>\d{1,5})$", RegexOptions.Compiled);
    private static readonly Regex HasYear = new(@"(?:19|20)\d\d", RegexOptions.Compiled);

    private const string Dashes = "‐‑‒–—―−~_";

    /// <summary>Латинские буквы, неотличимые от русских («B» и «В»), заменяются русскими: иначе один и тот же номер читается как два разных.</summary>
    public static string NormalizeVol(string vol)
    {
        const string latin = "ABCEHKMOPTXYabceopxy";
        const string cyr = "АВСЕНКМОРТХУабсеорху";
        return new string(vol.Select(ch => { int i = latin.IndexOf(ch); return i >= 0 ? cyr[i] : ch; }).ToArray());
    }

    public static string NormalizeDigits(string s)
    {
        var map = new Dictionary<char, char>
        {
            ['O'] = '0', ['О'] = '0', ['o'] = '0', ['о'] = '0', ['l'] = '1', ['I'] = '1', ['|'] = '1', ['!'] = '1',
            ['З'] = '3', ['з'] = '3', ['Б'] = '6', ['S'] = '5', ['Ѕ'] = '5', ['В'] = '8', ['B'] = '8',
        };
        return new string(s.Select(ch => map.TryGetValue(ch, out var r) ? r : ch).ToArray());
    }

    public static RegNumber? Parse(string text, bool lenient = false)
    {
        var t = text;
        foreach (var d in Dashes) t = t.Replace(d, '-');
        // идём с последнего маркера «№»: так не путаем с «по реестру № 3-2060» выше по тексту
        var segs = Marker.Matches(t).Select(m => m.Groups[1].Value.Trim()).Reverse().ToList();
        if (segs.Count == 0) segs.Add(t.Trim());

        foreach (var seg in segs)
        {
            var n = (lenient ? NewFormatLenient : NewFormat).Match(seg);
            if (n.Success)
            {
                var year = NormalizeDigits(n.Groups[1].Value);
                if (Regex.IsMatch(year, @"^(19|20)\d\d$") && int.TryParse(NormalizeDigits(n.Groups[3].Value), out var num))
                    return new RegNumber(int.Parse(year), NormalizeVol(n.Groups[2].Value), num);
            }
            if (!seg.Contains('/') && !HasYear.IsMatch(seg))
            {
                var o = (lenient ? OldFormatLenient : OldFormat).Match(seg);
                if (o.Success && int.TryParse(NormalizeDigits(o.Groups[2].Value), out var num))
                    return new RegNumber(null, NormalizeVol(o.Groups[1].Value), num);
            }
        }
        return null;
    }

    /// <summary>Разбор для старых документов без тома: после последнего «№/N» берётся только номер. Запись вида «1-437» здесь не принимается.</summary>
    public static RegNumber? ParseNoVolume(string text, bool lenient = false)
    {
        var t = text;
        foreach (var d in Dashes) t = t.Replace(d, '-');
        var segs = Marker.Matches(t).Select(m => m.Groups[1].Value.Trim()).Reverse().ToList();
        if (segs.Count == 0) segs.Add(t.Trim());
        foreach (var seg in segs)
        {
            if (VolumeForm.IsMatch(seg)) continue;
            var lt = (lenient ? NoVolumeLetterLenient : NoVolumeLetter).Match(seg);          // «В-37»: буква вместо тома
            if (lt.Success && int.TryParse(NormalizeDigits(lt.Groups[2].Value), out var lnum))
                return new RegNumber(null, NormalizeVol(lt.Groups[1].Value), lnum);
            var m = (lenient ? NoVolumeLenient : NoVolume).Match(seg);
            if (m.Success && int.TryParse(NormalizeDigits(m.Groups[1].Value), out var num))
                return new RegNumber(null, "", num);
        }
        return null;
    }

    /// <summary>
    /// Поле «Номер», введённое человеком: цифры, либо буква(ы) и тире перед цифрами. «37» → (пусто, 37), «В-37» → («В», 37), «1-437» → («1», 437).
    /// </summary>
    public static bool TryParseNumberField(string text, out string vol, out int num)
    {
        vol = ""; num = 0;
        var t = text.Trim();
        foreach (var d in Dashes) t = t.Replace(d, '-');
        var m = NumberField.Match(t);
        if (!m.Success || !int.TryParse(m.Groups["n"].Value, out num) || num < 1) return false;
        vol = NormalizeVol(m.Groups["v"].Success ? m.Groups["v"].Value : m.Groups["v2"].Success ? m.Groups["v2"].Value : "");
        return true;
    }

    /// <summary>Правдоподобие номера: отсекает явный мусор.</summary>
    public static bool IsPlausible(RegNumber n)
    {
        if (n.Year is int y && (y < 1990 || y > DateTime.Now.Year + 1)) return false;
        if (n.Vol.Length > 3) return false;     // пустой том допустим: старые документы без тома
        return n.Num is >= 1 and <= 99999;
    }
}
