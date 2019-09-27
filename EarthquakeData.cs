namespace Earthquake.Checker
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    public class EarthquakeData
    {
        public DateTime Date { get; set; }
        public TimeSpan Time { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double Depth { get; set; }
        public double Magnitude { get; set; }
        public string Place { get; set; }

    }
}
