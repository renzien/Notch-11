using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DynamicIsland.Media;

public class LyricItem
{
    public TimeSpan Time { get; set; }
    public string Text { get; set; } = string.Empty;
}

public class LyricsResult
{
    public bool Found { get; set; }
    public List<LyricItem> SyncedLyrics { get; set; } = new();
    public List<string> PlainLyrics { get; set; } = new();
}

public class LyricsService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, LyricsResult> _cache = new();
    private CancellationTokenSource? _currentCts;

    public event Action<string, LyricsResult>? LyricsLoaded;

    public LyricsService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(3.5)
        };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "DynamicIsland-Windows11/1.0");
    }

    public async Task FetchLyricsAsync(string title, string artist, double durationSeconds = 0)
    {
        if (string.IsNullOrWhiteSpace(title)) return;

        string cacheKey = $"{artist.Trim()} - {title.Trim()}".ToLowerInvariant();
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            LyricsLoaded?.Invoke(cacheKey, cached);
            return;
        }

        _currentCts?.Cancel();
        _currentCts = new CancellationTokenSource();
        var token = _currentCts.Token;

        _ = Task.Run(async () =>
        {
            var result = new LyricsResult();
            try
            {
                string cleanTitle = CleanTrackTitle(title);
                string cleanArtist = CleanArtistName(artist);

                // 1. Coba endpoint exact match /api/get
                string getUrl = $"https://lrclib.net/api/get?artist_name={Uri.EscapeDataString(cleanArtist)}&track_name={Uri.EscapeDataString(cleanTitle)}";
                if (durationSeconds > 5)
                {
                    getUrl += $"&duration={Math.Round(durationSeconds)}";
                }

                var response = await _httpClient.GetAsync(getUrl, token);
                if (response.IsSuccessStatusCode)
                {
                    string json = await response.Content.ReadAsStringAsync(token);
                    using var doc = JsonDocument.Parse(json);
                    ExtractFromElement(doc.RootElement, result);
                }

                // 2. Jika belum dapat syncedLyrics, fallback ke /api/search agar lebih akurat
                if (result.SyncedLyrics.Count == 0)
                {
                    string searchUrl = $"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(cleanTitle)}&artist_name={Uri.EscapeDataString(cleanArtist)}";
                    var searchResp = await _httpClient.GetAsync(searchUrl, token);
                    if (searchResp.IsSuccessStatusCode)
                    {
                        string searchJson = await searchResp.Content.ReadAsStringAsync(token);
                        using var searchDoc = JsonDocument.Parse(searchJson);
                        if (searchDoc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            JsonElement? bestMatch = null;
                            double bestDiff = double.MaxValue;

                            foreach (var item in searchDoc.RootElement.EnumerateArray())
                            {
                                bool hasSynced = item.TryGetProperty("syncedLyrics", out var sp) &&
                                                 sp.ValueKind == JsonValueKind.String &&
                                                 !string.IsNullOrWhiteSpace(sp.GetString());
                                if (!hasSynced) continue;

                                double itemDur = 0;
                                if (item.TryGetProperty("duration", out var dp) && dp.ValueKind == JsonValueKind.Number)
                                {
                                    itemDur = dp.GetDouble();
                                }

                                double diff = durationSeconds > 5 && itemDur > 0
                                    ? Math.Abs(itemDur - durationSeconds)
                                    : 0;

                                if (diff < bestDiff)
                                {
                                    bestDiff = diff;
                                    bestMatch = item;
                                }

                                if (durationSeconds <= 5) break;
                            }

                            if (bestMatch.HasValue)
                            {
                                ExtractFromElement(bestMatch.Value, result);
                            }
                            else if (!result.Found && searchDoc.RootElement.GetArrayLength() > 0)
                            {
                                ExtractFromElement(searchDoc.RootElement[0], result);
                            }
                        }
                    }
                }
            }
            catch
            {
                // Fallback saat offline / timeout / tidak ditemukan
            }

            if (!result.Found)
            {
                result.PlainLyrics = new List<string> { "♪ Instrumental / Lirik tidak ditemukan ♪" };
            }

            _cache[cacheKey] = result;

            if (!token.IsCancellationRequested)
            {
                LyricsLoaded?.Invoke(cacheKey, result);
            }
        }, token);
    }

    private static void ExtractFromElement(JsonElement root, LyricsResult result)
    {
        if (root.TryGetProperty("syncedLyrics", out var syncedProp) && syncedProp.ValueKind == JsonValueKind.String)
        {
            string syncedStr = syncedProp.GetString() ?? string.Empty;
            var parsed = ParseSyncedLyrics(syncedStr);
            if (parsed.Count > 0)
            {
                result.SyncedLyrics = parsed;
            }
        }

        if (result.PlainLyrics.Count == 0 && root.TryGetProperty("plainLyrics", out var plainProp) && plainProp.ValueKind == JsonValueKind.String)
        {
            string plainStr = plainProp.GetString() ?? string.Empty;
            var parsedPlain = ParsePlainLyrics(plainStr);
            if (parsedPlain.Count > 0)
            {
                result.PlainLyrics = parsedPlain;
            }
        }

        result.Found = result.SyncedLyrics.Count > 0 || result.PlainLyrics.Count > 0;
    }

    private static string CleanTrackTitle(string title)
    {
        // Hilangkan (feat. ...), (Remastered...), [Official Video], dll.
        string clean = Regex.Replace(title, @"\s*(\(|\[)(feat\.|ft\.|remaster|official|audio|video|live).*?(\)|\])", "", RegexOptions.IgnoreCase);
        // Hilangkan suffix dash jika ada ' - Remastered'
        int dashIdx = clean.IndexOf(" - ", StringComparison.Ordinal);
        if (dashIdx > 0)
        {
            clean = clean.Substring(0, dashIdx);
        }
        return clean.Trim();
    }

    private static string CleanArtistName(string artist)
    {
        // Ambil artis pertama jika ada koma atau feat
        int commaIdx = artist.IndexOfAny(new[] { ',', '&', ';' });
        if (commaIdx > 0)
        {
            return artist.Substring(0, commaIdx).Trim();
        }
        return artist.Trim();
    }

    private static List<LyricItem> ParseSyncedLyrics(string lrc)
    {
        var list = new List<LyricItem>();
        var regex = new Regex(@"\[(\d{1,2}):(\d{2})(?:[\.:](\d{1,3}))?\](.*)");

        var lines = lrc.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var match = regex.Match(line);
            if (match.Success)
            {
                int min = int.Parse(match.Groups[1].Value);
                int sec = int.Parse(match.Groups[2].Value);
                int ms = 0;
                if (match.Groups[3].Success && !string.IsNullOrEmpty(match.Groups[3].Value))
                {
                    ms = int.Parse(match.Groups[3].Value.PadRight(3, '0').Substring(0, 3));
                }

                string text = match.Groups[4].Value.Trim();
                if (string.IsNullOrEmpty(text))
                {
                    text = "♪ • • •";
                }

                list.Add(new LyricItem
                {
                    Time = new TimeSpan(0, 0, min, sec, ms),
                    Text = text
                });
            }
        }

        list.Sort((a, b) => a.Time.CompareTo(b.Time));
        return list;
    }

    private static List<string> ParsePlainLyrics(string plain)
    {
        var lines = plain.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var list = new List<string>();
        foreach (var line in lines)
        {
            string t = line.Trim();
            if (!string.IsNullOrEmpty(t)) list.Add(t);
        }
        return list;
    }

    public void Dispose()
    {
        _currentCts?.Cancel();
        _httpClient.Dispose();
    }
}
