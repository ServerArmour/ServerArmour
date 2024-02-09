using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Plugins;

namespace Oxide.Plugins
{
    [Info("Discord Api", "Pho3niX90", "0.0.2")]
    class DiscordApi : CovalencePlugin
    {
        #region Classes
        public class FancyMessage
        {
            [JsonProperty("content")] private string Content { get; set; }

            [JsonProperty("embeds")] private EmbedBuilder[] Embeds { get; set; }

            public FancyMessage WithContent(string value) {
                Content = value;
                return this;
            }

            public FancyMessage SetEmbed(EmbedBuilder value) {
                Embeds = new[]
                {
                    value
                };
                return this;
            }

            public string ToJson() {
                return JsonConvert.SerializeObject(this, _instance._jsonSettings);
            }
        }

        public class EmbedBuilder
        {
            public EmbedBuilder() {
                Fields = new List<Field>();
            }

            [JsonProperty("title")] private string Title { get; set; }

            [JsonProperty("color")] private int Color { get; set; }

            [JsonProperty("fields")] private List<Field> Fields { get; }

            public EmbedBuilder WithTitle(string title) {
                Title = title;
                return this;
            }

            public EmbedBuilder SetColor(int color) {
                Color = color;
                return this;
            }

            public EmbedBuilder AddField(Field field) {
                Fields.Add(field);
                return this;
            }

            internal class Field
            {
                public Field(string name, object value, bool inline) {
                    Name = name;
                    Value = value;
                    Inline = inline;
                }

                [JsonProperty("name")] public string Name { get; set; }

                [JsonProperty("value")] public object Value { get; set; }

                [JsonProperty("inline")] public bool Inline { get; set; }
            }
        }

        private abstract class Response
        {
            public int Code { get; set; }
            public string Message { get; set; }
        }

        private class BaseResponse : Response
        {
            public bool IsRatelimit => Code == 429;
            public bool IsOk => (Code == 200) | (Code == 204);
            public bool IsBad => !IsRatelimit && !IsOk;

            public RateLimitResponse GetRateLimit() {
                return JsonConvert.DeserializeObject<RateLimitResponse>(Message);
            }
        }

        private class Request
        {
            private static readonly RateLimitHandler handler = new RateLimitHandler();

            private readonly string _payload;
            private readonly Plugin _plugin;
            private readonly Action<BaseResponse> _response;
            private readonly string _url;


            public void Send() {
                _instance.webrequest.Enqueue(_url, _payload, (code, rawResponse) => {
                    var response = new BaseResponse {
                        Message = rawResponse,
                        Code = code
                    };
                    if (response.IsRatelimit) handler.AddMessage(response.GetRateLimit(), this);
                    if (response.IsBad) _instance.PrintWarning("Failed! Discord responded with code: {0}. Plugin: {1}\n{2}", code, _plugin != null ? _plugin.Name : "Unknown Plugin", response.Message);
                    try {
                        _response?.Invoke(response);
                    } catch (Exception ex) {
                        Interface.Oxide.LogException("[DiscordApi] Request callback raised an exception!", ex);
                    }
                }, _instance, RequestMethod.POST, _instance._headers);
            }
            public static void Send(string url, FancyMessage message, Plugin plugin = null) {
                new Request(url, message, plugin).Send();
            }

            private Request(string url, FancyMessage message, Plugin plugin = null) {
                _url = url;
                _payload = message.ToJson();
                _plugin = plugin;
            }

            public float NextTime { get; private set; }

            public Request SetNextTime(float time) {
                NextTime = time;
                return this;
            }
        }

        private class RateLimitHandler
        {
            private Queue<Request> Messages { get; } = new Queue<Request>();

            public void AddMessage(RateLimitResponse response, Request request) {
                request.SetNextTime(response.RetryAfter / 1000f);
                Messages.Enqueue(request);
                RateTimerHandler();
            }

            private void RateTimerHandler() {
                while (true) {
                    if (Messages.Count == 0) return;

                    var request = Messages.Dequeue();
                    _instance.timer.Once(request.NextTime, () => request.Send());
                }
            }
        }

        private class RateLimitResponse : BaseResponse
        {
            [JsonProperty("global")] public bool Global { get; set; }

            [JsonProperty("retry_after")] public int RetryAfter { get; set; }
        }
        #endregion

        #region Variables
        private static DiscordApi _instance;
        private readonly JsonSerializerSettings _jsonSettings = new JsonSerializerSettings();

        private readonly Dictionary<string, string> _headers = new Dictionary<string, string> {
            ["Content-Type"] = "application/json"
        };
        #endregion

        #region Hooks / Load

        private void Init() {
            _instance = this;
            _jsonSettings.NullValueHandling = NullValueHandling.Ignore;
        }

        private void Unload() {
            _instance = null;
        }

        #endregion

        #region API
        private void API_SendEmbeddedMessage(string webhookUrl, string embedName, int embedColor, string json, string content = null, Plugin plugin = null) {
            var builder = new EmbedBuilder()
                .WithTitle(embedName)
                .SetColor(embedColor);
            foreach (var field in JsonConvert.DeserializeObject<EmbedBuilder.Field[]>(json)) builder.AddField(field);
            var payload = new FancyMessage()
                .SetEmbed(builder)
                .WithContent(content);
            Request.Send(webhookUrl, payload, plugin);
        }
        #endregion
    }
}