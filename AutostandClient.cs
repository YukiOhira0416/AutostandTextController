using System;

namespace Autostand
{
    public class AutostandClient : IDisposable
    {
        public static string DefaultBaseUrl { get; } = "https://api.autostand.example.com/v1/";

        public AutostandClient(string apiKey, string baseUrl, double timeoutSec)
        {
        }

        public StandInfo Up(int standId, double timeoutSec)
        {
            throw new NotImplementedException("AutostandClient.Up is not implemented");
        }

        public StandInfo Down(int standId, double timeoutSec)
        {
            throw new NotImplementedException("AutostandClient.Down is not implemented");
        }

        public StandInfo CheckStatus(int standId, double timeoutSec)
        {
            throw new NotImplementedException("AutostandClient.CheckStatus is not implemented");
        }

        public BatteryLevel? CheckBattery(int standId, double timeoutSec)
        {
            throw new NotImplementedException("AutostandClient.CheckBattery is not implemented");
        }

        public void Dispose()
        {
        }
    }

    public class StandInfo
    {
        public int Id { get; set; }
        public string Operate { get; set; }
        public string ArmState { get; set; }
        public string StandState { get; set; }
        public BatteryLevel? Battery { get; set; }
        public bool? UltrasonicDetected { get; set; }
    }

    public enum BatteryLevel
    {
        Level0 = 0,
        Level10 = 10,
        Level20 = 20,
        Level30 = 30,
        Level40 = 40,
        Level50 = 50,
        Level60 = 60,
        Level70 = 70,
        Level80 = 80,
        Level90 = 90,
        Level100 = 100
    }

    public class AutoStandApiError : Exception
    {
        public string Code { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public DateTime? ResponseDate { get; set; }

        public AutoStandApiError(string message) : base(message)
        {
        }
    }

    public class AutoStandHttpError : Exception
    {
        public int StatusCode { get; set; }
        public object Body { get; set; }

        public AutoStandHttpError(string message) : base(message)
        {
        }
    }
}
