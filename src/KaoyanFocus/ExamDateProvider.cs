using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace KaoyanFocus;

public sealed record ExamDateResult(DateOnly Date, string Url);

public sealed partial class ExamDateProvider : IDisposable
{
    private const string ListingUrl = "https://www.moe.gov.cn/jyb_xwfb/gzdt_gzdt/s5987/";
    private readonly HttpClient http;

    public ExamDateProvider()
        : this(new HttpClientHandler { AllowAutoRedirect = false })
    {
    }

    public ExamDateProvider(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        http = new HttpClient(handler, disposeHandler: true);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("KaoyanFocus/1.0");
    }

    [GeneratedRegex("<a\\b[^>]*\\bhref=[\"'](?<url>[^\"']+)[\"'][^>]*>(?:(?!</a\\s*>).)*?全国硕士研究生考试招生工作", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex AnnouncementLink();

    [GeneratedRegex("初试时间(?:为|：|:|\\s)+(?<year>20\\d{2})年(?<month>\\d{1,2})月(?<day>\\d{1,2})日")]
    private static partial Regex ExamDateText();

    public static DateOnly? TryParseDate(string html, DateOnly today)
    {
        var text = WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", ""));
        var match = ExamDateText().Match(text);
        if (!match.Success)
            return null;

        if (!int.TryParse(match.Groups["year"].Value, out var year)
            || !int.TryParse(match.Groups["month"].Value, out var month)
            || !int.TryParse(match.Groups["day"].Value, out var day)
            || month is < 1 or > 12
            || day < 1
            || day > DateTime.DaysInMonth(year, month))
        {
            return null;
        }

        var date = new DateOnly(year, month, day);
        return date >= today ? date : null;
    }

    public async Task<ExamDateResult?> FetchAsync(
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var listing = await GetStringIfSuccessAsync(ListingUrl, cancellationToken);
        if (listing is null)
            return null;

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
        var html = await GetStringIfSuccessAsync(url, cancellationToken);
        if (html is null)
            return null;

        var date = TryParseDate(html, today);
        return date is null ? null : new ExamDateResult(date.Value, url);
    }

    public void Dispose() => http.Dispose();

    private async Task<string?> GetStringIfSuccessAsync(
        string url,
        CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}
