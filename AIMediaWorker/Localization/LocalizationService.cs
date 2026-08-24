using AIMediaWorker.Settings;
using Microsoft.Windows.Globalization;
using System.Globalization;
using System.Linq;
using Windows.ApplicationModel.Resources.Core;
using Windows.System.UserProfile;

namespace AIMediaWorker.Localization;

public static class LocalizationService
{
    public static string ToLanguageTag(AppLanguage language)
    {
        if (language == AppLanguage.Default)
        {
            var systemLanguage = GetSystemLanguage();
            return systemLanguage.StartsWith("ko", StringComparison.OrdinalIgnoreCase) ? "ko-KR"
                : systemLanguage.StartsWith("ja", StringComparison.OrdinalIgnoreCase) ? "ja-JP"
                : "en-US";
        }
        return language switch { AppLanguage.Korean => "ko-KR", AppLanguage.Japanese => "ja-JP", _ => "en-US" };
    }

    public static void Apply(AppLanguage language)
    {
        var tag = ToLanguageTag(language);
        ApplicationLanguages.PrimaryLanguageOverride = tag;
        ResourceContext.SetGlobalQualifierValue("Language", tag);
    }

    public static string Get(string key)
    {
        var resourcePath = key.Replace('.', '/');
        var value = ResourceManager.Current.MainResourceMap.GetValue($"Resources/{resourcePath}", ResourceContext.GetForViewIndependentUse());
        return value?.ValueAsString ?? key;
    }

    private static string GetSystemLanguage()
    {
        try
        {
            return GlobalizationPreferences.Languages.FirstOrDefault() ?? CultureInfo.CurrentUICulture.Name;
        }
        catch
        {
            return CultureInfo.CurrentUICulture.Name;
        }
    }
}
