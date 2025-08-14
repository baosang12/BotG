using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Globalization;

namespace DataFetcher.News
{
    public class NewsEvent
    {
        public string Impact { get; set; }
        public DateTime Date { get; set; }
        public string Title { get; set; }
    }

    public class NewsFetcher
    {
        private readonly string _apiKey = "7cc373846f6f4e9:c1bari9ios933r0";
        private readonly string _endpoint = "https://calendar-api.fxstreet.com/v1/events";

        public async Task<List<NewsEvent>> FetchNewsAsync(DateTime from, DateTime to)
        {
            var newsList = new List<NewsEvent>();
            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
                    var url = $"{_endpoint}?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}";
                    var response = await client.GetAsync(url);
                    response.EnsureSuccessStatusCode();
                    var json = await response.Content.ReadAsStringAsync();
                    newsList = ParseNewsEventsFromJson(json);
                }
            }
            catch (Exception ex)
            {
                // Log error or handle gracefully
                System.Console.WriteLine($"Error fetching news: {ex.Message}");
            }
            return newsList;
        }

        private List<NewsEvent> ParseNewsEventsFromJson(string json)
        {
            var newsList = new List<NewsEvent>();
            try
            {
                // Regex-based parsing (simple, for cTrader compatibility)
                var eventPattern = @"\{[^}]*""impact""\s*:\s*""([^""]*)""|[^}]*""date""\s*:\s*""([^""]*)""|[^}]*""title""\s*:\s*""([^""]*)""[^}]*\}";
                var matches = Regex.Matches(json, eventPattern);
                foreach (Match match in matches)
                {
                    var impact = match.Groups[1].Value;
                    var dateStr = match.Groups[2].Value;
                    var title = match.Groups[3].Value;
                    DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date);
                    newsList.Add(new NewsEvent { Impact = impact, Date = date, Title = title });
                }
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"Error parsing news: {ex.Message}");
            }
            return newsList;
        }
    }
}
