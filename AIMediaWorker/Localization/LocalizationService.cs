using AIMediaWorker.Settings;
using Windows.ApplicationModel.Resources.Core;

namespace AIMediaWorker.Localization;

public static class LocalizationService
{
    public static string ToLanguageTag(AppLanguage language) => language switch { AppLanguage.Korean => "ko-KR", AppLanguage.Japanese => "ja-JP", _ => "en-US" };

    public static void Apply(AppLanguage language)
    {
        var tag = ToLanguageTag(language);
        ResourceContext.SetGlobalQualifierValue("Language", tag);
    }

    public static string Get(string key)
    {
        var resourcePath = key.Replace('.', '/');
        var value = ResourceManager.Current.MainResourceMap.GetValue($"Resources/{resourcePath}", ResourceContext.GetForViewIndependentUse());
        return value?.ValueAsString ?? key;
    }
}
