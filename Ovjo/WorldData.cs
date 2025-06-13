namespace Ovjo
{
    public class WorldData
    {
        public string? FilePath;

        private Dictionary<string, string>? _plainFiles;
        public Dictionary<string, string> PlainFiles
        {
            get
            {
                if (_plainFiles != null)
                {
                    return _plainFiles;
                }
                _plainFiles = new Dictionary<string, string>();
                if (FilePath != null)
                {

                }
                return _plainFiles;
            }
            set
            {

            }
        }

        public WorldData() { }

        public WorldData(string path)
        {
            this.FilePath = path;
        }
    }
}
