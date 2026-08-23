using AIMediaWorker.Settings;
using Windows.ApplicationModel.Resources.Core;
using Windows.Globalization;

namespace AIMediaWorker.Localization;

public static class LocalizationService
{
    public static string ToLanguageTag(AppLanguage language) => language switch { AppLanguage.Korean => "ko-KR", AppLanguage.Japanese => "ja-JP", _ => "en-US" };

    public static void Apply(AppLanguage language)
    {
        var tag = ToLanguageTag(language);
        ApplicationLanguages.PrimaryLanguageOverride = tag;
        ResourceContext.SetGlobalQualifierValue("Language", tag);
    }

    public static string Get(string key)
    {
        var value = ResourceManager.Current.MainResourceMap.GetValue($"Resources/{key}", ResourceContext.GetForViewIndependentUse());
        return value?.ValueAsString ?? key;
    }
}
