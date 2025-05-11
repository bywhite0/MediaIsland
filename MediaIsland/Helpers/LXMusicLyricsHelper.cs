using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MediaIsland.Models;

namespace MediaIsland.Helpers
{
    public class LXMusicLyricsHelper
    {
        PluginSettings Settings { get; set; }
        private readonly HttpClient _httpClient = new HttpClient();
        private string LXMusicAPIUrl;
        private readonly string LyricsIslandAPIUrl = "http://127.0.0.1:50063/component/lyrics/lyrics/";
        private const int MaxFailures = 3;
        public ILogger<LXMusicLyricsHelper>? logger;

        public LXMusicLyricsHelper(PluginSettings settings)
        {
            Settings = settings;
            LXMusicAPIUrl = $"http://127.0.0.1:{Settings.LXMusicAPIPort}/subscribe-player-status?filter=lyricLineAllText";
            Task.Run(LyricsForwarderAsync);
        }
        public async Task LyricsForwarderAsync()
        {
            int failureCount = 0;

            while (failureCount < MaxFailures)
            {
                try
                {
                    logger?.LogInformation("连接 LX Music API...");

                    var request = new HttpRequestMessage(HttpMethod.Get, LXMusicAPIUrl);
                    request.Headers.Add("Accept", "text/event-stream");

                    using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();

                    using var stream = await response.Content.ReadAsStreamAsync();
                    using var reader = new StreamReader(stream);

                    logger?.LogInformation("连接成功");
                    failureCount = 0; // 重置失败计数

                    while (!reader.EndOfStream)
                    {
                        var line = await reader.ReadLineAsync();
                        if (line == null) continue;

                        if (line.StartsWith("data:"))
                        {
                            var data = line.Substring(5).Trim().Replace("\"", "");
                            var json = JsonSerializer.Serialize(new { lyric = data, extra = "" });
                            if (data.Contains("\\n"))
                            {
                                var ExtraData = data.Split("\\n");
                                json = JsonSerializer.Serialize(new { lyric = ExtraData[0], extra = ExtraData[1] });
                            }
                            var content = new StringContent(json, Encoding.UTF8, "application/json");
                            await _httpClient.PostAsync(LyricsIslandAPIUrl, content);
                        }
                    }
                }
                catch (Exception ex)
                {
                    failureCount++;
                    logger?.LogError($"连接 LX Music API 失败[{failureCount}/{MaxFailures}]：{ex.Message}");

                    if (failureCount >= MaxFailures)
                    {
                        logger?.LogError("已达到最大失败次数，停止连接。");
                        break;
                    }

                    logger?.LogInformation("正在重试...");
                    await Task.Delay(5000);
                }
            }

            logger?.LogInformation("LX Music API 连接已断开。");
        }
    }
}