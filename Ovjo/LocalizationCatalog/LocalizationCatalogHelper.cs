using NGettext;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace Ovjo.LocalizationCatalog
{
    internal static class LocalizationCatalogHelper
    {
        public static ICatalog CreateCatalog(string catalogName)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var uiCulture = new CultureInfo(Environment.GetEnvironmentVariable("OVJO_LOCALE") ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
            var resourceName = $"Ovjo.locales.{uiCulture.TwoLetterISOLanguageName}.LC_MESSAGES.{catalogName}.mo";

            var stream = assembly.GetManifestResourceStream(resourceName);

            if (stream == null)
            {
                return new Catalog();
            }
            return new Catalog(stream);
        }
    }
}
