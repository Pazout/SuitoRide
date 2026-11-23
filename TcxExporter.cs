using System.IO;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SuitoRide;

internal static class TcxExporter
{
    public static byte[] Create(RideRecord ride)
    {
        if(ride.DurationSeconds<=0 || ride.DistanceMeters<0 || !double.IsFinite(ride.DurationSeconds) || !double.IsFinite(ride.DistanceMeters))
            throw new InvalidDataException(UiText.Get("Jízda nemá platný čas nebo vzdálenost."));
        XNamespace tcx="http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2";
        XNamespace ext="http://www.garmin.com/xmlschemas/ActivityExtension/v2";
        string Number(double value)=>XmlConvert.ToString(value);
        string Time(double seconds)=>ride.StartedAt.ToUniversalTime().AddSeconds(seconds).ToString("yyyy-MM-ddTHH:mm:ss.fffZ",CultureInfo.InvariantCulture);
        var track=new XElement(tcx+"Track");
        void AddPoint(double seconds, double distance, RidePosition? position, double? cadence,
            double? power, double? speed, int? heartRate = null)
        {
            var point = new XElement(tcx + "Trackpoint", new XElement(tcx + "Time", Time(seconds)));
            if (position is { Latitude: >= -90 and <= 90, Longitude: >= -180 and <= 180 })
            {
                point.Add(new XElement(tcx + "Position",
                    new XElement(tcx + "LatitudeDegrees", Number(position.Latitude)),
                    new XElement(tcx + "LongitudeDegrees", Number(position.Longitude))));
                if (double.IsFinite(position.AltitudeMeters))
                    point.Add(new XElement(tcx + "AltitudeMeters", Number(position.AltitudeMeters)));
            }
            point.Add(new XElement(tcx + "DistanceMeters", Number(distance)));
            if(heartRate is >0 and <=255)
                point.Add(new XElement(tcx+"HeartRateBpm",new XElement(tcx+"Value",heartRate.Value)));
            if(cadence is >=0 and <=254 && double.IsFinite(cadence.Value))
                point.Add(new XElement(tcx+"Cadence",(int)Math.Round(cadence.Value)));
            var tpx=new XElement(ext+"TPX");
            if(speed is >=0 && double.IsFinite(speed.Value))tpx.Add(new XElement(ext+"Speed",Number(speed.Value/3.6)));
            if(power is >=0 && double.IsFinite(power.Value))tpx.Add(new XElement(ext+"Watts",(int)Math.Round(power.Value)));
            if(tpx.HasElements)point.Add(new XElement(tcx+"Extensions",tpx));
            track.Add(point);
        }
        AddPoint(0, 0, ride.StartPosition, null, null, null);
        double previous=0;
        foreach(var sample in ride.Samples ?? Array.Empty<RideSample>())
        {
            if(!double.IsFinite(sample.ElapsedSeconds) || !double.IsFinite(sample.DistanceMeters))continue;
            double t=Math.Round(Math.Clamp(sample.ElapsedSeconds,0,ride.DurationSeconds),3);
            if(t<=previous || t>=ride.DurationSeconds)continue;
            AddPoint(t, Math.Clamp(sample.DistanceMeters, 0, ride.DistanceMeters), sample.Position,
                sample.CadenceRpm, sample.PowerWatts, sample.SpeedKmh, sample.HeartRateBpm);
            previous=t;
        }
        AddPoint(ride.DurationSeconds, ride.DistanceMeters, ride.EndPosition, null, null, null);
        var lap=new XElement(tcx+"Lap",new XAttribute("StartTime",Time(0)),
            new XElement(tcx+"TotalTimeSeconds",Number(ride.DurationSeconds)),
            new XElement(tcx+"DistanceMeters",Number(ride.DistanceMeters)),
            new XElement(tcx+"Calories",0),new XElement(tcx+"Intensity","Active"),
            new XElement(tcx+"TriggerMethod","Manual"),track);
        var doc=new XDocument(new XDeclaration("1.0","utf-8",null),
            new XElement(tcx+"TrainingCenterDatabase",
                new XAttribute(XNamespace.Xmlns+"ns3",ext),
                new XElement(tcx+"Activities",new XElement(tcx+"Activity",new XAttribute("Sport","Biking"),
                    new XElement(tcx+"Id",Time(0)),lap))));
        using var stream=new MemoryStream();
        using(var writer=XmlWriter.Create(stream,new XmlWriterSettings{Encoding=new UTF8Encoding(false),Indent=true}))doc.Save(writer);
        return stream.ToArray();
    }
}
