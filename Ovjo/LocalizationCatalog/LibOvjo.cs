using NGettext;

namespace Ovjo.LocalizationCatalog
{
    internal static class LibOvjo
    {
        private static readonly ICatalog _catalog;

        static LibOvjo()
        {
            _catalog = LocalizationCatalogHelper.CreateCatalog("lib_ovjo");
        }

        internal static string _(string text, params object[] args)
        {
            return _catalog.GetString(text, args);
        }
    }
}
