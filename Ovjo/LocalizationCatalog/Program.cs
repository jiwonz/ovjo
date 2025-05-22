using NGettext;

namespace Ovjo.LocalizationCatalog
{
    internal static class Program
    {
        private static readonly ICatalog _catalog;

        static Program()
        {
            _catalog = LocalizationCatalogHelper.CreateCatalog("program");
        }

        internal static string _(string text, params object[] args)
        {
            return _catalog.GetString(text, args);
        }
    }
}
