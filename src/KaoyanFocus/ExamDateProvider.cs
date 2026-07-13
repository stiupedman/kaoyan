using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace KaoyanFocus;

public sealed record ExamDateResult(DateOnly Date, string Url);

public sealed partial class ExamDateProvider(HttpClient http)
{
    private const string ListingUrl = "https://www.moe.gov.cn/jyb_xwfb/gzdt_gzdt/s5987/";

    [GeneratedRegex("href=[\"'](?<url>[^\"']+)[\"'][^>]*>.*?全国硕士研究生考试招生工作", RegexOptions.Singleline)]
    private static partial Regex AnnouncementLink();

    [GeneratedRegex("初试时间.{0,12}?(?<year>20\\d{2})年(?<month>\\d{1,2})月(?<day>\\d{1,2})日", RegexOptions.Singleline)]
    private static partial Regex ExamDateText();

    public static DateOnly? TryParseDate(string html, DateOnly today)
    {
        var text = WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", ""));
        var match = ExamDateText().Match(text);
        if (!match.Success)
            return null;

        var date = new DateOnly(
            int.Parse(match.Groups["year"].Value),
            int.Parse(match.Groups["month"].Value),
            int.Parse(match.Groups["day"].Value));
        return date >= today ? date : null;
    }

    public async Task<ExamDateResult?> FetchAsync(
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var listing = await http.GetStringAsync(ListingUrl, cancellationToken);
        var match = AnnouncementLink().Match(listing);
        if (!match.Success)
            return null;

        if (!Uri.TryCreate(new Uri(ListingUrl), match.Groups["url"].Value, out var candidate)
            || candidate.Scheme != Uri.UriSchemeHttps
            || !candidate.IsDefaultPort
            || !candidate.Host.Equals("www.moe.gov.cn", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var url = candidate.ToString();
        var html = await http.GetStringAsync(url, cancellationToken);
        var date = TryParseDate(html, today);
        return date is null ? null : new ExamDateResult(date.Value, url);
    }
}
