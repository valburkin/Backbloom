namespace Backbloom.Core.Mapping;

/// <summary>How frame pixels become LED colors (the <c>color.imageToLedMappingType</c> setting).</summary>
public enum LedMappingType
{
    /// <summary>"advanced": each LED averages its own area in linear light (C++ value 0, multicolor mean).</summary>
    Advanced = 0,

    /// <summary>"unicolor_mean": every LED shows the average of the whole frame (C++ value 1).</summary>
    UnicolorMean = 1,
}

public static class LedMappingTypes
{
    /// <summary>Port of <c>ImageToLedManager::mappingTypeToInt</c>: anything but "unicolor_mean" is advanced.</summary>
    public static LedMappingType Parse(string? value) =>
        value == "unicolor_mean" ? LedMappingType.UnicolorMean : LedMappingType.Advanced;

    /// <summary>Port of <c>ImageToLedManager::mappingTypeToStr</c>.</summary>
    public static string ToSettingString(this LedMappingType type) =>
        type == LedMappingType.UnicolorMean ? "unicolor_mean" : "advanced";
}
