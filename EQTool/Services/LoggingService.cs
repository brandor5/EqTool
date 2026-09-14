using EQToolShared.Enums;
using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace EQTool.Services
{
    public class LoggingService
    {
        private readonly HttpClient httpclient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        public void Log(string message, EventType eventType, Servers? server)
        {
            _ = Task.Run(async () =>
            {
                var build = BuildType.Release;
#if TEST
            return;
#elif DEBUG
                build = BuildType.Debug;
#elif BETA
            build = BuildType.Beta; 
#endif
                try
                {
                    var msg = new App.ExceptionRequest
                    {
                        Version = App.Version,
                        Message = message,
                        EventType = eventType,
                        BuildType = build,
                        Server = server
                    };
                    var msagasjson = Newtonsoft.Json.JsonConvert.SerializeObject(msg);
                    var content = new StringContent(msagasjson, Encoding.UTF8, "application/json");
                    var result = await httpclient.PostAsync("https://pigparse.azurewebsites.net/api/eqtool/exception", content).ConfigureAwait(false);
                }
                catch { }
            });
        }
    }
}
