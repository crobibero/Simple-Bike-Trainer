using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer;

public enum UnitSystem
{
    Metric,
    Imperial,
}

/// <summary>
/// Display units. Everything is stored and recorded in metric (the config, the ride loop, FIT files); these convert
/// for showing and for typing in values.
/// </summary>
public static class Units
{
    public const double MetersPerMile = 1609.344;
    public const double KgPerPound = 0.45359237;
    public const double MetersPerFoot = 0.3048;

    public static string DistanceUnit(this UnitSystem units) => units == UnitSystem.Imperial ? CoreStrings.Unit_Mi : CoreStrings.Unit_Km;

    public static string SpeedUnit(this UnitSystem units) => units == UnitSystem.Imperial ? CoreStrings.Unit_Mph : CoreStrings.Unit_Kmh;

    public static string WeightUnit(this UnitSystem units) => units == UnitSystem.Imperial ? CoreStrings.Unit_Lb : CoreStrings.Unit_Kg;

    public static string ElevationUnit(this UnitSystem units) => units == UnitSystem.Imperial ? CoreStrings.Unit_Ft : CoreStrings.Unit_M;

    public static double Elevation(this UnitSystem units, double meters) =>
        units == UnitSystem.Imperial ? meters / MetersPerFoot : meters;

    public static double ElevationToMeters(this UnitSystem units, double value) =>
        units == UnitSystem.Imperial ? value * MetersPerFoot : value;

    public static double DistanceToMeters(this UnitSystem units, double value) =>
        units == UnitSystem.Imperial ? value * MetersPerMile : value * 1000;

    /// <summary>"412 m" or "1352 ft".</summary>
    public static string FormatElevation(this UnitSystem units, double meters) =>
        Loc.Format(CoreStrings.Unit_Quantity, units.Elevation(meters).ToString("0", System.Globalization.CultureInfo.CurrentCulture), units.ElevationUnit());

    public static double Distance(this UnitSystem units, double meters) =>
        units == UnitSystem.Imperial ? meters / MetersPerMile : meters / 1000;

    public static double Speed(this UnitSystem units, double metersPerSecond) =>
        units == UnitSystem.Imperial ? metersPerSecond * 3600 / MetersPerMile : metersPerSecond * 3.6;

    public static double Weight(this UnitSystem units, double kg) =>
        units == UnitSystem.Imperial ? kg / KgPerPound : kg;

    public static double WeightToKg(this UnitSystem units, double value) =>
        units == UnitSystem.Imperial ? value * KgPerPound : value;

    /// <summary>"12.34 km" or "7.67 mi".</summary>
    public static string FormatDistance(this UnitSystem units, double meters, string format = "0.00") =>
        Loc.Format(CoreStrings.Unit_Quantity, units.Distance(meters).ToString(format, System.Globalization.CultureInfo.CurrentCulture), units.DistanceUnit());

    /// <summary>"31.4 km/h" or "19.5 mph".</summary>
    public static string FormatSpeed(this UnitSystem units, double metersPerSecond) =>
        Loc.Format(CoreStrings.Unit_Quantity, units.Speed(metersPerSecond).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture), units.SpeedUnit());

    /// <summary>"80 kg" or "176.4 lb".</summary>
    public static string FormatWeight(this UnitSystem units, double kg) =>
        Loc.Format(CoreStrings.Unit_Quantity, units.Weight(kg).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture), units.WeightUnit());
}
