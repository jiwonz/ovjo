using Newtonsoft.Json.Linq;
using System.Text;

namespace Ovjo
{
    public class WorldData
    {
        public const string MapName = "Map";
        public const string PlainFilesName = "PlainFiles";
        public const string JsonFilesName = "JsonFiles";

        public string? DirectoryPath;

        public string[] PlainFiles = [];

        public WorldData() { }

        // Get from directory
        public WorldData(string path)
        {
            this.DirectoryPath = path;
        }

        public void Write(string path)
        {

        }

        public static WorldData? FromRojoProject(JObject rojoProject)
        {

        }

        public static WorldData FromOverdareWorld(string path)
        {
            return new()
            {
                PlainFiles = Directory.GetFiles(path)
                    .Where(file =>
                        !Path.GetExtension(file).Equals(".json", StringComparison.OrdinalIgnoreCase) &&
                        !Path.GetExtension(file).Equals(".umap", StringComparison.OrdinalIgnoreCase))
                    .ToArray()
            };
        }

        private static byte[] FilesToMessagePackBinaryString(string[] files)
        {
            Dictionary<string, byte[]> filesData = new();
            foreach (string p in files)
            {
                byte[] content = File.ReadAllBytes(p);
                if (Path.GetExtension(p).Equals(".json", StringComparison.OrdinalIgnoreCase))
                {
                    content = MessagePack.MessagePackSerializer.ConvertFromJson(Encoding.UTF8.GetString(content));
                }
                filesData.Add(Path.GetFileNameWithoutExtension(p), content);
            }
            return MessagePack.MessagePackSerializer.Serialize(filesData);
        }
    }
}
