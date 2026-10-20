using System.Text.Json.Serialization;
using System.Text.Json;
using SimpleBikeTrainer.Ble;
using SimpleBikeTrainer.Recording;
using SimpleBikeTrainer.Workouts;

namespace SimpleBikeTrainer;

/// <summary>
/// Source-generated JSON for the files the app writes, so trimmed and native AOT builds read and write them without
/// reflection. Enums are stored by camel-case name.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    Converters =
    [
        typeof(CamelCaseEnumConverter<ThemeChoice>),
        typeof(CamelCaseEnumConverter<UnitSystem>),
        typeof(CamelCaseEnumConverter<ResistanceMode>),
        typeof(CamelCaseEnumConverter<DeviceKind>),
        typeof(CamelCaseEnumConverter<DeviceTransport>),
        typeof(CamelCaseEnumConverter<Devices.MetricSource>),
    ])]
[JsonSerializable(typeof(AppConfig))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;

/// <inheritdoc cref="ConfigJsonContext"/>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(CamelCaseEnumConverter<PowerUnit>)])]
[JsonSerializable(typeof(RideInfo))]
internal sealed partial class RideInfoJsonContext : JsonSerializerContext;

internal sealed class CamelCaseEnumConverter<TEnum>() : JsonStringEnumConverter<TEnum>(JsonNamingPolicy.CamelCase)
    where TEnum : struct, Enum;
