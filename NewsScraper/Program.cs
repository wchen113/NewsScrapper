using Microsoft.Extensions.Configuration;
using NewsAPI;
using NewsAPI.Constants;
using NewsAPI.Models;
using OpenAI_API;
using OpenAI_API.Completions;
using RestSharp;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NewsScraper
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // Load configuration
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            var newsApiKey = configuration["ApiKeys:NewsApiKey"];
            var openAiApiKey = configuration["ApiKeys:OpenAiApiKey"];
            var googleGeocodingApiKey = configuration["ApiKeys:GoogleGeocodingApiKey"];

            var newsApiSettings = configuration.GetSection("NewsApiSettings").Get<NewsApiSettings>();
            var apiUrls = configuration.GetSection("ApiUrls").Get<ApiUrls>();

            var newsApiClient = new NewsApiClient(newsApiKey);
            var openAiClient = new OpenAIAPI(openAiApiKey);

            // Load category keywords
            var categoryKeywords = await GetCategoryKeywordsAsync(apiUrls.CategoryKeywordsUrl);

            // Process the keywords
            await ProcessKeywordsAsync(categoryKeywords, newsApiClient, openAiClient, googleGeocodingApiKey, newsApiSettings, apiUrls.ArticlesUrl);
        }

        static async Task<List<CategoryKeyword>> GetCategoryKeywordsAsync(string url)
        {
            var client = new HttpClient();
            try
            {
                var response = await client.GetStringAsync(url);

                if (string.IsNullOrWhiteSpace(response))
                {
                    Console.WriteLine("No response received from API.");
                    return new List<CategoryKeyword>();
                }

                var jsonDocument = JsonDocument.Parse(response);
                var categoryKeywords = new List<CategoryKeyword>();

                if (jsonDocument.RootElement.TryGetProperty("$values", out var values))
                {
                    categoryKeywords = values.EnumerateArray()
                        .Select(e => new CategoryKeyword
                        {
                            Id = e.GetProperty("id").GetInt32(),
                            Category = e.GetProperty("category").GetString(),
                            Keyword = e.GetProperty("keyword").GetString()
                        })
                        .ToList();
                }

                return categoryKeywords;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception while getting category keywords: {ex.Message}");
                return new List<CategoryKeyword>();
            }
        }

        static async Task ProcessKeywordsAsync(IEnumerable<CategoryKeyword> categoryKeywords, NewsApiClient newsApiClient, OpenAIAPI openAiClient, string googleGeocodingApiKey, NewsApiSettings settings, string articlesUrl)
        {
            var stopwatchRecords = new List<StopwatchRecord>();

            foreach (var category in categoryKeywords)
            {
                var categoryKeyword = category.Category;

                var searchStopwatch = Stopwatch.StartNew();
                var request = new EverythingRequest
                {
                    Q = categoryKeyword,
                    From = DateTime.Now.AddDays(-settings.DaysRange),
                    To = DateTime.Now,
                    SortBy = settings.SortBy.Equals("publishedAt", StringComparison.OrdinalIgnoreCase) ? SortBys.PublishedAt : SortBys.Relevancy,
                    Language = settings.Language.Equals("en", StringComparison.OrdinalIgnoreCase) ? Languages.EN : null
                };

                try
                {
                    var response = await newsApiClient.GetEverythingAsync(request);
                    searchStopwatch.Stop();
                    stopwatchRecords.Add(new StopwatchRecord
                    {
                        TaskName = $"Search for keyword '{categoryKeyword}'",
                        ElapsedMilliseconds = searchStopwatch.ElapsedMilliseconds
                    });

                    if (response.Status == Statuses.Ok && response.TotalResults > 0)
                    {
                        foreach (var article in response.Articles)
                        {
                            await ProcessArticleAsync(article, category.Category, openAiClient, googleGeocodingApiKey, stopwatchRecords, articlesUrl);
                        }
                    }
                    else
                    {
                        Console.WriteLine($"No articles found for keyword '{categoryKeyword}'.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Exception while processing keyword '{categoryKeyword}': {ex.Message}");
                }
            }

            SaveStopwatchRecordsToCsv(stopwatchRecords);
        }


        static async Task ProcessArticleAsync(Article article, string category, OpenAIAPI openAiClient, string googleGeocodingApiKey, List<StopwatchRecord> stopwatchRecords, string articlesUrl)
        {
            var articleStopwatch = Stopwatch.StartNew();

            try
            {
                var title = article.Title ?? "No Title";
                var description = RemoveHtmlTags(article.Description ?? "No Description");
                var url = article.Url ?? "No URL";
                var publishedAt = article.PublishedAt?.ToString("yyyy-MM-dd") ?? "Unknown";

                var location = await ExtractLocationFromArticle(openAiClient, description);
                var (latitude, longitude) = await GetLatLongFromLocation(location, googleGeocodingApiKey);

                if (latitude == "Unknown" || longitude == "Unknown")
                {
                    articleStopwatch.Stop();
                    stopwatchRecords.Add(new StopwatchRecord
                    {
                        TaskName = $"Skipping article '{title}' due to unknown location",
                        ElapsedMilliseconds = articleStopwatch.ElapsedMilliseconds
                    });
                    return;
                }

                var articleText = await GetArticleText(openAiClient, description);
                var summary = await SummarizeArticleText(openAiClient, articleText);
                var source = article.Source?.Name ?? "Unknown";
                var urlImage = article.UrlToImage ?? "No Image";
                var createdAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ssZ");

                var articleData = new
                {
                    Created_At = createdAt,
                    Title = title,
                    Text = summary,
                    Location = location,
                    Lat = decimal.TryParse(latitude, out decimal lat) ? Math.Round(lat, 5) : 0.00000m,
                    Lng = decimal.TryParse(longitude, out decimal lng) ? Math.Round(lng, 5) : 0.00000m,
                    DisruptionType = category,
                    Severity = description,
                    SourceName = source,
                    PublishedDate = publishedAt,
                    Url = url,
                    ImageUrl = urlImage,
                    Radius = 10000,
                    Article = new { Title = title, Description = description, Url = url }
                };

                var httpClient = new HttpClient();
                var responseMessage = await httpClient.PostAsJsonAsync(articlesUrl, articleData);

                if (responseMessage.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Article '{title}' saved successfully.");
                }
                else
                {
                    var responseContent = await responseMessage.Content.ReadAsStringAsync();
                    Console.WriteLine($"Failed to save article '{title}': {responseMessage.ReasonPhrase} - {responseContent}");
                }

                articleStopwatch.Stop();
                stopwatchRecords.Add(new StopwatchRecord
                {
                    TaskName = $"Processing article '{title}'",
                    ElapsedMilliseconds = articleStopwatch.ElapsedMilliseconds
                });
            }
            catch (Exception ex)
            {
                articleStopwatch.Stop();
                stopwatchRecords.Add(new StopwatchRecord
                {
                    TaskName = $"Exception while processing article '{article.Title}': {ex.Message}",
                    ElapsedMilliseconds = articleStopwatch.ElapsedMilliseconds
                });
            }
        }

        static async Task<string> ExtractLocationFromArticle(OpenAIAPI client, string articleContent)
        {
            var prompt = $"Extract the location mentioned in the following article: {articleContent}";

            var completionRequest = new CompletionRequest
            {
                Prompt = prompt,
                MaxTokens = 50,
                Temperature = 0.7,
                TopP = 1.0
            };

            var completionResult = await client.Completions.CreateCompletionAsync(completionRequest);

            var location = completionResult.Completions.FirstOrDefault()?.Text.Trim();
            return location;
        }

        static async Task<(string, string)> GetLatLongFromLocation(string location, string apiKey)
        {
            if (string.IsNullOrEmpty(location))
            {
                Console.WriteLine("Location is null or empty.");
                return ("Unknown", "Unknown");
            }

            try
            {
                var client = new RestClient("https://maps.googleapis.com/maps/api/geocode/json");
                var request = new RestRequest("", Method.Get)
                    .AddQueryParameter("address", location)
                    .AddQueryParameter("key", apiKey);

                var response = await client.ExecuteAsync(request);

                if (response.IsSuccessful)
                {
                    var jsonDocument = JsonDocument.Parse(response.Content);
                    if (jsonDocument.RootElement.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
                    {
                        var locationElement = results[0].GetProperty("geometry").GetProperty("location");
                        var lat = locationElement.GetProperty("lat").ToString();
                        var lng = locationElement.GetProperty("lng").ToString();
                        return (lat, lng);
                    }
                    else
                    {
                        Console.WriteLine($"No results found for location '{location}'.");
                        return ("Unknown", "Unknown");
                    }
                }
                else
                {
                    Console.WriteLine($"Failed to get coordinates for location '{location}': {response.ErrorMessage}");
                    return ("Unknown", "Unknown");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception while getting coordinates for location '{location}': {ex.Message}");
                return ("Unknown", "Unknown");
            }
        }

        static async Task<string> GetArticleText(OpenAIAPI client, string description)
        {
            var prompt = $"Extract the main content of the article from this description: {description}";

            var completionRequest = new CompletionRequest
            {
                Prompt = prompt,
                MaxTokens = 2000,
                Temperature = 0.7,
                TopP = 1.0
            };

            var completionResult = await client.Completions.CreateCompletionAsync(completionRequest);

            var articleText = completionResult.Completions.FirstOrDefault()?.Text.Trim();
            return articleText;
        }

        static async Task<string> SummarizeArticleText(OpenAIAPI client, string articleText)
        {
            var prompt = $"Summarize the following article: {articleText}";

            var completionRequest = new CompletionRequest
            {
                Prompt = prompt,
                MaxTokens = 100,
                Temperature = 0.7,
                TopP = 1.0
            };

            var completionResult = await client.Completions.CreateCompletionAsync(completionRequest);

            var summary = completionResult.Completions.FirstOrDefault()?.Text.Trim();
            return summary;
        }

        static string RemoveHtmlTags(string input)
        {
            return Regex.Replace(input, "<.*?>", string.Empty);
        }

        static void SaveStopwatchRecordsToCsv(List<StopwatchRecord> records)
        {
            var filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "StopwatchRecords.txt");
            var lines = records.Select(r => $"{r.TaskName},{r.ElapsedMilliseconds}");

            File.WriteAllLines(filePath, lines);
        }

        class StopwatchRecord
        {
            public string TaskName { get; set; }
            public long ElapsedMilliseconds { get; set; }
        }

        class CategoryKeyword
        {
            public int Id { get; set; }
            public string Category { get; set; }
            public string Keyword { get; set; }
        }

        class NewsApiSettings
        {
            public string Language { get; set; }
            public string SortBy { get; set; }
            public int DaysRange { get; set; }
        }

        class ApiUrls
        {
            public string CategoryKeywordsUrl { get; set; }
            public string ArticlesUrl { get; set; }
        }
    }
}
