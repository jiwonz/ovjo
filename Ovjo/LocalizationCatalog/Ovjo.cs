using NGettext;

namespace Ovjo.LocalizationCatalog
{
    internal static class Ovjo
    {
        private static readonly ICatalog _catalog;

        static Ovjo()
        {
            _catalog = LocalizationCatalogHelper.CreateCatalog("ovjo");
        }

        internal static string _(string text, params object[] args)
        {
            return _catalog.GetString(text, args);
        }
    }
}
