namespace Earthquake.Checker
{
    using HtmlAgilityPack;
    using Microsoft.Azure.Devices.Client;
    using Microsoft.Azure.Devices.Shared;
    using Microsoft.Azure.Devices.Provisioning.Client;
    using Microsoft.Azure.Devices.Provisioning.Client.Transport;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Text.Json;
    using System.Linq;
    using System.Net.Http;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Newtonsoft.Json;


    public class Worker : BackgroundService
    {
        private readonly IHostApplicationLifetime _appLifetime;
        private readonly ILogger<Worker> _logger;
        private readonly IOptions<WorkerSettings> _settings;
        private readonly IConfiguration _configuration;

        private readonly string ScopeID = string.Empty;
        private readonly string DeviceID = string.Empty;
        private readonly string PrimaryKey = string.Empty;
        private readonly string GlobalDeviceEndpoint = string.Empty;
        private DeviceClient _deviceClient = null;
        private TwinCollection _reportProperties = new TwinCollection();
        private List<EarthquakeData> _data;
        public Worker(ILogger<Worker> logger, IOptions<WorkerSettings> settings, IHostApplicationLifetime appLifetime, IConfiguration configuration)
        {
            _appLifetime = appLifetime;
            _settings = settings;
            _logger = logger;
            _configuration = configuration;

            DeviceID = _settings.Value.DeviceID;
            ScopeID = _settings.Value.ScopeID;
            PrimaryKey = _settings.Value.PrimaryKey;
            GlobalDeviceEndpoint = _settings.Value.GlobalDeviceEndpoint;

            _data = new List<EarthquakeData>();

        }
        private async Task<bool> Init()
        {
            try
            {
                using (var security = new SecurityProviderSymmetricKey(DeviceID, PrimaryKey, null))
                {
                    DeviceRegistrationResult result = await RegisterDeviceAsync(security);
                    if (result.Status != ProvisioningRegistrationStatusType.Assigned)
                    {
                        _logger.LogInformation("{time} - Failed to register device", DateTimeOffset.Now);

                        return false;
                    }
                    IAuthenticationMethod auth = new DeviceAuthenticationWithRegistrySymmetricKey(result.DeviceId, (security as SecurityProviderSymmetricKey).GetPrimaryKey());
                    _deviceClient = DeviceClient.Create(result.AssignedHub, auth, TransportType.Mqtt);
                }

                await SendDevicePropertiesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError($"{DateTimeOffset.Now} - Error:{ex.Message}");
                return false;
            }

            return true;
        }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _appLifetime.ApplicationStarted.Register(OnStarted);
            _appLifetime.ApplicationStopping.Register(OnStopping);
            _appLifetime.ApplicationStopped.Register(OnStopped);

