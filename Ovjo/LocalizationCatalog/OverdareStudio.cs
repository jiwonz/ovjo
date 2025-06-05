using NGettext;

namespace Ovjo.LocalizationCatalog
{
    internal static class OverdareStudio
    {
        private static readonly ICatalog _catalog;

        static OverdareStudio()
        {
            _catalog = LocalizationCatalogHelper.CreateCatalog("lib_ovjo");
        }

        internal static string _(string text, params object[] args)
        {
            return _catalog.GetString(text, args);
        }
    }
}
