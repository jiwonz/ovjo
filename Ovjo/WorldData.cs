namespace Ovjo
{
    public class WorldData
    {
        private Dictionary<string, string>? _plainFiles;
        public Dictionary<string, string> PlainFiles
        {
            get
            {
                if (_plainFiles == null)
                {

                }
            }
        }

        public WorldData() { }

        public WorldData(string path)
        {

        }
    }
}