            var isInitialized = await Init();
            if (!isInitialized)
            {
                _appLifetime.StopApplication();
            }
            try
            {
                Random rand = new Random();

                while (!stoppingToken.IsCancellationRequested)
                {
                    var client = new HttpClient();
                    var doc = new HtmlDocument();
                    var dataParts = new Regex(@$"{_settings.Value.RegularExpression}");


                    HttpResponseMessage result = await client.GetAsync(@$"{_settings.Value.URL}");

                    if (!result.IsSuccessStatusCode) continue; //Re-try

                    string content = await result.Content.ReadAsStringAsync();

                    if (string.IsNullOrEmpty(content)) continue; //Re-try

                    doc.LoadHtml(content);

                    //Get <pre> tag from HTML document
                    HtmlNode contentData = doc.DocumentNode.SelectSingleNode($"{_settings.Value.HtmlParseTag}");

                    if (contentData != null)
                    {
                        //Remove some unnecessary data
                        int start = contentData.InnerText.IndexOf("-------", 0);
                        string[] lines = contentData.InnerText.Substring(start).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

                        /**
                        Content of lines is like, so need the fetch data.
                        
                            2019.09.27 16:35:47  40.4215   26.0910       11.7 -.- 4.1  3.9   SAROS KORFEZI(EGE DENIZI)                        İlksel
                         
                        Process the line with RegularExpression pattern and map it to a DTO.

                         */
                        foreach (var item in lines.Skip(1))//Skip first line
                        {
                            Match match = dataParts.Match(item);
                            if (match.Success && match.Groups.Count == 10)
                            {
                                var data = new EarthquakeData
                                {
                                    Date = DateTime.Parse(match.Groups[1].Value.Trim()),
                                    Time = TimeSpan.Parse(match.Groups[2].Value.Trim()),
                                    Latitude = double.Parse(match.Groups[3].Value.Trim()),
                                    Longitude = double.Parse(match.Groups[4].Value.Trim()),
                                    Depth = double.Parse(match.Groups[5].Value.Trim()),
                                    Magnitude = double.Parse(match.Groups[7].Value.Trim()),
                                    Place = match.Groups[9].Value.Trim()
                                };

                                //Check if data is already added into a data repository
                                //TODO: Make a storage like DB
                                EarthquakeData lastEarthquake = _data.FirstOrDefault();
                                if (lastEarthquake != null && (lastEarthquake.Date == data.Date && lastEarthquake.Time == data.Time))
                                {
                                    _logger.LogInformation("{time} - No new earthquake", DateTimeOffset.Now);
                                    break;
                                }
                                else
                                {
                                    //Add data into a data repository
                                    _data.Add(data);

                                    //If there is commandline argument as --alert 5 as magnitude, display a warning message
                                    double alert = _configuration.GetValue<double>("alert");

                                    if (alert != 0 && data.Magnitude >= alert)
                                    {
                                        _logger.LogInformation($"{DateTimeOffset.Now} - !!!WARNING!!! EARTHQUAKE - {data.Magnitude} at {data.Place}");
                                    }

                                    var telemetryData = new
                                    {
                                        latitude = data.Latitude,
                                        longitude = data.Longitude,
                                        magnitude = data.Magnitude
                                    };

                                    var telemetryDataString = JsonConvert.SerializeObject(telemetryData);
                                    var telemetryMessage = new Message(Encoding.ASCII.GetBytes(telemetryDataString));
                                    await _deviceClient.SendEventAsync(telemetryMessage);
                                    _logger.LogInformation("{time} - Event data is sent.", DateTimeOffset.Now);
                                }
                            }
                            else
                            {
                                //Structure is changed
                                _logger.LogInformation("{time} - Data structure might be changed. Please check.", DateTimeOffset.Now);
                                _appLifetime.StopApplication();
                            }
                        }

                    }

                    //Wait a little bit for next check
                    await Task.Delay(_settings.Value.Period, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogInformation("{time} - Error: {time}", DateTimeOffset.Now);
                _logger.LogInformation(ex.Message);

            }


        }

        private void OnStopped()
        {
            _logger.LogInformation("{time} - Worker stopped.", DateTimeOffset.Now);
            //Do some post-work
        }

        private void OnStopping()
        {
            //Some resource can be cleaned
        }

        private void OnStarted()
        {
            _logger.LogInformation("{time} - Worker started.", DateTimeOffset.Now);
            //Do some pre-work
        }

        private async Task<DeviceRegistrationResult> RegisterDeviceAsync(SecurityProviderSymmetricKey security)
        {
            _logger.LogInformation("{time} - Register device...", DateTimeOffset.Now);

            using (var transport = new ProvisioningTransportHandlerMqtt(TransportFallbackType.TcpOnly))
            {
                ProvisioningDeviceClient provClient = ProvisioningDeviceClient.Create(GlobalDeviceEndpoint, ScopeID, security, transport);
                _logger.LogInformation($"{DateTimeOffset.Now} - RegistrationID = {security.GetRegistrationID()}");

                DeviceRegistrationResult result = await provClient.RegisterAsync();

                _logger.LogInformation($"{DateTimeOffset.Now} - ProvisioningClient AssignedHub: {result.AssignedHub}; DeviceID: {result.DeviceId}");

                return result;
            }
        }

        private async Task SendDevicePropertiesAsync()
        {
            _logger.LogInformation("{time} - Send device properties...", DateTimeOffset.Now);

            Random random = new Random();
            var telemetryConfig = new TwinCollection();
            _reportProperties["dieNumber"] = random.Next(1, 6);

            await _deviceClient.UpdateReportedPropertiesAsync(_reportProperties);
        }
    }
}
